# P0 Server-first Vertical Slice — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route a manual CEX **futures market order** end-to-end through the server (place → risk-gate → journal → execute → stream status), establishing the client↔server template every later flow reuses.

**Architecture:** A server `TradingService` (in `CryptoAITerminal.Executor`, beside `ExchangeBotOrderExecutor`) wraps the existing per-user key → decrypt → `IGatewayFactory` → `IExchangeGateway` path, adds a per-order risk gate and an idempotent order journal, and exposes REST (`/api/trade/*`) + a SignalR hub (`/hubs/trade`) from `CryptoAITerminal.Server.Api`. The desktop calls a thin `IServerTradingClient` instead of the gateway directly, behind a `UseServerTrading` feature flag.

**Tech Stack:** C# / .NET 8, ASP.NET Core minimal API + SignalR, Dapper + Npgsql (Postgres/TimescaleDB), xUnit. Reuses `ICexKeyProvider`, `IEnvelopeCipher`, `IGatewayFactory` from `Executor`.

---

## File Structure

- `CryptoAITerminal.Core/Contracts/TradeContracts.cs` — **create**: shared DTOs (both server + desktop reference Core).
- `CryptoAITerminal.Executor/GatewayFactory.cs` — **modify**: add the `futures` market branch.
- `CryptoAITerminal.Executor/TradingService.cs` — **create**: `ITradingService` + `TradingService`, `IOrderJournal`, `IManualRiskGate`.
- `CryptoAITerminal.Executor/PerOrderCapManualRiskGate.cs` — **create**: default per-order USD cap gate.
- `CryptoAITerminal.Server.Data/OrderJournalRepository.cs` — **create**: Npgsql `IOrderJournal` impl.
- `db/017_trade_orders.sql` — **create**: journal table migration.
- `CryptoAITerminal.Server.Api/TradeEndpoints.cs` — **create**: REST endpoints + DI wiring helper.
- `CryptoAITerminal.Server.Api/TradeHub.cs` — **create**: SignalR hub + `ITradeNotifier`.
- `CryptoAITerminal.Server.Api/Program.cs` — **modify**: register services, map endpoints + hub.
- `CryptoAITerminal.TerminalUI/Services/ServerTradingClient.cs` — **create**: `IServerTradingClient` (REST + SignalR client).
- `CryptoAITerminal.Core.Tests/Trading/*.cs` — **create**: unit tests (Core.Tests already references Executor + Core + RiskManager).

---

## Task 1: Contract DTOs in Core

**Files:**
- Create: `CryptoAITerminal.Core/Contracts/TradeContracts.cs`
- Test: `CryptoAITerminal.Core.Tests/Trading/TradeContractsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class TradeContractsTests
{
    [Fact]
    public void PlaceMarketCommand_round_trips_through_json()
    {
        var cmd = new PlaceMarketCommand("okx", "BTCUSDT", OrderSide.Buy, 0.5m, false, 10,
            FuturesMarginMode.Cross, FuturesPositionSide.Long, "cid-1");
        var json = JsonSerializer.Serialize(cmd);
        var back = JsonSerializer.Deserialize<PlaceMarketCommand>(json)!;
        Assert.Equal(cmd, back);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter TradeContractsTests`
Expected: FAIL — `PlaceMarketCommand` / namespace `CryptoAITerminal.Core.Contracts` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using CryptoAITerminal.Core.Enums;

namespace CryptoAITerminal.Core.Contracts;

/// <summary>Command to open/close a CEX futures position at market. Quantity is a POSITIVE
/// base-asset magnitude; direction is Side + PositionSide. ClientOrderId drives idempotency.</summary>
public sealed record PlaceMarketCommand(
    string Exchange, string Symbol, OrderSide Side, decimal Quantity, bool ReduceOnly,
    int Leverage, FuturesMarginMode MarginMode, FuturesPositionSide PositionSide, string ClientOrderId);

public sealed record PlaceOrderResult(bool Accepted, string? OrderId, string? RejectReason);
public sealed record CancelResult(bool Ok, string? Error);

/// <summary>Open futures position. Quantity is SIGNED (long +, short −).</summary>
public sealed record FuturesPositionDto(
    string Symbol, decimal Quantity, decimal EntryPrice, decimal MarkPrice,
    decimal UnrealizedPnl, decimal LiquidationPrice, int Leverage);

public sealed record OrderStatusDto(
    string OrderId, string ClientOrderId, string Status, decimal FilledQty, decimal AvgPrice, DateTime UpdatedUtc);

