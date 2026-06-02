using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class AiTraderAgentServiceTests
{
    // ── Stub gateway: public order book at a fixed mid; private endpoints unused in paper. ──
    private sealed class StubGateway : IExchangeGateway
    {
        private readonly decimal _bid, _ask;
        public StubGateway(decimal bid, decimal ask) { _bid = bid; _ask = ask; }

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order order) { order.Status = OrderStatus.Filled; return Task.FromResult(order); }
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(10_000m);
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult(new OrderBook
        {
            Symbol = symbol,
            Bids = new List<OrderBookLevel> { new() { Price = _bid, Quantity = 10m } },
            Asks = new List<OrderBookLevel> { new() { Price = _ask, Quantity = 10m } }
        });
        public IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
    }

    // ── Scripted Anthropic responses: returns each queued body in turn. ──
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public int Calls { get; private set; }
        public ScriptedHandler(IEnumerable<string> responses) => _responses = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = _responses.Count > 0 ? _responses.Dequeue() : EndTurn("done");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    // ── Stub DEX gateway: quotes 1000 tokens per 1 native; honeypot probe passes. ──
    private sealed class StubDexGateway : IDexTradeGateway
    {
        public string NetworkName => "Solana";
        public string NativeSymbol => "SOL";
        public string SupportedDexesLabel => "Jupiter";
        public IReadOnlyList<DexQuoteAssetOption> SupportedQuoteAssets => [];
        public bool SupportsDex(string? dexId) => true;

        public Task<decimal> GetTokenPriceInNativeAsync(string tokenAddress, decimal nativeAmount, string? dexId = null)
            => Task.FromResult(nativeAmount * 1000m);
        public Task<string> BuyTokenAsync(string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent = 5, string? dexId = null, string? spendAssetSymbol = null)
            => Task.FromResult("0xBUY");
        public Task<string> SellTokenAsync(string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent = 5, string? dexId = null, string? receiveAssetSymbol = null)
            => Task.FromResult("0xSELL");
        public Task<int> GetTokenDecimalsAsync(string tokenAddress) => Task.FromResult(9);
        public Task<decimal> GetTokenBalanceAsync(string tokenAddress) => Task.FromResult(0m);
        public Task<DexSellabilityProbeResult> ProbeSellabilityAsync(DexSellabilityProbeRequest request)
            => Task.FromResult(new DexSellabilityProbeResult(true, true, false, 1m, 0.95m, -2m, "ok"));
        public Task<DexBuyExecutionResult> ExecuteConfirmedBuyAsync(DexBuyExecutionRequest request) => throw new NotImplementedException();
        public Task<DexSellExecutionResult> ExecuteConfirmedSellAsync(DexSellExecutionRequest request) => throw new NotImplementedException();

        // IExchangeGateway
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order order) => Task.FromResult(order);
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(5m);
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult(new OrderBook { Symbol = symbol });
        public IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
    }

    private static string ToolUse(string id, string name, string inputJson) =>
        $$"""
        {"stop_reason":"tool_use","content":[
          {"type":"text","text":"Inspecting then trading."},
          {"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{inputJson}}}
        ]}
        """;

    private static string EndTurn(string text) =>
        $$"""
        {"stop_reason":"end_turn","content":[{"type":"text","text":"{{text}}"}]}
        """;

    [Fact]
    public async Task PaperBuy_OpensPosition_AndFills()
    {
        // mid = (99 + 101) / 2 = 100; usd 50 → qty 0.5
        var gateway = new StubGateway(bid: 99m, ask: 101m);
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "place_order", """{"symbol":"BTCUSDT","side":"buy","usd":50}"""),
            EndTurn("Opened a small long.")
        });
        var http = new HttpClient(handler);
        var svc = new AiTraderAgentService(gateway, httpForTests: http) { LiveEnabled = false };

        string? fillSymbol = null; decimal fillQty = 0, fillPrice = 0; string? fillMode = null;
        svc.OnFill += (sym, side, qty, price, usd, mode) =>
        {
            fillSymbol = sym; fillQty = qty; fillPrice = price; fillMode = mode;
        };

        var result = await svc.RunOnceAsync("test-key", "claude-sonnet-4-6");

        Assert.Equal("end_turn", result.StoppedReason);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal("BTCUSDT", fillSymbol);
        Assert.Equal(0.5m, fillQty);
        Assert.Equal(100m, fillPrice);
        Assert.Equal("paper", fillMode);
    }

    [Fact]
    public async Task PaperOrder_ExceedingMaxOrderUsd_IsRejected()
    {
        var gateway = new StubGateway(bid: 100m, ask: 100m);
        var handler = new ScriptedHandler(new[]
        {
            // 5000 USD order against a 100 USD cap → must be rejected, no fill.
            ToolUse("t1", "place_order", """{"symbol":"ETHUSDT","side":"buy","usd":5000}"""),
            EndTurn("Could not size that.")
        });
        var svc = new AiTraderAgentService(gateway,
            limits: new AiTraderAgentService.Limits(MaxOrderUsd: 100m),
            httpForTests: new HttpClient(handler)) { LiveEnabled = false };

        var fills = 0;
        svc.OnFill += (_, _, _, _, _, _) => fills++;

        var result = await svc.RunOnceAsync("test-key", "claude-sonnet-4-6");

        Assert.Equal("end_turn", result.StoppedReason);
        Assert.Equal(0, fills);
    }

    [Fact]
    public async Task FuturesPaper_ShortOpensNegativePosition()
    {
        var gateway = new StubGateway(bid: 100m, ask: 100m);
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "place_order", """{"symbol":"BTCUSDT","side":"sell","usd":50}"""),
            EndTurn("Opened a small short.")
        });
        var svc = new AiTraderAgentService(gateway,
            httpForTests: new HttpClient(handler),
            marketType: TradingMarketType.FuturesUsdM,
            leverage: 5) { LiveEnabled = false };

        string? fillSide = null; decimal fillQty = 0;
        svc.OnFill += (_, side, qty, _, _, _) => { fillSide = side; fillQty = qty; };

        var result = await svc.RunOnceAsync("k", "claude-sonnet-4-6");

        Assert.Equal("end_turn", result.StoppedReason);
        Assert.Equal("sell", fillSide);   // shorting allowed on futures
        Assert.Equal(0.5m, fillQty);
    }

    [Fact]
    public async Task SpotPaper_SellWithoutHolding_IsRejected()
    {
        var gateway = new StubGateway(bid: 100m, ask: 100m);
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "place_order", """{"symbol":"BTCUSDT","side":"sell","usd":50}"""),
            EndTurn("Nothing to sell.")
        });
        var svc = new AiTraderAgentService(gateway,
            httpForTests: new HttpClient(handler),
            marketType: TradingMarketType.Spot) { LiveEnabled = false };

        var fills = 0;
        svc.OnFill += (_, _, _, _, _, _) => fills++;

        await svc.RunOnceAsync("k", "claude-sonnet-4-6");

        Assert.Equal(0, fills);   // spot is long-only, no short
    }

    [Fact]
    public async Task DexPaperBuy_SpendsNative_OpensTokenPosition()
    {
        var dex = new StubDexGateway();
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "dex_buy", """{"token_address":"So1111","native_amount":0.02}"""),
            EndTurn("Bought a small bag.")
        });
        var cfg = new AiTraderAgentService.DexConfig(dex, SlippagePercent: 3m, MaxNativePerOrder: 0.05m, VirtualNativeStart: 1m);
        var svc = new AiTraderAgentService(
            gateway: null!, httpForTests: new HttpClient(handler),
            venue: AiTraderAgentService.Venue.Dex, dex: cfg) { LiveEnabled = false };

        string? side = null; decimal qty = 0, nativeSpent = 0; string? mode = null;
        svc.OnFill += (_, s, q, _, val, m) => { side = s; qty = q; nativeSpent = val; mode = m; };

        var result = await svc.RunOnceAsync("k", "claude-sonnet-4-6");

        Assert.Equal("end_turn", result.StoppedReason);
        Assert.Equal("buy", side);
        Assert.Equal(20m, qty);          // 0.02 native * 1000 tokens/native
        Assert.Equal(0.02m, nativeSpent);
        Assert.Equal("paper", mode);
    }

    [Fact]
    public async Task DexBuy_OverMaxNative_IsRejected()
    {
        var dex = new StubDexGateway();
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "dex_buy", """{"token_address":"So1111","native_amount":1.0}"""),
            EndTurn("Too big.")
        });
        var cfg = new AiTraderAgentService.DexConfig(dex, MaxNativePerOrder: 0.05m);
        var svc = new AiTraderAgentService(
            gateway: null!, httpForTests: new HttpClient(handler),
            venue: AiTraderAgentService.Venue.Dex, dex: cfg) { LiveEnabled = false };

        var fills = 0;
        svc.OnFill += (_, _, _, _, _, _) => fills++;

        await svc.RunOnceAsync("k", "claude-sonnet-4-6");

        Assert.Equal(0, fills);
    }

    [Fact]
    public async Task KillSwitch_BlocksOrders()
    {
        var gateway = new StubGateway(bid: 100m, ask: 100m);
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "place_order", """{"symbol":"BTCUSDT","side":"buy","usd":50}"""),
            EndTurn("halted")
        });
        var svc = new AiTraderAgentService(gateway, httpForTests: new HttpClient(handler)) { LiveEnabled = false };
        var fills = 0;
        svc.OnFill += (_, _, _, _, _, _) => fills++;

        svc.Kill();
        await svc.RunOnceAsync("test-key", "claude-sonnet-4-6");

        Assert.Equal(0, fills);
    }
}
