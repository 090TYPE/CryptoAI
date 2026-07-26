using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// The follower turns someone else's trade feed into real market orders on the user's own account,
/// so every guard between the feed and <c>PlaceOrderAsync</c> is money: the start watermark that
/// stops the leader's back catalogue from being replayed at today's price, the once-only rule, the
/// default-deny execution guard, and the venue's size rules.
/// </summary>
public class CopyTradingFollowerServiceTests
{
    // ── a leader endpoint on loopback ────────────────────────────────────────
    // The service holds a private static HttpClient, so there is no handler to swap: the only way
    // to exercise the polling path end to end is to answer a real request. A raw socket keeps that
    // free of HTTP.sys url-acl requirements.
    private sealed class LeaderStub : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<string> _body;
        private int _requests;

        public string BaseUrl { get; }
        /// <summary>Requests answered so far. Poll N+1 can only start once poll N has fully finished.</summary>
        public int Requests => Volatile.Read(ref _requests);

        public LeaderStub(Func<string> body)
        {
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var payload = Encoding.UTF8.GetBytes(_body());
                Interlocked.Increment(ref _requests);

                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                // Graceful FIN rather than the abortive reset a bare Dispose can send.
                try { client.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    private sealed class FakeGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;

        private readonly object _lock = new();
        private readonly List<Order> _orders = [];
        public IReadOnlyList<Order> Orders { get { lock (_lock) return _orders.ToArray(); } }

        public SymbolFilters? Filters;
        public decimal Balance = 10_000m;
        public bool PlaceThrows;

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order order)
        {
            lock (_lock) _orders.Add(order);
            if (PlaceThrows) return Task.FromException<Order>(new InvalidOperationException("insufficient balance"));
            order.Status = OrderStatus.Filled;
            return Task.FromResult(order);
        }
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(Balance);
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult<OrderBook>(null!);
        public Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
            => Task.FromResult(Filters);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CopyTrade Trade(string id, string symbol, decimal qty, decimal price, DateTime executedUtc,
        string side = "BUY")
        => new()
        {
            Id = id, Symbol = symbol, Side = side, Quantity = qty, Price = price,
            ExecutedUtc = executedUtc, Exchange = "binance", Source = "AIBot",
        };

    private static string Json(params CopyTrade[] trades) => JsonSerializer.Serialize(trades);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 4_000; i++)
        {
            if (condition()) return;
            await Task.Delay(3);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Runs the follower against <paramref name="body"/> until <paramref name="until"/> holds.</summary>
    private static async Task RunAsync(
        FakeGateway gw,
        Func<string> body,
        Func<bool> until,
        string what,
        Action<CopyTradingFollowerService>? configure = null,
        ConcurrentQueue<string>? logs = null,
        ConcurrentQueue<CopyExecution>? executions = null)
    {
        using var stub = new LeaderStub(body);
        var svc = new CopyTradingFollowerService
        {
            LeaderUrl = stub.BaseUrl,
            Gateway = gw,
            // Every test but the guard test speaks for the workspace that said yes.
            LiveExecutionGuard = _ => null,
        };
        if (logs is not null) svc.LogMessage += logs.Enqueue;
        if (executions is not null) svc.ExecutionCompleted += executions.Enqueue;
        configure?.Invoke(svc);

        svc.Start();
        try { await WaitForAsync(until, what); }
        finally { await svc.StopAsync(); }
    }

    private static bool Any(ConcurrentQueue<string> logs, string fragment)
        => logs.ToArray().Any(l => l.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unwired_follower_refuses_to_mirror_anything()
    {
        // The service has no paper mode: an instance nobody attached a workspace guard to would
        // otherwise send real market orders the moment a leader url is typed in.
        var gw = new FakeGateway();
        ConcurrentQueue<string> logs = new();

        using var stub = new LeaderStub(() => Json(Trade("t1", "BTCUSDT", 1m, 100m, DateTime.UtcNow)));
        var svc = new CopyTradingFollowerService { LeaderUrl = stub.BaseUrl, Gateway = gw };
        svc.LogMessage += logs.Enqueue;

        svc.Start();
        try { await WaitForAsync(() => Any(logs, "refusing to mirror real orders"), "the default guard to refuse"); }
        finally { await svc.StopAsync(); }

        Assert.Empty(gw.Orders);
    }

    [Fact]
    public async Task The_leaders_back_catalogue_is_not_replayed_and_no_trade_is_mirrored_twice()
    {
        // The leader publishes up to 100 past trades. Without the start watermark the first poll
        // fired every one of them into the market at today's price; without the executed-id set
        // the same trade came back on every 2-second poll.
        var gw = new FakeGateway();
        var stub = new LeaderStub(() => Json(
            Trade("fresh", "ETHUSDT", 1m, 100m, DateTime.UtcNow),
            Trade("history", "BTCUSDT", 1m, 100m, DateTime.UtcNow.AddMinutes(-5))));

        using (stub)
        {
            var svc = new CopyTradingFollowerService
            {
                LeaderUrl = stub.BaseUrl, Gateway = gw, LiveExecutionGuard = _ => null,
            };
            svc.Start();
            // A second answered request proves the first poll ran to completion — both trades in it
            // have been decided on, and the re-published pair has been offered a second time.
            try { await WaitForAsync(() => stub.Requests >= 2, "a second poll"); }
            finally { await svc.StopAsync(); }
        }

        var order = Assert.Single(gw.Orders);
        Assert.Equal("ETHUSDT", order.Symbol);   // the five-minute-old trade never reached the venue
    }

    [Fact]
    public async Task Quantity_is_scaled_and_floored_onto_the_venues_lot_step()
    {
        var gw = new FakeGateway { Filters = new SymbolFilters("BTCUSDT", 0.01m, 0.001m, 0.01m, 10m) };
        await RunAsync(gw,
            () => Json(Trade("t1", "BTCUSDT", 1.2345m, 100m, DateTime.UtcNow)),
            () => gw.Orders.Count > 0, "the mirrored order",
            svc => svc.ScaleRatio = 0.5m);

        var order = Assert.Single(gw.Orders);
        // 1.2345 × 0.5 = 0.61725, floored onto the 0.001 step. Rounding up would order more than
        // the follower's balance covers.
        Assert.Equal(0.617m, order.Quantity);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderType.Market, order.Type);
    }

    [Fact]
    public async Task Without_venue_filters_the_quantity_still_gets_a_lot_step()
    {
        var gw = new FakeGateway();   // gateway cannot answer GetSymbolFiltersAsync
        await RunAsync(gw,
            () => Json(Trade("t1", "BTCUSDT", 1.2345678m, 100m, DateTime.UtcNow)),
            () => gw.Orders.Count > 0, "the mirrored order",
            svc => svc.ScaleRatio = 0.5m);

        Assert.Equal(0.617283m, Assert.Single(gw.Orders).Quantity);
    }

    [Fact]
    public async Task A_scaled_quantity_under_the_venue_minimum_is_dropped_rather_than_rounded_up()
    {
        var gw = new FakeGateway { Filters = new SymbolFilters("BTCUSDT", 0.01m, 0.001m, 1m, 10m) };
        ConcurrentQueue<string> logs = new();

        await RunAsync(gw,
            () => Json(Trade("t1", "BTCUSDT", 1m, 100m, DateTime.UtcNow)),
            () => Any(logs, "too small"), "the size rejection",
            svc => svc.ScaleRatio = 0.1m,
            logs);

        Assert.Empty(gw.Orders);
    }

    [Fact]
    public async Task An_order_under_the_venues_notional_floor_is_dropped()
    {
        var gw = new FakeGateway { Filters = new SymbolFilters("BTCUSDT", 0.01m, 0.001m, 0m, 25m) };
        ConcurrentQueue<string> logs = new();

        await RunAsync(gw,
            () => Json(Trade("t1", "BTCUSDT", 0.1m, 100m, DateTime.UtcNow)),   // 10 USD < 25
            () => Any(logs, "below the 25"), "the notional rejection",
            logs: logs);

        Assert.Empty(gw.Orders);
    }

    [Fact]
    public async Task The_followers_own_risk_limits_apply_even_though_the_leader_chose_the_size()
    {
        var gw = new FakeGateway();
        ConcurrentQueue<string> logs = new();

        await RunAsync(gw,
            () => Json(Trade("t1", "BTCUSDT", 1m, 100m, DateTime.UtcNow)),
            () => Any(logs, "risk gate"), "the risk rejection",
            svc => svc.Risk.UpdateLimits(maxPositionSizeUsd: 10m, maxDailyLossUsd: 500m),
            logs);

        Assert.Empty(gw.Orders);
    }

    [Fact]
    public async Task A_rejected_order_is_reported_as_a_failed_execution_and_not_counted_as_copied()
    {
        var gw = new FakeGateway { PlaceThrows = true };
        ConcurrentQueue<CopyExecution> executions = new();
        CopyTradingFollowerService? captured = null;

        await RunAsync(gw,
            () => Json(Trade("t1", "BTCUSDT", 0.5m, 100m, DateTime.UtcNow)),
            () => !executions.IsEmpty, "the execution report",
            svc => captured = svc,
            executions: executions);

        var exec = Assert.Single(executions.ToArray());
        Assert.False(exec.Success);
        Assert.Equal("t1", exec.LeaderTradeId);
        Assert.Contains("insufficient balance", exec.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, captured!.TotalCopied);
    }
}