public sealed record NotificationDto(string Kind, string Severity, string Message, DateTime AtUtc);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter TradeContractsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.Core/Contracts/TradeContracts.cs CryptoAITerminal.Core.Tests/Trading/TradeContractsTests.cs
git commit -m "feat(trade): add client-server contract DTOs"
```

---

## Task 2: Extend GatewayFactory for futures

**Files:**
- Modify: `CryptoAITerminal.Executor/GatewayFactory.cs`
- Test: `CryptoAITerminal.Core.Tests/Trading/GatewayFactoryFuturesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using CryptoAITerminal.Executor;
using CryptoAITerminal.Gateway.OKX;
using CryptoAITerminal.Gateway.Binance;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class GatewayFactoryFuturesTests
{
    private const string Creds = "{\"key\":\"k\",\"secret\":\"s\",\"passphrase\":\"p\"}";

    [Fact]
    public void Create_futures_okx_returns_okx_futures_gateway()
    {
        var gw = new GatewayFactory().Create("okx", "futures", Creds);
        Assert.IsType<OKXFuturesGateway>(gw);
    }

    [Fact]
    public void Create_futures_binance_returns_binance_futures_gateway()
    {
        var gw = new GatewayFactory().Create("binance", "futures", Creds);
        Assert.IsType<BinanceFuturesGateway>(gw);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter GatewayFactoryFuturesTests`
Expected: FAIL — `NotSupportedException: market 'futures' not supported`.

- [ ] **Step 3: Write minimal implementation**

In `GatewayFactory.cs`, add the futures gateway usings at the top:

```csharp
// (add alongside existing Gateway usings — the *Futures* gateways share the same namespaces)
```

Replace the body of `Create` (currently spot-only) with:

```csharp
public IExchangeGateway Create(string exchange, string market, string credentialsJson)
{
    var c = ParseCreds(credentialsJson);
    var ex = exchange.ToLowerInvariant();

    if (string.Equals(market, "futures", StringComparison.OrdinalIgnoreCase))
    {
        return ex switch
        {
            "binance" => new BinanceFuturesGateway(null, c.Key, c.Secret),
            "bybit"   => new BybitFuturesGateway(null, c.Key, c.Secret),
            "okx"     => new OKXFuturesGateway(null, c.Key, c.Secret, c.Passphrase),
            "kucoin"  => new KucoinFuturesGateway(null, c.Key, c.Secret, c.Passphrase),
            _ => throw new NotSupportedException($"exchange '{exchange}' not supported")
        };
    }

    if (!string.Equals(market, "spot", StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException($"market '{market}' not supported");

    return ex switch
    {
        "binance" => new BinanceGateway(null, c.Key, c.Secret),
        "bybit"   => new BybitGateway(null, c.Key, c.Secret),
        "okx"     => new OKXGateway(null, c.Key, c.Secret, c.Passphrase),
        "kucoin"  => new KucoinGateway(null, c.Key, c.Secret, c.Passphrase),
        _ => throw new NotSupportedException($"exchange '{exchange}' not supported")
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter GatewayFactoryFuturesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.Executor/GatewayFactory.cs CryptoAITerminal.Core.Tests/Trading/GatewayFactoryFuturesTests.cs
git commit -m "feat(trade): build futures gateways server-side"
```

---

## Task 3: IOrderJournal abstraction + in-memory fake

**Files:**
- Create: `CryptoAITerminal.Executor/TradingService.cs` (the interfaces first — service added in Task 4)
- Test: `CryptoAITerminal.Core.Tests/Trading/InMemoryOrderJournal.cs` (test helper)

- [ ] **Step 1: Define the journal interface** (add to `TradingService.cs`)

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.Executor;

/// <summary>One recorded manual order. Status: accepted | placed | rejected.</summary>
public sealed record TradeOrderRow(
    System.Guid UserId, string Exchange, string ClientOrderId, string? ExchangeOrderId,
    string Symbol, string Side, decimal Quantity, bool ReduceOnly, string Status, string? RejectReason);

/// <summary>Idempotent journal of manual orders. Writes are keyed by (UserId, ClientOrderId).</summary>
public interface IOrderJournal
{
    Task<bool> ExistsAsync(System.Guid userId, string clientOrderId, CancellationToken ct);
    Task InsertAsync(TradeOrderRow row, CancellationToken ct);
    Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct);
}
```

- [ ] **Step 2: Add the in-memory fake** (test project)

```csharp
using System.Collections.Concurrent;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Trading;

public sealed class InMemoryOrderJournal : IOrderJournal
{
    public readonly ConcurrentDictionary<string, TradeOrderRow> Rows = new();
    private static string Key(System.Guid u, string cid) => $"{u}|{cid}";

    public Task<bool> ExistsAsync(System.Guid userId, string clientOrderId, System.Threading.CancellationToken ct)
        => Task.FromResult(Rows.ContainsKey(Key(userId, clientOrderId)));

    public Task InsertAsync(TradeOrderRow row, System.Threading.CancellationToken ct)
    { Rows[Key(row.UserId, row.ClientOrderId)] = row; return Task.CompletedTask; }

    public Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, System.Threading.CancellationToken ct)
    {
        var k = Key(userId, clientOrderId);
        if (Rows.TryGetValue(k, out var r)) Rows[k] = r with { ExchangeOrderId = exchangeOrderId, Status = "placed" };
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.Core.Tests`
Expected: build succeeds (no test yet — types compile).

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.Executor/TradingService.cs CryptoAITerminal.Core.Tests/Trading/InMemoryOrderJournal.cs
git commit -m "feat(trade): order journal interface + in-memory fake"
```

---

## Task 4: Manual risk gate

**Files:**
- Create: `CryptoAITerminal.Executor/PerOrderCapManualRiskGate.cs`
- Test: `CryptoAITerminal.Core.Tests/Trading/PerOrderCapManualRiskGateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Executor;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class PerOrderCapManualRiskGateTests
{
    private static PlaceMarketCommand Cmd(decimal qty) => new(
        "okx", "BTCUSDT", OrderSide.Buy, qty, false, 10, FuturesMarginMode.Cross, FuturesPositionSide.Long, "c1");

    [Fact]
    public void Blocks_when_notional_exceeds_cap()
    {
        var gate = new PerOrderCapManualRiskGate(maxNotionalUsd: 1000m);
        var (ok, reason) = gate.Check(Cmd(1m), price: 50000m); // 50000 notional
        Assert.False(ok);
        Assert.Contains("cap", reason);
    }

    [Fact]
    public void Allows_within_cap()
    {
        var gate = new PerOrderCapManualRiskGate(maxNotionalUsd: 100000m);
        var (ok, _) = gate.Check(Cmd(0.001m), price: 50000m); // 50 notional
        Assert.True(ok);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter PerOrderCapManualRiskGateTests`
Expected: FAIL — `PerOrderCapManualRiskGate` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using CryptoAITerminal.Core.Contracts;

namespace CryptoAITerminal.Executor;

/// <summary>Gate a manual order before it is placed. Mirrors the server bot risk gates.</summary>
public interface IManualRiskGate
{
    (bool Ok, string? Reason) Check(PlaceMarketCommand cmd, decimal price);
}

/// <summary>Rejects a manual order whose notional (qty × price) exceeds a per-order USD cap.</summary>
public sealed class PerOrderCapManualRiskGate : IManualRiskGate
{
    private readonly decimal _maxNotionalUsd;
    public PerOrderCapManualRiskGate(decimal maxNotionalUsd) => _maxNotionalUsd = maxNotionalUsd;

    public (bool Ok, string? Reason) Check(PlaceMarketCommand cmd, decimal price)
    {
        if (cmd.Quantity <= 0m) return (false, "Quantity must be positive.");
        if (price <= 0m) return (false, "No reference price to size the order.");
        var notional = cmd.Quantity * price;
        return notional > _maxNotionalUsd
            ? (false, $"Order notional {notional:C} exceeds per-order cap {_maxNotionalUsd:C}.")
            : (true, null);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter PerOrderCapManualRiskGateTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.Executor/PerOrderCapManualRiskGate.cs CryptoAITerminal.Core.Tests/Trading/PerOrderCapManualRiskGateTests.cs
git commit -m "feat(trade): per-order notional cap risk gate"
```

---

## Task 5: TradingService (the core)

**Files:**
- Modify: `CryptoAITerminal.Executor/TradingService.cs` (add the service to the interfaces from Task 3)
- Test: `CryptoAITerminal.Core.Tests/Trading/TradingServiceTests.cs`

Reuses `ICexKeyProvider`, `CexKeyMaterial`, `IEnvelopeCipher`, `IGatewayFactory`, `IPriceSource` — all already in `Executor` (`ExchangeBotOrderExecutor.cs`).

> **Test-fake note:** `FakeGateway` below must implement every non-default member of `IExchangeGateway` (`CryptoAITerminal.Core/Interfaces/IExchangeGateway.cs`). Open that interface first and stub any members the slice doesn't exercise (e.g. `GetBalanceAsync`, `GetOrderBookAsync`, `GetCandlesAsync`) with `throw new NotImplementedException()` or a trivial default; only `PlaceOrderAsync`, `CancelOrderAsync`, `GetOpenPositionsAsync`, and `MarketDataStream` need real behavior for these tests.

- [ ] **Step 1: Write the failing tests**

```csharp
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class TradingServiceTests
{
    private static readonly System.Guid Uid = System.Guid.NewGuid();

    private sealed class FakeKeys : ICexKeyProvider
    {
        public Task<CexKeyMaterial?> FindAsync(System.Guid u, string ex, System.Threading.CancellationToken ct)
            => Task.FromResult<CexKeyMaterial?>(new CexKeyMaterial(new byte[]{1}, new byte[]{2}, "trade"));
    }
    private sealed class FakeCipher : IEnvelopeCipher
    {
        public Task<string> DecryptAsync(byte[] ct1, byte[] dek, System.Threading.CancellationToken ct)
            => Task.FromResult("{\"key\":\"k\",\"secret\":\"s\",\"passphrase\":\"p\"}");
        public Task<(byte[] Ciphertext, byte[] WrappedDek)> EncryptAsync(string plaintext, System.Threading.CancellationToken ct)
            => Task.FromResult((new byte[0], new byte[0]));
    }
    private sealed class FakePrice : IPriceSource
    {
        public decimal Price = 50000m;
        public Task<decimal> GetPriceAsync(string ex, string sym, System.Threading.CancellationToken ct) => Task.FromResult(Price);
    }
    private sealed class FakeGateway : IExchangeGateway
    {
        public System.Func<Order, Order> OnPlace = o => { o.Id = "ex-1"; o.FilledQuantity = o.Quantity; o.Price = 50000m; return o; };
        public Order? Placed;
        public IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
        public Task<Order> PlaceOrderAsync(Order o) { Placed = o; return Task.FromResult(OnPlace(o)); }
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
    }
    private sealed class FakeFactory : IGatewayFactory
    {
        public FakeGateway Gw = new();
        public IExchangeGateway Create(string ex, string mkt, string creds) => Gw;
    }

    private static PlaceMarketCommand Cmd(bool reduceOnly = false, OrderSide side = OrderSide.Buy, decimal qty = 0.001m)
        => new("okx", "BTCUSDT", side, qty, reduceOnly, 10, FuturesMarginMode.Cross,
               side == OrderSide.Buy ? FuturesPositionSide.Long : FuturesPositionSide.Short, "cid-1");

    private static (TradingService svc, FakeFactory f, InMemoryOrderJournal j) Build(
        IManualRiskGate? gate = null, FakePrice? price = null)
    {
        var f = new FakeFactory();
        var j = new InMemoryOrderJournal();
        var svc = new TradingService(new FakeKeys(), new FakeCipher(), f, price ?? new FakePrice(),
            gate ?? new PerOrderCapManualRiskGate(1_000_000m), j);
        return (svc, f, j);
    }

    [Fact]
    public async Task Happy_path_accepts_places_and_journals()
    {
        var (svc, f, j) = Build();
        var r = await svc.PlaceMarketAsync(Uid, Cmd(), default);
        Assert.True(r.Accepted);
        Assert.Equal("ex-1", r.OrderId);
        Assert.Equal(TradingMarketType.FuturesUsdM, f.Gw.Placed!.MarketType);
        Assert.Equal("placed", j.Rows.Values.Single().Status);
    }

    [Fact]
    public async Task Risk_block_rejects_and_does_not_place()
    {
        var (svc, f, _) = Build(gate: new PerOrderCapManualRiskGate(10m)); // 0.001*50000=50 > 10
        var r = await svc.PlaceMarketAsync(Uid, Cmd(), default);
        Assert.False(r.Accepted);
        Assert.Contains("cap", r.RejectReason);
        Assert.Null(f.Gw.Placed);
    }

    [Fact]
    public async Task Gateway_throw_is_journaled_rejected_not_crashed()
    {
        var (svc, f, j) = Build();
        f.Gw.OnPlace = _ => throw new System.Exception("exchange down");
        var r = await svc.PlaceMarketAsync(Uid, Cmd(), default);
        Assert.False(r.Accepted);
        Assert.Contains("exchange down", r.RejectReason);
        Assert.Equal("rejected", j.Rows.Values.Single().Status);
    }

    [Fact]
    public async Task Duplicate_client_order_id_is_idempotent()
    {
        var (svc, f, _) = Build();
        await svc.PlaceMarketAsync(Uid, Cmd(), default);
        f.Gw.Placed = null; // reset
        var r2 = await svc.PlaceMarketAsync(Uid, Cmd(), default);
        Assert.True(r2.Accepted);
        Assert.Null(f.Gw.Placed); // second call placed nothing
    }

    [Fact]
    public async Task Close_sets_reduce_only_and_short_position_side()
    {
        var (svc, f, _) = Build();
        await svc.PlaceMarketAsync(Uid, Cmd(reduceOnly: true, side: OrderSide.Sell), default);
        Assert.True(f.Gw.Placed!.ReduceOnly);
        Assert.Equal(FuturesPositionSide.Short, f.Gw.Placed!.PositionSide);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter TradingServiceTests`
Expected: FAIL — `TradingService` does not exist.

- [ ] **Step 3: Write minimal implementation** (append to `TradingService.cs`)

```csharp
using System;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Executor;

public interface ITradingService
{
    Task<PlaceOrderResult> PlaceMarketAsync(Guid uid, PlaceMarketCommand cmd, CancellationToken ct);
    Task<CancelResult> CancelAsync(Guid uid, string exchange, string orderId, CancellationToken ct);
    Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(Guid uid, string exchange, CancellationToken ct);
}

/// <summary>Server-side manual futures trading: key → decrypt → risk-gate → journal → gateway.
/// Idempotent by (uid, ClientOrderId). Mirrors <see cref="ExchangeBotOrderExecutor"/>.</summary>
public sealed class TradingService : ITradingService
{
    private readonly ICexKeyProvider _keys;
    private readonly IEnvelopeCipher _cipher;
    private readonly IGatewayFactory _factory;
    private readonly IPriceSource _price;
    private readonly IManualRiskGate _risk;
    private readonly IOrderJournal _journal;

    public TradingService(ICexKeyProvider keys, IEnvelopeCipher cipher, IGatewayFactory factory,
        IPriceSource price, IManualRiskGate risk, IOrderJournal journal)
    { _keys = keys; _cipher = cipher; _factory = factory; _price = price; _risk = risk; _journal = journal; }

    public async Task<PlaceOrderResult> PlaceMarketAsync(Guid uid, PlaceMarketCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.ClientOrderId))
            return new PlaceOrderResult(false, null, "ClientOrderId is required.");

        // Idempotency: a retried command returns success without placing again.
        if (await _journal.ExistsAsync(uid, cmd.ClientOrderId, ct))
            return new PlaceOrderResult(true, null, null);

        var price = await _price.GetPriceAsync(cmd.Exchange, cmd.Symbol, ct);
        var (ok, reason) = _risk.Check(cmd, price);
        if (!ok) return new PlaceOrderResult(false, null, reason);

        var sideStr = cmd.Side == OrderSide.Sell ? "sell" : "buy";
        await _journal.InsertAsync(new TradeOrderRow(uid, cmd.Exchange, cmd.ClientOrderId, null,
            cmd.Symbol, sideStr, cmd.Quantity, cmd.ReduceOnly, "accepted", null), ct);

        var key = await _keys.FindAsync(uid, cmd.Exchange, ct);
        if (key is null)
            return await RejectAsync(uid, cmd.ClientOrderId, $"no trade key for {cmd.Exchange}", ct);
        var perms = key.Permissions ?? "";
        if (perms.Contains("withdraw", StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(uid, cmd.ClientOrderId, "key has withdraw permission — refused (trade-only required)", ct);
        if (!perms.Contains("trade", StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(uid, cmd.ClientOrderId, "key lacks trade permission", ct);

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            var gateway = _factory.Create(cmd.Exchange, "futures", creds);
            var order = new Order
            {
                Symbol = cmd.Symbol,
                Side = cmd.Side,
                Type = OrderType.Market,
                Quantity = cmd.Quantity,
                ReduceOnly = cmd.ReduceOnly,
                Leverage = cmd.Leverage,
                MarginMode = cmd.MarginMode,
                PositionSide = cmd.PositionSide,
                MarketType = TradingMarketType.FuturesUsdM,
                ClientOrderId = cmd.ClientOrderId,
            };
            var placed = await gateway.PlaceOrderAsync(order);
            await _journal.MarkPlacedAsync(uid, cmd.ClientOrderId, placed.Id ?? "", ct);
            return new PlaceOrderResult(true, placed.Id, null);
        }
        catch (Exception ex)
        {
            return await RejectAsync(uid, cmd.ClientOrderId, ex.Message, ct);
        }
        finally { creds = null!; }
    }

    private async Task<PlaceOrderResult> RejectAsync(Guid uid, string cid, string reason, CancellationToken ct)
    {
        await _journal.InsertAsync(new TradeOrderRow(uid, "", cid, null, "", "", 0m, false, "rejected", reason), ct);
        return new PlaceOrderResult(false, null, reason);
    }

    public async Task<CancelResult> CancelAsync(Guid uid, string exchange, string orderId, CancellationToken ct)
    {
        var key = await _keys.FindAsync(uid, exchange, ct);
        if (key is null) return new CancelResult(false, $"no trade key for {exchange}");
        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try { await _factory.Create(exchange, "futures", creds).CancelOrderAsync(orderId); return new CancelResult(true, null); }
        catch (Exception ex) { return new CancelResult(false, ex.Message); }
        finally { creds = null!; }
    }

    public async Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(Guid uid, string exchange, CancellationToken ct)
    {
        var key = await _keys.FindAsync(uid, exchange, ct);
        if (key is null) return [];
        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            var positions = await _factory.Create(exchange, "futures", creds).GetOpenPositionsAsync();
            return positions.Select(p => new FuturesPositionDto(
                p.Symbol, p.Quantity, p.EntryPrice, p.MarkPrice, p.UnrealizedPnl, p.LiquidationPrice, p.Leverage)).ToList();
        }
        finally { creds = null!; }
    }
}
```

> Note on `RejectAsync` idempotency: the reject row uses the same `(uid, ClientOrderId)` key so `InsertAsync` overwrites the earlier `accepted` row in the in-memory fake and upserts in the Npgsql impl (Task 6). Verify `InMemoryOrderJournal.InsertAsync` overwrites by key — it does (dictionary assignment).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter TradingServiceTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.Executor/TradingService.cs CryptoAITerminal.Core.Tests/Trading/TradingServiceTests.cs
git commit -m "feat(trade): server TradingService with risk gate + idempotent journal"
```

---

## Task 6: Npgsql order journal + migration

**Files:**
- Create: `db/017_trade_orders.sql`
- Create: `CryptoAITerminal.Server.Data/OrderJournalRepository.cs`

- [ ] **Step 1: Write the migration**

```sql
-- db/017_trade_orders.sql — manual order journal (idempotent by client_order_id)
CREATE TABLE IF NOT EXISTS trade_orders (
    user_id           uuid        NOT NULL,
    exchange          text        NOT NULL,
    client_order_id   text        NOT NULL,
    exchange_order_id text,
    symbol            text        NOT NULL,
    side              text        NOT NULL,
    quantity          numeric     NOT NULL,
    reduce_only       boolean     NOT NULL DEFAULT false,
    status            text        NOT NULL,
    reject_reason     text,
    created_utc       timestamptz NOT NULL DEFAULT now(),
    updated_utc       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, client_order_id)
);
```

- [ ] **Step 2: Write the repository** (mirrors `BotOrdersRepository` — Dapper + `Db`)

```csharp
using Dapper;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Server.Data;

/// <summary>Npgsql-backed <see cref="IOrderJournal"/>. Upserts by (user_id, client_order_id).</summary>
public sealed class OrderJournalRepository : IOrderJournal
{
    private readonly Db _db;
    public OrderJournalRepository(Db db) => _db = db;

    public async Task<bool> ExistsAsync(System.Guid userId, string clientOrderId, System.Threading.CancellationToken ct)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM trade_orders WHERE user_id=@userId AND client_order_id=@clientOrderId);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { userId, clientOrderId }, cancellationToken: ct));
    }

    public async Task InsertAsync(TradeOrderRow r, System.Threading.CancellationToken ct)
    {
        const string sql = @"
INSERT INTO trade_orders (user_id, exchange, client_order_id, exchange_order_id, symbol, side, quantity, reduce_only, status, reject_reason)
VALUES (@UserId, @Exchange, @ClientOrderId, @ExchangeOrderId, @Symbol, @Side, @Quantity, @ReduceOnly, @Status, @RejectReason)
ON CONFLICT (user_id, client_order_id) DO UPDATE
   SET status=@Status, reject_reason=@RejectReason, exchange_order_id=COALESCE(EXCLUDED.exchange_order_id, trade_orders.exchange_order_id), updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, r, cancellationToken: ct));
    }

    public async Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, System.Threading.CancellationToken ct)
    {
        const string sql = @"UPDATE trade_orders SET status='placed', exchange_order_id=@exchangeOrderId, updated_utc=now()
                             WHERE user_id=@userId AND client_order_id=@clientOrderId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, clientOrderId, exchangeOrderId }, cancellationToken: ct));
    }
}
```

- [ ] **Step 3: Add the Executor project reference to Server.Data (if missing)**

Check `CryptoAITerminal.Server.Data.csproj` for a reference to `..\CryptoAITerminal.Executor\CryptoAITerminal.Executor.csproj`. If absent, add it (Server.Data needs `IOrderJournal`/`TradeOrderRow`). Then:

Run: `dotnet build CryptoAITerminal.Server.Data`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add db/017_trade_orders.sql CryptoAITerminal.Server.Data/OrderJournalRepository.cs CryptoAITerminal.Server.Data/CryptoAITerminal.Server.Data.csproj
git commit -m "feat(trade): npgsql order journal + migration"
```

---

## Task 7: REST endpoints in Server.Api

**Files:**
- Create: `CryptoAITerminal.Server.Api/TradeEndpoints.cs`
- Modify: `CryptoAITerminal.Server.Api/Program.cs`

- [ ] **Step 1: Write the endpoint module**

```csharp
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Executor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CryptoAITerminal.Server.Api;

public static class TradeEndpoints
{
    public static void MapTradeEndpoints(this IEndpointRouteBuilder app)
    {
        // uid is set by the existing auth middleware: ctx.Items["uid"] (Guid).
        static System.Guid Uid(HttpContext ctx) => (System.Guid)ctx.Items["uid"]!;

        app.MapPost("/api/trade/order", async (HttpContext ctx, PlaceMarketCommand cmd, ITradingService svc) =>
            Results.Ok(await svc.PlaceMarketAsync(Uid(ctx), cmd, ctx.RequestAborted)));

        app.MapPost("/api/trade/order/{orderId}/cancel", async (HttpContext ctx, string orderId, string exchange, ITradingService svc) =>
            Results.Ok(await svc.CancelAsync(Uid(ctx), exchange, orderId, ctx.RequestAborted)));

        app.MapGet("/api/trade/positions", async (HttpContext ctx, string exchange, ITradingService svc) =>
            Results.Ok(await svc.GetPositionsAsync(Uid(ctx), exchange, ctx.RequestAborted)));
    }
}
```

- [ ] **Step 2: Register services + map endpoints in `Program.cs`**

Add to the DI section (near the other `builder.Services.AddSingleton<...>` lines):

```csharp
builder.Services.AddSingleton<IGatewayFactory, GatewayFactory>();
builder.Services.AddSingleton<IManualRiskGate>(_ => new PerOrderCapManualRiskGate(
    decimal.TryParse(Environment.GetEnvironmentVariable("MANUAL_MAX_NOTIONAL_USD"), out var cap) ? cap : 5000m));
builder.Services.AddSingleton<IOrderJournal, OrderJournalRepository>();
builder.Services.AddSingleton<ITradingService, TradingService>();
// ICexKeyProvider, IEnvelopeCipher, IPriceSource are already registered for the executor path —
// if not present in this host, register the same implementations used by ExchangeBotOrderExecutor.
```

Add after the endpoint maps (near the end, before `app.Run()`):

```csharp
app.MapTradeEndpoints();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.Server.Api`
Expected: build succeeds. (If `ICexKeyProvider`/`IEnvelopeCipher`/`IPriceSource` are unregistered in this host, DI validation will flag them — register the same impls the executor uses.)

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.Server.Api/TradeEndpoints.cs CryptoAITerminal.Server.Api/Program.cs
git commit -m "feat(trade): REST endpoints for place/cancel/positions"
```

---

## Task 8: SignalR hub + order-status stream

**Files:**
- Create: `CryptoAITerminal.Server.Api/TradeHub.cs`
- Modify: `CryptoAITerminal.Server.Api/Program.cs`, `CryptoAITerminal.Server.Api/TradeEndpoints.cs`

- [ ] **Step 1: Add the SignalR package**

Run: `dotnet add CryptoAITerminal.Server.Api package Microsoft.AspNetCore.SignalR` (bundled with the ASP.NET Core shared framework; the explicit package is a no-op on net8.0 web SDK — skip if it errors as already-included).

- [ ] **Step 2: Write the hub + notifier**

```csharp
using CryptoAITerminal.Core.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace CryptoAITerminal.Server.Api;

/// <summary>Per-user trade stream. Clients join the group named by their uid.</summary>
public sealed class TradeHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        // uid resolved by auth middleware and stashed on the connection's HttpContext.
        var uid = Context.GetHttpContext()?.Items["uid"]?.ToString();
        if (!string.IsNullOrEmpty(uid))
            await Groups.AddToGroupAsync(Context.ConnectionId, uid);
        await base.OnConnectedAsync();
    }
}

public interface ITradeNotifier
{
    Task OrderStatusAsync(System.Guid uid, OrderStatusDto status);
    Task PositionUpdateAsync(System.Guid uid, FuturesPositionDto position);
    Task NotifyAsync(System.Guid uid, NotificationDto notification);
}

public sealed class TradeNotifier : ITradeNotifier
{
    private readonly IHubContext<TradeHub> _hub;
    public TradeNotifier(IHubContext<TradeHub> hub) => _hub = hub;
    public Task OrderStatusAsync(System.Guid uid, OrderStatusDto s) => _hub.Clients.Group(uid.ToString()).SendAsync("orderStatus", s);
    public Task PositionUpdateAsync(System.Guid uid, FuturesPositionDto p) => _hub.Clients.Group(uid.ToString()).SendAsync("positionUpdate", p);
    public Task NotifyAsync(System.Guid uid, NotificationDto n) => _hub.Clients.Group(uid.ToString()).SendAsync("notification", n);
}
```

- [ ] **Step 3: Wire it in `Program.cs`**

```csharp
builder.Services.AddSignalR();
builder.Services.AddSingleton<ITradeNotifier, TradeNotifier>();
```

After building `app` and after `app.MapTradeEndpoints();`:

```csharp
app.MapHub<TradeHub>("/hubs/trade");
```

- [ ] **Step 4: Emit status from the place endpoint**

In `TradeEndpoints.MapTradeEndpoints`, update the order endpoint to push an `orderStatus` on a successful place:

```csharp
app.MapPost("/api/trade/order", async (HttpContext ctx, PlaceMarketCommand cmd, ITradingService svc, ITradeNotifier notif) =>
{
    var uid = Uid(ctx);
    var result = await svc.PlaceMarketAsync(uid, cmd, ctx.RequestAborted);
    if (result.Accepted && result.OrderId is not null)
        await notif.OrderStatusAsync(uid, new OrderStatusDto(result.OrderId, cmd.ClientOrderId, "placed", cmd.Quantity, 0m, System.DateTime.UtcNow));
    else if (!result.Accepted)
        await notif.NotifyAsync(uid, new NotificationDto("order", "error", result.RejectReason ?? "rejected", System.DateTime.UtcNow));
    return Results.Ok(result);
});
```

> A background order-status poller (real fills from the exchange) is a follow-up; P0 emits the synchronous accept + reject notifications so the client stream is proven end-to-end.

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.Server.Api`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.Server.Api/TradeHub.cs CryptoAITerminal.Server.Api/Program.cs CryptoAITerminal.Server.Api/TradeEndpoints.cs
git commit -m "feat(trade): SignalR hub + order-status/notification stream"
```

---

## Task 9: Desktop thin client + feature flag

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/ServerTradingClient.cs`
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` (the manual futures market path only)

- [ ] **Step 1: Add the SignalR client package**

Run: `dotnet add CryptoAITerminal.TerminalUI package Microsoft.AspNetCore.SignalR.Client`
Expected: package added.

- [ ] **Step 2: Write the client**

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace CryptoAITerminal.TerminalUI.Services;

public interface IServerTradingClient
{
    Task<PlaceOrderResult> PlaceMarketAsync(PlaceMarketCommand cmd);
    Task<CancelResult> CancelAsync(string exchange, string orderId);
    Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(string exchange);
    IObservable<OrderStatusDto> OrderStatus { get; }
    IObservable<NotificationDto> Notifications { get; }
    Task ConnectAsync();
}

/// <summary>REST + SignalR client for the server trade API. Auth header X-License is set by the caller's HttpClient.</summary>
public sealed class ServerTradingClient : IServerTradingClient, IAsyncDisposable
{
    private readonly HttpClient _http;      // BaseAddress = server root, default header X-License
    private readonly string _hubUrl;
    private readonly string _license;
    private HubConnection? _hub;
    private readonly Subject<OrderStatusDto> _status = new();
    private readonly Subject<NotificationDto> _notif = new();

    public ServerTradingClient(HttpClient http, string hubUrl, string license)
    { _http = http; _hubUrl = hubUrl; _license = license; }

    public IObservable<OrderStatusDto> OrderStatus => _status;
    public IObservable<NotificationDto> Notifications => _notif;

    public async Task ConnectAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(_hubUrl, o => o.Headers["X-License"] = _license)
            .WithAutomaticReconnect()
            .Build();
        _hub.On<OrderStatusDto>("orderStatus", s => _status.OnNext(s));
        _hub.On<NotificationDto>("notification", n => _notif.OnNext(n));
        await _hub.StartAsync();
    }

    public async Task<PlaceOrderResult> PlaceMarketAsync(PlaceMarketCommand cmd)
    {
        var resp = await _http.PostAsJsonAsync("/api/trade/order", cmd);
        return (await resp.Content.ReadFromJsonAsync<PlaceOrderResult>())!;
    }

    public async Task<CancelResult> CancelAsync(string exchange, string orderId)
    {
        var resp = await _http.PostAsync($"/api/trade/order/{orderId}/cancel?exchange={exchange}", null);
        return (await resp.Content.ReadFromJsonAsync<CancelResult>())!;
    }

    public async Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(string exchange)
        => await _http.GetFromJsonAsync<List<FuturesPositionDto>>($"/api/trade/positions?exchange={exchange}") ?? [];

    public async ValueTask DisposeAsync() { if (_hub is not null) await _hub.DisposeAsync(); }
}
```

- [ ] **Step 3: Route the manual futures market path behind a flag**

In `MainWindowViewModel`, add a feature flag field:

```csharp
private readonly bool _useServerTrading =
    string.Equals(Environment.GetEnvironmentVariable("USE_SERVER_TRADING"), "true", StringComparison.OrdinalIgnoreCase);
private IServerTradingClient? _serverTrading; // injected when the flag is on
```

In the manual futures market path (`PlaceCexMarketOrderAsync`, futures branch), before the direct gateway call, add:

```csharp
if (_useServerTrading && IsManualFuturesMode && _serverTrading is not null)
{
    var cmd = new CryptoAITerminal.Core.Contracts.PlaceMarketCommand(
        SelectedFuturesExchange, SelectedTradingSymbol, side, Math.Abs(quantity), reduceOnly,
        ManualFuturesLeverage, SelectedManualFuturesMarginModeEnum,
        ManualEntryPositionSide(side == Core.Enums.OrderSide.Buy), Guid.NewGuid().ToString("N"));
    var r = await _serverTrading.PlaceMarketAsync(cmd);
    if (!r.Accepted) { AddLog($"Server rejected order: {r.RejectReason}"); }
    return new Order { Id = r.OrderId ?? "", Symbol = cmd.Symbol, Quantity = cmd.Quantity };
}
// ...existing in-process gateway path unchanged (fallback when the flag is off)...
```

> The exact local variable/property names (`side`, `quantity`, `reduceOnly`, `ManualEntryPositionSide`, `SelectedManualFuturesMarginModeEnum`) already exist in `PlaceCexMarketOrderAsync`; wire the flag branch at the method's entry after those are computed. Keep the existing path intact so the flag defaults off.

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI -p:UseAppHost=false`
Expected: build succeeds (0 `error CS`).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/ServerTradingClient.cs CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj
git commit -m "feat(trade): desktop thin client behind USE_SERVER_TRADING flag"
```

---

## Task 10: Full regression + slice smoke

- [ ] **Step 1: Run the whole suite**

Run: `dotnet test CryptoAITerminal.Core.Tests`
Expected: all pass (713 existing + the new Trading tests).

- [ ] **Step 2: Manual smoke (documented, not automated)**

With `USE_SERVER_TRADING=true` and a running `Server.Api` (paper key), place a small futures market order from the desktop; confirm: REST returns `Accepted=true`, an `orderStatus` arrives over SignalR, `trade_orders` has one `placed` row, and a duplicate `ClientOrderId` places nothing. Record the result in the PR description.

- [ ] **Step 3: Commit any fixes, then open the PR** (when the user asks).

---

## Spec coverage check

- Manual futures market order (open/close) → Tasks 5, 9. ✅
- Risk gate server-side → Tasks 4, 5. ✅
- Idempotency by ClientOrderId → Tasks 5, 6. ✅
- Order journal (TimescaleDB) → Task 6. ✅
- REST contract (`/api/trade/order|cancel|positions`) → Task 7. ✅
- SignalR hub (`orderStatus`/`positionUpdate`/`notification`) → Task 8. ✅
- Per-user auth (`uid`) + Vault keys (trade-only enforcement) → Tasks 5, 7. ✅
- Desktop thin client + feature flag → Task 9. ✅
- Base-asset units, signed positions → Tasks 1, 5 (contract note + DTO). ✅
- Cancel positions read → Tasks 5, 7. ✅

**Out of scope (per spec):** background real-fill poller, bots/DEX/spot/working-orders migration, further god-object extraction — follow-ups after P0 proves the template.
