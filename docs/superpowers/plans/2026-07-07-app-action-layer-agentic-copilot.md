# App Action Layer + Agentic Copilot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the read-only global Copilot into an action-capable app agent that can navigate, read, and (with confirmation) fill fields / arm / place orders across the app, on a shared typed action layer.

**Architecture:** A typed `IAppAction` catalog (`AppActionRegistry`) whose actions call the app only through a thin `IAppActionContext` bridge (adapter over existing VMs — no duplicated logic). `AppAgentService` exposes read actions as immediately-executing agent tools and mutating actions as *proposals* routed to an Action Tray (CONFIRM mode, default) or executed directly (AUTO mode, opt-in). Existing money-gates (`RiskManager`, `WalletVM.TryApproveLiveExecution`, Hyperliquid testnet default) are always in the call path.

**Tech Stack:** .NET 8, Avalonia + ReactiveUI, xUnit (`CryptoAITerminal.Core.Tests`), existing `CryptoAITerminal.AIEngine.Agent` runner framework (`IAgentRunner`, `AgentTool`, `AgentRunnerFactory`).

---

## File Structure

Created under `CryptoAITerminal.TerminalUI/Services/AppActions/`:
- `IAppAction.cs` — action contract + `AppActionResult` + `AppActionCategory`.
- `IAppActionContext.cs` — the only bridge into the app (interface).
- `AppActionRegistry.cs` — catalog; exposes actions and builds `AgentTool`s.
- `NavigationActions.cs`, `TradingActions.cs`, `SignalAlertActions.cs`, `BotWalletActions.cs` — action groups.
- `AppActionAuditLog.cs` — persisted audit trail.
- `AppAgentService.cs` — agent loop + confirm/auto routing + proposal sink.
- `MainWindowAppActionContext.cs` — real bridge to `MainWindowViewModel`/`DexDeskViewModel`/`AlertsVM`/`GridBotVM`/`WalletVM`.

ViewModels/Views:
- `ViewModels/AgentActionTrayViewModel.cs` + `ViewModels/AgentActionProposalViewModel.cs`
- `Views/AgentActionTrayView.axaml` (+ `.cs`)
- Modify `ViewModels/CopilotViewModel.cs`, `ViewModels/MainWindowViewModel.cs`, and the Copilot view XAML.

Tests under `CryptoAITerminal.Core.Tests/AppActions/` (+ a `FakeAppActionContext` test double).

> **Note on project reference:** `CryptoAITerminal.Core.Tests` must reference `CryptoAITerminal.TerminalUI` (it already does — `CopilotAgentService`/`AiTraderAgentService` are tested there). Verify with `grep TerminalUI CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj` before Task 1; if absent, add `<ProjectReference Include="..\CryptoAITerminal.TerminalUI\CryptoAITerminal.TerminalUI.csproj" />`.

---

## Task 1: Action contract, result, category

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/IAppAction.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/AppActionResultTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppActionResultTests
{
    [Fact]
    public void Ok_sets_success_and_message()
    {
        var r = AppActionResult.Ok("done");
        Assert.True(r.Success);
        Assert.Equal("done", r.Message);
    }

    [Fact]
    public void Fail_sets_failure_and_message()
    {
        var r = AppActionResult.Fail("nope");
        Assert.False(r.Success);
        Assert.Equal("nope", r.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppActionResultTests`
Expected: FAIL — `AppActionResult` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>Broad grouping of an action, used for UI badges and tool namespacing.</summary>
public enum AppActionCategory { Navigation, Read, Trading, Signals, Bots, Settings, Wallet }

/// <summary>Structured outcome of an action, fed to both the model and the UI.</summary>
public sealed record AppActionResult(bool Success, string Message, string? Detail = null)
{
    public static AppActionResult Ok(string message, string? detail = null) => new(true, message, detail);
    public static AppActionResult Fail(string message, string? detail = null) => new(false, message, detail);
}

/// <summary>
/// One thing the AI can do in the app. Metadata + logic only; all app effects go through
/// <see cref="IAppActionContext"/>, so actions are unit-testable with a fake context.
/// </summary>
public interface IAppAction
{
    /// <summary>snake_case id the model sees, e.g. "nav.goto" → tool name "nav_goto".</summary>
    string Id { get; }
    AppActionCategory Category { get; }
    /// <summary>One/two sentences teaching the model when to call it.</summary>
    string Description { get; }
    /// <summary>JSON-schema object for the input (same shape as AgentTool.InputSchema).</summary>
    object ParamSchema { get; }
    /// <summary>True if the action changes state (needs confirmation in CONFIRM mode).</summary>
    bool IsMutating { get; }
    /// <summary>Human sentence shown before execution, e.g. "Arm BUY LIMIT 0.5 ETH @ 3200".</summary>
    string Preview(JsonElement args);
    Task<AppActionResult> ExecuteAsync(JsonElement args, IAppActionContext ctx, CancellationToken ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppActionResultTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/IAppAction.cs CryptoAITerminal.Core.Tests/AppActions/AppActionResultTests.cs
git commit -m "feat(actions): add IAppAction contract, AppActionResult, category"
```

---

## Task 2: The app-context bridge interface

Defines every operation actions may perform. Actions in later tasks call ONLY these members.

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/IAppActionContext.cs`

- [ ] **Step 1: Write the interface** (no test — pure interface; Task 4 fakes it, Task 5+ exercise it)

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>Minimal read snapshot for the context (mirrors CopilotAgentService rows).</summary>
public sealed record ActionPositionLine(string Symbol, decimal Qty, decimal AvgEntry, decimal Mark, decimal UnrealizedPnl);
public sealed record ActionMarketSnapshot(string Symbol, decimal Bid, decimal Ask, decimal Last);

/// <summary>
/// The ONLY surface actions use to affect the app. Implemented by
/// <see cref="MainWindowAppActionContext"/> over the real view models (UI-thread marshalled),
/// and by a fake in tests. Every method is small and maps to an existing command/method.
/// Read methods return data; mutating methods return an <see cref="AppActionResult"/>.
/// </summary>
public interface IAppActionContext
{
    // ── Navigation + reads ──
    AppActionResult NavigateTo(string sectionKey);
    IReadOnlyList<string> KnownSections { get; }
    Task<decimal> GetBalanceUsdtAsync(CancellationToken ct);
    Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct);
    Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct);

    // ── Trading ticket (CEX) ──
    AppActionResult SetTradingSymbol(string symbol);
    AppActionResult SetTicketSide(bool isBuy);
    AppActionResult SetOrderType(string type);          // "Market" | "Limit"
    AppActionResult SetMarketMode(string mode);         // "Spot" | "Futures"
    AppActionResult SetQuantity(decimal quantity);
    AppActionResult SetUsdNotional(decimal usd);        // converts to qty at current price
    AppActionResult SetLeverage(int leverage);
    AppActionResult SetLimitPrice(decimal price);
    AppActionResult SetTakeProfit(decimal price);
    AppActionResult SetStopLoss(decimal price);
    AppActionResult ArmLimit(bool isBuy);
    AppActionResult ArmTakeProfit();
    AppActionResult ArmStopLoss();
    Task<AppActionResult> PlaceMarketAsync(bool isBuy, CancellationToken ct);
    Task<AppActionResult> ClosePositionAsync(CancellationToken ct);

    // ── DEX / perps ──
    Task<AppActionResult> SelectDexTokenAsync(string tokenAddressOrSymbol, CancellationToken ct);
    Task<AppActionResult> DexBuyAsync(decimal amountNative, CancellationToken ct);
    Task<AppActionResult> DexSellAsync(decimal amountTokens, CancellationToken ct);
    AppActionResult SetPerpLiveMode(bool live);

    // ── Signals + alerts ──
    Task<AppActionResult> ApplySignalToTicketAsync(string symbol, string side, CancellationToken ct);
    AppActionResult AddPriceAlert(string symbol, decimal price, bool above);

    // ── Bots / settings / wallet ──
    AppActionResult ConfigureGridBot(string symbol, decimal lower, decimal upper, int levels);
    AppActionResult SelectWalletNetwork(string network);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI -c Debug`
Expected: 0 errors (interface only).

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/IAppActionContext.cs
git commit -m "feat(actions): define IAppActionContext bridge interface"
```

---

## Task 3: Fake context (test double)

**Files:**
- Create: `CryptoAITerminal.Core.Tests/AppActions/FakeAppActionContext.cs`

- [ ] **Step 1: Write the fake** (records calls, returns canned data — used by all action tests)

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;

namespace CryptoAITerminal.Core.Tests.AppActions;

/// <summary>Records every call so action tests can assert the right bridge method fired.</summary>
public sealed class FakeAppActionContext : IAppActionContext
{
    public readonly List<string> Calls = new();
    public decimal BalanceUsdt = 1000m;
    public ActionMarketSnapshot? Market = new("BTCUSDT", 99_990m, 100_010m, 100_000m);
    public IReadOnlyList<ActionPositionLine> Positions = new List<ActionPositionLine>();

    private AppActionResult Log(string call) { Calls.Add(call); return AppActionResult.Ok(call); }

    public IReadOnlyList<string> KnownSections => new[] { "trading", "aisignals", "markets", "portfolio", "bots", "settings" };
    public AppActionResult NavigateTo(string s) => Log($"NavigateTo:{s}");
    public Task<decimal> GetBalanceUsdtAsync(CancellationToken ct) { Calls.Add("GetBalance"); return Task.FromResult(BalanceUsdt); }
    public Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct) { Calls.Add("GetPositions"); return Task.FromResult(Positions); }
    public Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct) { Calls.Add($"GetMarket:{symbol}"); return Task.FromResult(Market); }

    public AppActionResult SetTradingSymbol(string s) => Log($"SetSymbol:{s}");
    public AppActionResult SetTicketSide(bool b) => Log($"SetSide:{(b ? "buy" : "sell")}");
    public AppActionResult SetOrderType(string t) => Log($"SetOrderType:{t}");
    public AppActionResult SetMarketMode(string m) => Log($"SetMarketMode:{m}");
    public AppActionResult SetQuantity(decimal q) => Log($"SetQty:{q}");
    public AppActionResult SetUsdNotional(decimal u) => Log($"SetUsd:{u}");
    public AppActionResult SetLeverage(int l) => Log($"SetLeverage:{l}");
    public AppActionResult SetLimitPrice(decimal p) => Log($"SetLimit:{p}");
    public AppActionResult SetTakeProfit(decimal p) => Log($"SetTp:{p}");
    public AppActionResult SetStopLoss(decimal p) => Log($"SetSl:{p}");
    public AppActionResult ArmLimit(bool b) => Log($"ArmLimit:{(b ? "buy" : "sell")}");
    public AppActionResult ArmTakeProfit() => Log("ArmTp");
    public AppActionResult ArmStopLoss() => Log("ArmSl");
    public Task<AppActionResult> PlaceMarketAsync(bool b, CancellationToken ct) => Task.FromResult(Log($"PlaceMarket:{(b ? "buy" : "sell")}"));
    public Task<AppActionResult> ClosePositionAsync(CancellationToken ct) => Task.FromResult(Log("Close"));
    public Task<AppActionResult> SelectDexTokenAsync(string t, CancellationToken ct) => Task.FromResult(Log($"SelectDex:{t}"));
    public Task<AppActionResult> DexBuyAsync(decimal a, CancellationToken ct) => Task.FromResult(Log($"DexBuy:{a}"));
    public Task<AppActionResult> DexSellAsync(decimal a, CancellationToken ct) => Task.FromResult(Log($"DexSell:{a}"));
    public AppActionResult SetPerpLiveMode(bool l) => Log($"PerpLive:{l}");
    public Task<AppActionResult> ApplySignalToTicketAsync(string s, string side, CancellationToken ct) => Task.FromResult(Log($"ApplySignal:{s}:{side}"));
    public AppActionResult AddPriceAlert(string s, decimal p, bool above) => Log($"Alert:{s}:{p}:{(above ? "above" : "below")}");
    public AppActionResult ConfigureGridBot(string s, decimal lo, decimal hi, int n) => Log($"Grid:{s}:{lo}:{hi}:{n}");
    public AppActionResult SelectWalletNetwork(string net) => Log($"Wallet:{net}");
}
```

- [ ] **Step 2: Build tests project**

Run: `dotnet build CryptoAITerminal.Core.Tests -c Debug`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.Core.Tests/AppActions/FakeAppActionContext.cs
git commit -m "test(actions): add FakeAppActionContext test double"
```

---

## Task 4: Navigation + read actions

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/NavigationActions.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/NavigationActionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using System.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class NavigationActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async System.Threading.Tasks.Task Goto_calls_NavigateTo_with_section()
    {
        var ctx = new FakeAppActionContext();
        var action = new NavGotoAction();
        var r = await action.ExecuteAsync(Args(new { section = "trading" }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("NavigateTo:trading", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Goto_is_not_mutating()
        => Assert.False(new NavGotoAction().IsMutating);

    [Fact]
    public async System.Threading.Tasks.Task ReadPositions_returns_count()
    {
        var ctx = new FakeAppActionContext
        {
            Positions = new[] { new ActionPositionLine("BTCUSDT", 0.1m, 100m, 110m, 1m) }
        };
        var r = await new ReadPositionsAction().ExecuteAsync(Args(new { }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("GetPositions", ctx.Calls);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter NavigationActionsTests`
Expected: FAIL — `NavGotoAction` not defined.

- [ ] **Step 3: Implement**

```csharp
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>Helpers for reading args off a JsonElement.</summary>
internal static class ArgReader
{
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    public static decimal Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : 0m;
    public static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    public static bool Bool(JsonElement e, string name, bool dflt = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : dflt;
}

public sealed class NavGotoAction : IAppAction
{
    public string Id => "nav.goto";
    public AppActionCategory Category => AppActionCategory.Navigation;
    public string Description => "Navigate to an app page. section is one of the known section keys (e.g. trading, aisignals, markets, portfolio, bots, settings).";
    public object ParamSchema => new { type = "object", properties = new { section = new { type = "string" } }, required = new[] { "section" } };
    public bool IsMutating => false;
    public string Preview(JsonElement a) => $"Open page: {ArgReader.Str(a, "section")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var section = ArgReader.Str(a, "section").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(section)) return Task.FromResult(AppActionResult.Fail("section required"));
        if (!ctx.KnownSections.Contains(section))
            return Task.FromResult(AppActionResult.Fail($"unknown section '{section}'. Known: {string.Join(", ", ctx.KnownSections)}"));
        return Task.FromResult(ctx.NavigateTo(section));
    }
}

public sealed class ReadBalanceAction : IAppAction
{
    public string Id => "read.balance";
    public AppActionCategory Category => AppActionCategory.Read;
    public string Description => "Get the account's available USDT balance.";
    public object ParamSchema => new { type = "object", properties = new { } };
    public bool IsMutating => false;
    public string Preview(JsonElement a) => "Read USDT balance";
    public async Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
        => AppActionResult.Ok($"USDT balance: {await ctx.GetBalanceUsdtAsync(ct)}");
}

public sealed class ReadPositionsAction : IAppAction
{
    public string Id => "read.positions";
    public AppActionCategory Category => AppActionCategory.Read;
    public string Description => "List open positions with quantity, entry, mark and unrealized P&L.";
    public object ParamSchema => new { type = "object", properties = new { } };
    public bool IsMutating => false;
    public string Preview(JsonElement a) => "Read open positions";
    public async Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var pos = await ctx.GetOpenPositionsAsync(ct);
        var detail = System.Text.Json.JsonSerializer.Serialize(pos);
        return AppActionResult.Ok($"{pos.Count} open position(s)", detail);
    }
}

public sealed class ReadMarketAction : IAppAction
{
    public string Id => "read.market";
    public AppActionCategory Category => AppActionCategory.Read;
    public string Description => "Get best bid/ask and last price for a symbol (e.g. BTCUSDT).";
    public object ParamSchema => new { type = "object", properties = new { symbol = new { type = "string" } }, required = new[] { "symbol" } };
    public bool IsMutating => false;
    public string Preview(JsonElement a) => $"Read market {ArgReader.Str(a, "symbol")}";
    public async Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var symbol = ArgReader.Str(a, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return AppActionResult.Fail("symbol required");
        var m = await ctx.GetMarketAsync(symbol, ct);
        return m is null ? AppActionResult.Fail($"no market for {symbol}")
                         : AppActionResult.Ok($"{m.Symbol} bid {m.Bid} ask {m.Ask} last {m.Last}");
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter NavigationActionsTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/NavigationActions.cs CryptoAITerminal.Core.Tests/AppActions/NavigationActionsTests.cs
git commit -m "feat(actions): navigation + read actions"
```

---

## Task 5: Trading actions (CEX ticket + orders + DEX/perp)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/TradingActions.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/TradingActionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using System.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class TradingActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async System.Threading.Tasks.Task SetTicket_applies_symbol_side_and_usd()
    {
        var ctx = new FakeAppActionContext();
        var r = await new SetTicketAction().ExecuteAsync(
            Args(new { symbol = "ETHUSDT", side = "long", usd = 500, leverage = 5, mode = "futures" }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("SetSymbol:ETHUSDT", ctx.Calls);
        Assert.Contains("SetSide:buy", ctx.Calls);
        Assert.Contains("SetUsd:500", ctx.Calls);
        Assert.Contains("SetLeverage:5", ctx.Calls);
        Assert.Contains("SetMarketMode:Futures", ctx.Calls);
    }

    [Fact]
    public void PlaceMarket_is_mutating() => Assert.True(new PlaceMarketAction().IsMutating);

    [Fact]
    public async System.Threading.Tasks.Task ArmLimit_sets_price_then_arms()
    {
        var ctx = new FakeAppActionContext();
        var r = await new ArmLimitAction().ExecuteAsync(Args(new { side = "buy", price = 3200 }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("SetLimit:3200", ctx.Calls);
        Assert.Contains("ArmLimit:buy", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task PlaceMarket_routes_side()
    {
        var ctx = new FakeAppActionContext();
        await new PlaceMarketAction().ExecuteAsync(Args(new { side = "sell" }), ctx, CancellationToken.None);
        Assert.Contains("PlaceMarket:sell", ctx.Calls);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter TradingActionsTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

internal static class SideParse
{
    public static bool IsBuy(string side) =>
        side.Trim().ToLowerInvariant() is "buy" or "long" or "b" or "bid";
}

/// <summary>Fills the trading ticket (non-mutating: just populates fields, no order sent).</summary>
public sealed class SetTicketAction : IAppAction
{
    public string Id => "trade.set_ticket";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description =>
        "Fill the trading ticket without placing an order. Provide symbol; optional side (buy/long or sell/short), " +
        "usd (notional) OR quantity, leverage, mode (spot/futures), order_type (market/limit), limit_price.";
    public object ParamSchema => new
    {
        type = "object",
        properties = new
        {
            symbol = new { type = "string" },
            side = new { type = "string" },
            usd = new { type = "number" },
            quantity = new { type = "number" },
            leverage = new { type = "number" },
            mode = new { type = "string" },
            order_type = new { type = "string" },
            limit_price = new { type = "number" }
        },
        required = new[] { "symbol" }
    };
    public bool IsMutating => false; // populating fields is reversible; placing is a separate action
    public string Preview(JsonElement a)
        => $"Set ticket: {ArgReader.Str(a, "symbol")} {ArgReader.Str(a, "side")} " +
           $"{(ArgReader.Dec(a, "usd") > 0 ? $"${ArgReader.Dec(a, "usd")}" : $"{ArgReader.Dec(a, "quantity")} units")}".Trim();

    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var symbol = ArgReader.Str(a, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return Task.FromResult(AppActionResult.Fail("symbol required"));
        ctx.SetTradingSymbol(symbol);

        var mode = ArgReader.Str(a, "mode");
        if (mode.Length > 0) ctx.SetMarketMode(mode.Equals("futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot");

        var side = ArgReader.Str(a, "side");
        if (side.Length > 0) ctx.SetTicketSide(SideParse.IsBuy(side));

        var lev = ArgReader.Int(a, "leverage");
        if (lev > 0) ctx.SetLeverage(lev);

        var orderType = ArgReader.Str(a, "order_type");
        if (orderType.Length > 0) ctx.SetOrderType(orderType.Equals("limit", StringComparison.OrdinalIgnoreCase) ? "Limit" : "Market");

        var limit = ArgReader.Dec(a, "limit_price");
        if (limit > 0) ctx.SetLimitPrice(limit);

        var usd = ArgReader.Dec(a, "usd");
        var qty = ArgReader.Dec(a, "quantity");
        if (usd > 0) ctx.SetUsdNotional(usd);
        else if (qty > 0) ctx.SetQuantity(qty);

        return Task.FromResult(AppActionResult.Ok($"Ticket set for {symbol}."));
    }
}

public sealed class ArmLimitAction : IAppAction
{
    public string Id => "trade.arm_limit";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Arm a LIMIT order on the current ticket. side buy/sell, price required.";
    public object ParamSchema => new { type = "object", properties = new { side = new { type = "string" }, price = new { type = "number" } }, required = new[] { "side", "price" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"Arm {ArgReader.Str(a, "side").ToUpperInvariant()} LIMIT @ {ArgReader.Dec(a, "price")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var price = ArgReader.Dec(a, "price");
        if (price <= 0) return Task.FromResult(AppActionResult.Fail("price must be > 0"));
        ctx.SetLimitPrice(price);
        return Task.FromResult(ctx.ArmLimit(SideParse.IsBuy(ArgReader.Str(a, "side"))));
    }
}

public sealed class ArmTpSlAction : IAppAction
{
    public string Id => "trade.arm_tp_sl";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Arm take-profit and/or stop-loss on the open position. Provide take_profit and/or stop_loss prices.";
    public object ParamSchema => new { type = "object", properties = new { take_profit = new { type = "number" }, stop_loss = new { type = "number" } } };
    public bool IsMutating => true;
    public string Preview(JsonElement a)
    {
        var tp = ArgReader.Dec(a, "take_profit"); var sl = ArgReader.Dec(a, "stop_loss");
        return $"Arm{(tp > 0 ? $" TP {tp}" : "")}{(sl > 0 ? $" SL {sl}" : "")}".Trim();
    }
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var tp = ArgReader.Dec(a, "take_profit"); var sl = ArgReader.Dec(a, "stop_loss");
        if (tp <= 0 && sl <= 0) return Task.FromResult(AppActionResult.Fail("provide take_profit and/or stop_loss"));
        if (tp > 0) { ctx.SetTakeProfit(tp); ctx.ArmTakeProfit(); }
        if (sl > 0) { ctx.SetStopLoss(sl); ctx.ArmStopLoss(); }
        return Task.FromResult(AppActionResult.Ok("Protection armed."));
    }
}

public sealed class PlaceMarketAction : IAppAction
{
    public string Id => "trade.place_market";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Place a MARKET order NOW on the current ticket. side buy/sell. Subject to risk + wallet gates.";
    public object ParamSchema => new { type = "object", properties = new { side = new { type = "string" } }, required = new[] { "side" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"PLACE MARKET {ArgReader.Str(a, "side").ToUpperInvariant()} on current ticket";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
        => ctx.PlaceMarketAsync(SideParse.IsBuy(ArgReader.Str(a, "side")), ct);
}

public sealed class ClosePositionAction : IAppAction
{
    public string Id => "trade.close";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Close the current open position at market.";
    public object ParamSchema => new { type = "object", properties = new { } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => "Close current position at market";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
        => ctx.ClosePositionAsync(ct);
}

public sealed class SetPerpModeAction : IAppAction
{
    public string Id => "perp.set_mode";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Set the DEX perps desk mode. live=true routes to real Hyperliquid (testnet default); false=paper.";
    public object ParamSchema => new { type = "object", properties = new { live = new { type = "boolean" } }, required = new[] { "live" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"Perp desk → {(ArgReader.Bool(a, "live") ? "LIVE" : "PAPER")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
        => Task.FromResult(ctx.SetPerpLiveMode(ArgReader.Bool(a, "live")));
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter TradingActionsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/TradingActions.cs CryptoAITerminal.Core.Tests/AppActions/TradingActionsTests.cs
git commit -m "feat(actions): trading ticket + order + perp actions"
```

---

## Task 6: Signal / alert + bot / wallet actions

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/SignalAlertActions.cs`
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/BotWalletActions.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/SignalBotActionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using System.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class SignalBotActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async System.Threading.Tasks.Task AddAlert_passes_symbol_price_direction()
    {
        var ctx = new FakeAppActionContext();
        await new AddAlertAction().ExecuteAsync(Args(new { symbol = "BTCUSDT", price = 120000, direction = "above" }), ctx, CancellationToken.None);
        Assert.Contains("Alert:BTCUSDT:120000:above", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task ApplySignal_forwards_symbol_and_side()
    {
        var ctx = new FakeAppActionContext();
        await new ApplySignalAction().ExecuteAsync(Args(new { symbol = "ETHUSDT", side = "long" }), ctx, CancellationToken.None);
        Assert.Contains("ApplySignal:ETHUSDT:long", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task GridBot_forwards_bounds()
    {
        var ctx = new FakeAppActionContext();
        await new ConfigureGridBotAction().ExecuteAsync(Args(new { symbol = "BTCUSDT", lower = 90000, upper = 110000, levels = 8 }), ctx, CancellationToken.None);
        Assert.Contains("Grid:BTCUSDT:90000:110000:8", ctx.Calls);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter SignalBotActionsTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement `SignalAlertActions.cs`**

```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

public sealed class AddAlertAction : IAppAction
{
    public string Id => "alert.add";
    public AppActionCategory Category => AppActionCategory.Signals;
    public string Description => "Add a price alert. symbol, price, direction ('above' or 'below').";
    public object ParamSchema => new { type = "object", properties = new { symbol = new { type = "string" }, price = new { type = "number" }, direction = new { type = "string" } }, required = new[] { "symbol", "price", "direction" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"Alert {ArgReader.Str(a, "symbol")} {ArgReader.Str(a, "direction")} {ArgReader.Dec(a, "price")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var symbol = ArgReader.Str(a, "symbol");
        var price = ArgReader.Dec(a, "price");
        if (string.IsNullOrWhiteSpace(symbol) || price <= 0) return Task.FromResult(AppActionResult.Fail("symbol and positive price required"));
        var above = ArgReader.Str(a, "direction").Trim().ToLowerInvariant() != "below";
        return Task.FromResult(ctx.AddPriceAlert(symbol, price, above));
    }
}

public sealed class ApplySignalAction : IAppAction
{
    public string Id => "signal.apply_to_ticket";
    public AppActionCategory Category => AppActionCategory.Signals;
    public string Description => "Load an AI-Signals idea into the trading ticket. symbol + side (long/short).";
    public object ParamSchema => new { type = "object", properties = new { symbol = new { type = "string" }, side = new { type = "string" } }, required = new[] { "symbol", "side" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"Load signal {ArgReader.Str(a, "symbol")} {ArgReader.Str(a, "side")} into ticket";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var symbol = ArgReader.Str(a, "symbol");
        var side = ArgReader.Str(a, "side");
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(side)) return Task.FromResult(AppActionResult.Fail("symbol and side required"));
        return ctx.ApplySignalToTicketAsync(symbol, side, ct);
    }
}
```

- [ ] **Step 4: Implement `BotWalletActions.cs`**

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

public sealed class ConfigureGridBotAction : IAppAction
{
    public string Id => "bot.configure_grid";
    public AppActionCategory Category => AppActionCategory.Bots;
    public string Description => "Configure a grid bot: symbol, lower, upper, levels.";
    public object ParamSchema => new { type = "object", properties = new { symbol = new { type = "string" }, lower = new { type = "number" }, upper = new { type = "number" }, levels = new { type = "number" } }, required = new[] { "symbol", "lower", "upper", "levels" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"Grid {ArgReader.Str(a, "symbol")} {ArgReader.Dec(a, "lower")}–{ArgReader.Dec(a, "upper")} × {ArgReader.Int(a, "levels")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var symbol = ArgReader.Str(a, "symbol");
        var lo = ArgReader.Dec(a, "lower"); var hi = ArgReader.Dec(a, "upper"); var n = ArgReader.Int(a, "levels");
        if (string.IsNullOrWhiteSpace(symbol) || lo <= 0 || hi <= lo || n < 2) return Task.FromResult(AppActionResult.Fail("need symbol, 0<lower<upper, levels>=2"));
        return Task.FromResult(ctx.ConfigureGridBot(symbol, lo, hi, n));
    }
}

public sealed class SelectWalletAction : IAppAction
{
    public string Id => "wallet.select_network";
    public AppActionCategory Category => AppActionCategory.Wallet;
    public string Description => "Select the active wallet network (e.g. Ethereum, BSC, Solana, Tron).";
    public object ParamSchema => new { type = "object", properties = new { network = new { type = "string" } }, required = new[] { "network" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"Select wallet network {ArgReader.Str(a, "network")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var net = ArgReader.Str(a, "network");
        return string.IsNullOrWhiteSpace(net)
            ? Task.FromResult(AppActionResult.Fail("network required"))
            : Task.FromResult(ctx.SelectWalletNetwork(net));
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter SignalBotActionsTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/SignalAlertActions.cs CryptoAITerminal.TerminalUI/Services/AppActions/BotWalletActions.cs CryptoAITerminal.Core.Tests/AppActions/SignalBotActionsTests.cs
git commit -m "feat(actions): signal/alert + bot/wallet actions"
```

---

## Task 7: Action registry

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/AppActionRegistry.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/AppActionRegistryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppActionRegistryTests
{
    [Fact]
    public void All_action_ids_are_unique()
    {
        var reg = AppActionRegistry.Default();
        var ids = reg.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_action_has_description_and_schema()
    {
        foreach (var a in AppActionRegistry.Default().All)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
            Assert.NotNull(a.ParamSchema);
        }
    }

    [Fact]
    public void Find_resolves_by_id()
    {
        var reg = AppActionRegistry.Default();
        Assert.NotNull(reg.Find("nav.goto"));
        Assert.Null(reg.Find("does.not.exist"));
    }

    [Fact]
    public void ToolName_replaces_dots_with_underscores()
        => Assert.Equal("nav_goto", AppActionRegistry.ToolName("nav.goto"));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppActionRegistryTests`
Expected: FAIL — `AppActionRegistry` not defined.

- [ ] **Step 3: Implement**

```csharp
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>The catalog of everything the AI can do. Pure — holds no app state.</summary>
public sealed class AppActionRegistry
{
    public IReadOnlyList<IAppAction> All { get; }
    private readonly Dictionary<string, IAppAction> _byId;

    public AppActionRegistry(IReadOnlyList<IAppAction> actions)
    {
        All = actions;
        _byId = actions.ToDictionary(a => a.Id, System.StringComparer.OrdinalIgnoreCase);
    }

    public IAppAction? Find(string id) => _byId.TryGetValue(id, out var a) ? a : null;

    /// <summary>Model tool names can't contain dots — map id "nav.goto" ⇄ "nav_goto".</summary>
    public static string ToolName(string id) => id.Replace('.', '_');
    public IAppAction? FindByToolName(string toolName) =>
        All.FirstOrDefault(a => ToolName(a.Id) == toolName);

    /// <summary>The full default action set.</summary>
    public static AppActionRegistry Default() => new(new IAppAction[]
    {
        new NavGotoAction(), new ReadBalanceAction(), new ReadPositionsAction(), new ReadMarketAction(),
        new SetTicketAction(), new ArmLimitAction(), new ArmTpSlAction(), new PlaceMarketAction(),
        new ClosePositionAction(), new SetPerpModeAction(),
        new AddAlertAction(), new ApplySignalAction(),
        new ConfigureGridBotAction(), new SelectWalletAction(),
    });
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppActionRegistryTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/AppActionRegistry.cs CryptoAITerminal.Core.Tests/AppActions/AppActionRegistryTests.cs
git commit -m "feat(actions): action registry with tool-name mapping"
```

---

## Task 8: Audit log

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/AppActionAuditLog.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/AppActionAuditLogTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppActionAuditLogTests
{
    [Fact]
    public void Append_then_reload_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{System.Guid.NewGuid():N}.json");
        try
        {
            var log = new AppActionAuditLog(path);
            log.Record("trade.place_market", "PLACE MARKET BUY", "proposed", null);
            log.Record("trade.place_market", "PLACE MARKET BUY", "executed", "ok");

            var reloaded = new AppActionAuditLog(path);
            Assert.Equal(2, reloaded.Recent(10).Count);
            Assert.Equal("executed", reloaded.Recent(1)[0].Outcome);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppActionAuditLogTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement** (reuse existing `AtomicJsonFile`)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

public sealed record AuditEntry(DateTime AtUtc, string ActionId, string Preview, string Outcome, string? Detail);

/// <summary>Persisted trail of every proposed/approved/executed/failed action (bounded).</summary>
public sealed class AppActionAuditLog
{
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly List<AuditEntry> _entries;

    public AppActionAuditLog(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal", "agent-actions.json");
        _entries = TryLoad();
    }

    public void Record(string actionId, string preview, string outcome, string? detail)
    {
        _entries.Add(new AuditEntry(DateTime.UtcNow, actionId, preview, outcome, detail));
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        try { Services.AtomicJsonFile.Write(_path, _entries); } catch { /* non-fatal */ }
    }

    public IReadOnlyList<AuditEntry> Recent(int n) =>
        _entries.AsEnumerable().Reverse().Take(n).ToList();

    private List<AuditEntry> TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return Services.AtomicJsonFile.Read<List<AuditEntry>>(_path) ?? new();
        }
        catch { return new(); }
    }
}
```

> Namespace note: `AtomicJsonFile` lives in `CryptoAITerminal.TerminalUI.Services`; from inside `...Services.AppActions` reference it as `Services.AtomicJsonFile` (as written) or add `using CryptoAITerminal.TerminalUI.Services;`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppActionAuditLogTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/AppActionAuditLog.cs CryptoAITerminal.Core.Tests/AppActions/AppActionAuditLogTests.cs
git commit -m "feat(actions): persisted action audit log"
```

---

## Task 9: Agent service with confirm/auto routing

The service exposes read actions as immediately-executing `AgentTool`s and mutating actions as *proposals*
delivered to an injected sink. Testable without network by calling the internal tool delegate directly.

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/AppAgentService.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/AppAgentServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppAgentServiceTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task Confirm_mode_queues_mutating_action_and_does_not_execute()
    {
        var ctx = new FakeAppActionContext();
        var proposals = new List<(string id, string preview)>();
        var svc = new AppAgentService(AppActionRegistry.Default(), ctx,
            proposalSink: p => { proposals.Add((p.ActionId, p.Preview)); return Task.FromResult(AppActionResult.Ok("queued")); },
            autoMode: () => false);

        var result = await svc.InvokeActionToolAsync("trade_place_market", Args(new { side = "buy" }), CancellationToken.None);

        Assert.Single(proposals);
        Assert.Equal("trade.place_market", proposals[0].id);
        Assert.DoesNotContain("PlaceMarket:buy", ctx.Calls); // not executed
        Assert.Contains("queued", result);
    }

    [Fact]
    public async Task Auto_mode_executes_mutating_action_immediately()
    {
        var ctx = new FakeAppActionContext();
        var svc = new AppAgentService(AppActionRegistry.Default(), ctx,
            proposalSink: _ => Task.FromResult(AppActionResult.Ok("queued")),
            autoMode: () => true);

        await svc.InvokeActionToolAsync("trade_place_market", Args(new { side = "buy" }), CancellationToken.None);

        Assert.Contains("PlaceMarket:buy", ctx.Calls);
    }

    [Fact]
    public async Task Read_action_executes_regardless_of_mode()
    {
        var ctx = new FakeAppActionContext();
        var svc = new AppAgentService(AppActionRegistry.Default(), ctx,
            proposalSink: _ => Task.FromResult(AppActionResult.Ok("queued")),
            autoMode: () => false);

        await svc.InvokeActionToolAsync("read_balance", Args(new { }), CancellationToken.None);

        Assert.Contains("GetBalance", ctx.Calls);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppAgentServiceTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine.Agent;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>A mutating action awaiting user approval.</summary>
public sealed record ActionProposal(string ActionId, string Preview, JsonElement Args);

/// <summary>
/// Agentic replacement for the read-only copilot loop. Read actions run immediately;
/// mutating actions either queue as a proposal (CONFIRM) or run now (AUTO). Money-gates
/// live in the context implementation, not here.
/// </summary>
public sealed class AppAgentService
{
    private readonly AppActionRegistry _registry;
    private readonly IAppActionContext _ctx;
    private readonly Func<ActionProposal, Task<AppActionResult>> _proposalSink;
    private readonly Func<bool> _autoMode;
    private readonly AppActionAuditLog? _audit;

    public AppAgentService(
        AppActionRegistry registry,
        IAppActionContext ctx,
        Func<ActionProposal, Task<AppActionResult>> proposalSink,
        Func<bool> autoMode,
        AppActionAuditLog? audit = null)
    {
        _registry = registry;
        _ctx = ctx;
        _proposalSink = proposalSink;
        _autoMode = autoMode;
        _audit = audit;
    }

    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    private string Key => ApiKey ?? CryptoAITerminal.AIEngine.AiRuntime.ActiveApiKey;
    private string Mdl => Model ?? CryptoAITerminal.AIEngine.AiRuntime.ActiveModel;
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(Key);
    public event Action<AgentEvent>? OnEvent;

    /// <summary>Build one AgentTool per action; the delegate routes through <see cref="InvokeActionToolAsync"/>.</summary>
    public IReadOnlyList<AgentTool> BuildTools() =>
        _registry.All.Select(a => new AgentTool(
            AppActionRegistry.ToolName(a.Id),
            a.Description + (a.IsMutating ? " (mutating — needs user confirmation unless auto mode is on)" : ""),
            a.ParamSchema,
            (input, ct) => InvokeActionToolAsync(AppActionRegistry.ToolName(a.Id), input, ct))).ToList();

    /// <summary>Core routing seam (also called directly by unit tests). Returns the model-facing string.</summary>
    public async Task<string> InvokeActionToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var action = _registry.FindByToolName(toolName);
        if (action is null) return Json(AppActionResult.Fail($"unknown action '{toolName}'"));

        var preview = SafePreview(action, args);

        if (action.IsMutating && !_autoMode())
        {
            _audit?.Record(action.Id, preview, "proposed", null);
            var queued = await _proposalSink(new ActionProposal(action.Id, preview, args)).ConfigureAwait(false);
            return Json(AppActionResult.Ok($"Proposed for confirmation: {preview}. {queued.Message}"));
        }

        AppActionResult result;
        try { result = await action.ExecuteAsync(args, _ctx, ct).ConfigureAwait(false); }
        catch (Exception ex) { result = AppActionResult.Fail(ex.Message); }

        _audit?.Record(action.Id, preview, result.Success ? "executed" : "failed", result.Detail ?? result.Message);
        return Json(result);
    }

    /// <summary>Approve a queued proposal — executes it now (called by the tray on user OK).</summary>
    public async Task<AppActionResult> ExecuteApprovedAsync(ActionProposal p, CancellationToken ct = default)
    {
        var action = _registry.Find(p.ActionId);
        if (action is null) return AppActionResult.Fail($"unknown action '{p.ActionId}'");
        AppActionResult result;
        try { result = await action.ExecuteAsync(p.Args, _ctx, ct).ConfigureAwait(false); }
        catch (Exception ex) { result = AppActionResult.Fail(ex.Message); }
        _audit?.Record(action.Id, p.Preview, result.Success ? "executed" : "failed", result.Detail ?? result.Message);
        return result;
    }

    /// <summary>Run one natural-language turn (network). Falls back to a plain message when no key.</summary>
    public async Task<string> RunTurnAsync(string instruction, CancellationToken ct = default)
    {
        if (!UsesLiveModel)
            return "Add a Claude/OpenAI API key in the AI Bot panel to let the assistant act. (Navigation and reads still need a key here.)";
        var runner = AgentRunnerFactory.Create(Key, Mdl, maxIterations: 10);
        var result = await runner.RunAsync(SystemPrompt(), instruction, BuildTools(), OnEvent, ct).ConfigureAwait(false);
        return result.FinalText;
    }

    private static string SystemPrompt() =>
        "You are the built-in agent for CryptoAI Terminal. You can navigate the app, read account/market data, " +
        "and (with the user's confirmation) fill tickets, arm and place orders, set alerts, apply signals, and configure bots. " +
        "Use read/navigation tools freely. For any mutating tool, the app will ask the user to approve unless auto mode is on — " +
        "call the tool and tell the user what you proposed. Never claim an order filled unless the tool result says it did. " +
        "Be concise. Not financial advice.";

    private static string SafePreview(IAppAction a, JsonElement args)
    {
        try { return a.Preview(args); } catch { return a.Id; }
    }

    private static string Json(AppActionResult r) => JsonSerializer.Serialize(new { ok = r.Success, message = r.Message, detail = r.Detail });
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AppAgentServiceTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/AppAgentService.cs CryptoAITerminal.Core.Tests/AppActions/AppAgentServiceTests.cs
git commit -m "feat(actions): AppAgentService with confirm/auto routing"
```

---

## Task 10: Real context bridge (`MainWindowAppActionContext`)

Adapts the interface to the live view models. No new business logic — calls existing commands/methods on the UI thread.

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppActions/MainWindowAppActionContext.cs`

> Before writing, confirm the exact members used below exist (they were verified during design; re-grep if a signature drifted):
> `MainWindowViewModel`: `SelectMainTabCommand` (ReactiveCommand<string,Unit>), `SelectedTradingSymbol`, ticket setters
> `SelectedOrderType`/`SelectedCexMarketMode`/`TradeQuantity`/`ManualFuturesLeverage`/`LimitPrice`/`TakeProfitPrice`/`StopLossPrice`,
> commands `PlaceBuyLimit`/`PlaceSellLimit`/`ArmTakeProfit`/`ArmStopLoss`, methods `ExecuteBuyMarket`/`ExecuteSellMarket`/`ExecuteClosePosition`,
> `AvailableBalanceUsdt`, `AlertsVM.AddAlertCommand`, `DexDeskVM.Perp.ToggleLiveTradingCommand`, `WalletVM`/`GridBotVM`.
> Where a command is `private`, expose a thin `internal` method on `MainWindowViewModel` for the bridge (small, mechanical) rather than duplicating logic.

- [ ] **Step 1: Implement the bridge** (UI-thread marshalled)

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>Bridges IAppActionContext onto the live MainWindowViewModel graph.</summary>
public sealed class MainWindowAppActionContext : IAppActionContext
{
    private readonly MainWindowViewModel _vm;
    public MainWindowAppActionContext(MainWindowViewModel vm) => _vm = vm;

    private static Task<T> Ui<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();
    private static AppActionResult UiR(Func<AppActionResult> f) => Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f).GetTask().GetAwaiter().GetResult();

    public IReadOnlyList<string> KnownSections => _vm.KnownSectionKeys; // add: expose the section-key list on the VM

    public AppActionResult NavigateTo(string sectionKey)
    {
        Dispatcher.UIThread.Post(() => _vm.SelectMainTabCommand.Execute(sectionKey).Subscribe());
        return AppActionResult.Ok($"Opened {sectionKey}.");
    }

    public async Task<decimal> GetBalanceUsdtAsync(CancellationToken ct) => await Ui(() => _vm.AvailableBalanceUsdt);

    public async Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct)
        => await Ui<IReadOnlyList<ActionPositionLine>>(() => _vm.PositionQuantity == 0
            ? new List<ActionPositionLine>()
            : new List<ActionPositionLine> { new(_vm.SelectedTradingSymbol, _vm.PositionQuantity, _vm.AverageEntryPrice, _vm.CurrentTradePrice, _vm.UnrealizedPnl) });

    public async Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct)
        => await Ui<ActionMarketSnapshot?>(() =>
        {
            var row = _vm.Markets.FirstOrDefault(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            return row is null ? null : new ActionMarketSnapshot(row.Symbol, row.BestBid, row.BestAsk, row.LastPrice);
        });

    public AppActionResult SetTradingSymbol(string symbol) => UiR(() => _vm.TrySelectTradingSymbol(symbol));   // add thin VM helper
    public AppActionResult SetTicketSide(bool isBuy) => AppActionResult.Ok(isBuy ? "side=buy" : "side=sell");   // side is chosen at place-time; store on VM if needed
    public AppActionResult SetOrderType(string type) => UiR(() => { _vm.SelectOrderTypeFromAgent(type); return AppActionResult.Ok($"order type {type}"); });
    public AppActionResult SetMarketMode(string mode) => UiR(() => { _vm.SelectMarketModeFromAgent(mode); return AppActionResult.Ok($"mode {mode}"); });
    public AppActionResult SetQuantity(decimal q) => UiR(() => { _vm.TradeQuantity = q; return AppActionResult.Ok($"qty {q}"); });
    public AppActionResult SetUsdNotional(decimal usd) => UiR(() => { _vm.SetTradeUsdFromAgent(usd); return AppActionResult.Ok($"~${usd}"); });
    public AppActionResult SetLeverage(int lev) => UiR(() => { _vm.ManualFuturesLeverage = lev; return AppActionResult.Ok($"{lev}x"); });
    public AppActionResult SetLimitPrice(decimal p) => UiR(() => { _vm.LimitPrice = p; return AppActionResult.Ok($"limit {p}"); });
    public AppActionResult SetTakeProfit(decimal p) => UiR(() => { _vm.TakeProfitPrice = p; return AppActionResult.Ok($"tp {p}"); });
    public AppActionResult SetStopLoss(decimal p) => UiR(() => { _vm.StopLossPrice = p; return AppActionResult.Ok($"sl {p}"); });
    public AppActionResult ArmLimit(bool isBuy) => UiR(() => _vm.ArmLimitFromAgent(isBuy));
    public AppActionResult ArmTakeProfit() => UiR(() => _vm.ArmTakeProfitFromAgent());
    public AppActionResult ArmStopLoss() => UiR(() => _vm.ArmStopLossFromAgent());
    public async Task<AppActionResult> PlaceMarketAsync(bool isBuy, CancellationToken ct) => await Ui(() => _vm.PlaceMarketFromAgent(isBuy)).Unwrap();
    public async Task<AppActionResult> ClosePositionAsync(CancellationToken ct) => await Ui(() => _vm.ClosePositionFromAgent()).Unwrap();

    public async Task<AppActionResult> SelectDexTokenAsync(string t, CancellationToken ct) => await Ui(() => _vm.DexDeskVM.Swap.SelectTokenByAddressAsync(t)).Unwrap().ContinueWith(_ => AppActionResult.Ok($"selected {t}"));
    public async Task<AppActionResult> DexBuyAsync(decimal a, CancellationToken ct) => await Ui(() => _vm.DexBuyFromAgent(a)).Unwrap();
    public async Task<AppActionResult> DexSellAsync(decimal a, CancellationToken ct) => await Ui(() => _vm.DexSellFromAgent(a)).Unwrap();
    public AppActionResult SetPerpLiveMode(bool live) => UiR(() => _vm.SetPerpLiveFromAgent(live));

    public async Task<AppActionResult> ApplySignalToTicketAsync(string s, string side, CancellationToken ct) => await Ui(() => _vm.ApplySignalToTicketFromAgent(s, side)).Unwrap();
    public AppActionResult AddPriceAlert(string s, decimal p, bool above) => UiR(() => _vm.AddPriceAlertFromAgent(s, p, above));
    public AppActionResult ConfigureGridBot(string s, decimal lo, decimal hi, int n) => UiR(() => _vm.ConfigureGridBotFromAgent(s, lo, hi, n));
    public AppActionResult SelectWalletNetwork(string net) => UiR(() => _vm.SelectWalletNetworkFromAgent(net));
}
```

> These `*FromAgent` methods are thin `internal` wrappers you add to the VMs, each delegating to the existing
> private command/method and returning an `AppActionResult`. Add them in Task 11 alongside wiring, one per line,
> mirroring the existing manual command (e.g. `internal AppActionResult ArmTakeProfitFromAgent() { if (PositionQuantity==0) return AppActionResult.Fail("no position"); ArmTakeProfit(); return AppActionResult.Ok("TP armed"); }`).

- [ ] **Step 2: Build** (will fail until Task 11 adds the VM helpers — that's expected; proceed to Task 11 in the same branch)

Run: `dotnet build CryptoAITerminal.TerminalUI -c Debug`
Expected: errors naming the missing `*FromAgent` helpers (drives Task 11).

- [ ] **Step 3: Commit (WIP allowed here since 10+11 are one unit)**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppActions/MainWindowAppActionContext.cs
git commit -m "feat(actions): MainWindowAppActionContext bridge (VM helpers follow)"
```

---

## Task 11: VM helper methods + section-key exposure

Add the thin `internal *FromAgent` wrappers + `KnownSectionKeys` to the view models so the bridge compiles. Each wrapper reuses the existing command and returns a result; NO duplicated trading logic.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/VmAgentHelpersTests.cs` (light — asserts guards)

- [ ] **Step 1: Add `KnownSectionKeys` + agent helpers to `MainWindowViewModel`**

Add a new region. Reuse `NormalizeSectionKey` for the section list. Example helpers (add the full set the bridge references):

```csharp
// ── Agent action bridge helpers (thin wrappers over existing commands) ──
public IReadOnlyList<string> KnownSectionKeys { get; } = new[]
{
    "dashboard","markets","trading","aisignals","sniper","portfolio","positions",
    "risk","bots","rules","backtest","journal","funding","arb","copy","statarb",
    "news","onchain","tape","settings"
};

internal Services.AppActions.AppActionResult TrySelectTradingSymbol(string symbol)
{
    var row = Markets.FirstOrDefault(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    if (row is null) return Services.AppActions.AppActionResult.Fail($"unknown symbol {symbol}");
    SelectedMarket = row;
    return Services.AppActions.AppActionResult.Ok($"symbol {symbol}");
}

internal void SelectOrderTypeFromAgent(string type) => SelectedOrderType = type.Equals("limit", StringComparison.OrdinalIgnoreCase) ? "Limit" : "Market";
internal void SelectMarketModeFromAgent(string mode) => SelectedCexMarketMode = mode.Equals("futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";
internal void SetTradeUsdFromAgent(decimal usd) { var px = CurrentTradePrice; if (px > 0) TradeQuantity = Math.Round(usd / px, 6); }

internal Services.AppActions.AppActionResult ArmLimitFromAgent(bool isBuy)
{
    if (LimitPrice <= 0 || TradeQuantity <= 0) return Services.AppActions.AppActionResult.Fail("set limit price and quantity first");
    if (isBuy) PlaceBuyLimit(); else PlaceSellLimit();
    return Services.AppActions.AppActionResult.Ok($"{(isBuy ? "BUY" : "SELL")} LIMIT armed at {LimitPrice}");
}
internal Services.AppActions.AppActionResult ArmTakeProfitFromAgent()
{
    if (PositionQuantity == 0) return Services.AppActions.AppActionResult.Fail("no open position");
    ArmTakeProfit(); return Services.AppActions.AppActionResult.Ok("TP armed");
}
internal Services.AppActions.AppActionResult ArmStopLossFromAgent()
{
    if (PositionQuantity == 0) return Services.AppActions.AppActionResult.Fail("no open position");
    ArmStopLoss(); return Services.AppActions.AppActionResult.Ok("SL armed");
}
internal async Task<Services.AppActions.AppActionResult> PlaceMarketFromAgent(bool isBuy)
{
    if (isBuy) await ExecuteBuyMarket(); else await ExecuteSellMarket();
    return Services.AppActions.AppActionResult.Ok($"market {(isBuy ? "buy" : "sell")} submitted");
}
internal async Task<Services.AppActions.AppActionResult> ClosePositionFromAgent()
{
    if (PositionQuantity == 0) return Services.AppActions.AppActionResult.Fail("no position");
    await ExecuteClosePosition(); return Services.AppActions.AppActionResult.Ok("close submitted");
}
internal async Task<Services.AppActions.AppActionResult> DexBuyFromAgent(decimal amountNative)
{ /* call DexDeskVM.Swap buy command path; guard on wallet */ return await DexDeskVM.Swap.BuyFromAgentAsync(amountNative); }
internal async Task<Services.AppActions.AppActionResult> DexSellFromAgent(decimal amountTokens)
{ return await DexDeskVM.Swap.SellFromAgentAsync(amountTokens); }
internal Services.AppActions.AppActionResult SetPerpLiveFromAgent(bool live)
{
    if (live == DexDeskVM.Perp.IsLiveTrading) return Services.AppActions.AppActionResult.Ok($"perp already {(live ? "live" : "paper")}");
    DexDeskVM.Perp.ToggleLiveTradingCommand.Execute().Subscribe();
    return Services.AppActions.AppActionResult.Ok($"perp mode → {(DexDeskVM.Perp.IsLiveTrading ? "LIVE" : "PAPER")}");
}
internal async Task<Services.AppActions.AppActionResult> ApplySignalToTicketFromAgent(string symbol, string side)
{
    var r = TrySelectTradingSymbol(symbol);
    if (!r.Success) return r;
    SelectMainTab("trading");
    return Services.AppActions.AppActionResult.Ok($"loaded {symbol} {side} into ticket");
}
internal Services.AppActions.AppActionResult AddPriceAlertFromAgent(string symbol, decimal price, bool above)
{
    AlertsVM.NewAlertSymbol = symbol; AlertsVM.NewAlertThreshold = price;
    AlertsVM.SelectedCondition = above ? "PriceAbove" : "PriceBelow";
    AlertsVM.AddAlertCommand.Execute().Subscribe();
    return Services.AppActions.AppActionResult.Ok($"alert {symbol} {(above ? "above" : "below")} {price}");
}
internal Services.AppActions.AppActionResult ConfigureGridBotFromAgent(string symbol, decimal lo, decimal hi, int n)
{ return GridBotVM.ConfigureFromAgent(symbol, lo, hi, n); }        // add a matching helper on GridBotViewModel
internal Services.AppActions.AppActionResult SelectWalletNetworkFromAgent(string net)
{ return WalletVM.SelectNetworkFromAgent(net); }                    // add a matching helper on WalletWorkspaceViewModel
```

> For `DexDeskVM.Swap.BuyFromAgentAsync/SellFromAgentAsync`, `GridBotVM.ConfigureFromAgent`,
> `WalletVM.SelectNetworkFromAgent`: add one thin `internal` method on each VM that reuses its existing
> command/method and returns `AppActionResult`, mirroring the wrappers above. If a target VM lacks a public
> setter you need, add a minimal `internal` mutator — do not reimplement logic.

- [ ] **Step 2: Write a light guard test**

```csharp
// VmAgentHelpersTests: construct MainWindowViewModel is heavy; instead assert the pure guards you can reach.
// If MainWindowViewModel can't be constructed headless, cover the guard logic via the actions (already tested
// with FakeAppActionContext) and rely on the build + manual e2e for the bridge. Document that here.
```

Note: `MainWindowViewModel` may not construct headless (it starts timers/streams). If so, skip a direct unit test here — the action layer is already covered via `FakeAppActionContext`, and the bridge is verified by build + manual e2e. Record this decision in the commit message.

- [ ] **Step 3: Build the whole UI project**

Run: `dotnet build CryptoAITerminal.TerminalUI -c Debug`
Expected: 0 errors (bridge + helpers now resolve).

- [ ] **Step 4: Run full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests`
Expected: all prior tests + new ones PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(actions): VM agent-bridge helpers + section keys"
```

---

## Task 12: Action tray view model

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/AgentActionTrayViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/AppActions/AgentActionTrayTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AgentActionTrayTests
{
    private static ActionProposal P() => new("trade.place_market", "PLACE MARKET BUY", JsonSerializer.SerializeToElement(new { side = "buy" }));

    [Fact]
    public async Task Enqueue_adds_pending_and_approve_executes()
    {
        var executed = 0;
        var tray = new AgentActionTrayViewModel(p => { executed++; return Task.FromResult(AppActionResult.Ok("done")); });
        tray.Enqueue(P());
        Assert.Single(tray.Pending);

        await tray.ApproveAsync(tray.Pending[0]);
        Assert.Equal(1, executed);
        Assert.Empty(tray.Pending);
    }

    [Fact]
    public void Reject_removes_without_executing()
    {
        var executed = 0;
        var tray = new AgentActionTrayViewModel(p => { executed++; return Task.FromResult(AppActionResult.Ok("done")); });
        tray.Enqueue(P());
        tray.Reject(tray.Pending[0]);
        Assert.Empty(tray.Pending);
        Assert.Equal(0, executed);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AgentActionTrayTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public sealed class AgentActionProposalViewModel : ReactiveObject
{
    public AgentActionProposalViewModel(ActionProposal proposal) { Proposal = proposal; }
    public ActionProposal Proposal { get; }
    public string ActionId => Proposal.ActionId;
    public string Preview => Proposal.Preview;
}

/// <summary>Holds pending mutating proposals; Approve executes via the injected executor, Reject drops.</summary>
public sealed class AgentActionTrayViewModel : ReactiveObject
{
    private readonly Func<ActionProposal, Task<AppActionResult>> _execute;
    public AgentActionTrayViewModel(Func<ActionProposal, Task<AppActionResult>> execute) => _execute = execute;

    public ObservableCollection<AgentActionProposalViewModel> Pending { get; } = new();
    public bool HasPending => Pending.Count > 0;

    public void Enqueue(ActionProposal p)
    {
        Pending.Add(new AgentActionProposalViewModel(p));
        this.RaisePropertyChanged(nameof(HasPending));
    }

    public async Task<AppActionResult> ApproveAsync(AgentActionProposalViewModel vm)
    {
        Pending.Remove(vm);
        this.RaisePropertyChanged(nameof(HasPending));
        return await _execute(vm.Proposal);
    }

    public void Reject(AgentActionProposalViewModel vm)
    {
        Pending.Remove(vm);
        this.RaisePropertyChanged(nameof(HasPending));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter AgentActionTrayTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/AgentActionTrayViewModel.cs CryptoAITerminal.Core.Tests/AppActions/AgentActionTrayTests.cs
git commit -m "feat(actions): action tray view model"
```

---

## Task 13: Wire the agent into Copilot + tray UI + AUTO toggle

Upgrade `CopilotViewModel` to route through `AppAgentService`, surface the tray and an AUTO toggle.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/CopilotViewModel.cs`
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` (construct the agent + tray, pass into Copilot)
- Create: `CryptoAITerminal.TerminalUI/Views/AgentActionTrayView.axaml` (+ `.cs`)
- Modify: the Copilot panel XAML to host the tray + a "AUTO mode" ToggleSwitch bound to `IsAutoMode`.

- [ ] **Step 1: Extend `CopilotViewModel`**

Add:
```csharp
private readonly AppAgentService? _agent;
private bool _isAutoMode;
public AgentActionTrayViewModel? Tray { get; }
public bool IsAutoMode { get => _isAutoMode; set => this.RaiseAndSetIfChanged(ref _isAutoMode, value); }
public bool CanAct => _agent is not null;
```
- Add a constructor overload `CopilotViewModel(CopilotAgentService.CopilotDataSource data, AppAgentService agent, AgentActionTrayViewModel tray)` that stores them; keep the existing constructor for back-compat.
- In `AskAsync`, when `_agent is not null && _agent.UsesLiveModel`, call `await _agent.RunTurnAsync(q, ct)` instead of the read-only service; append a note if the tray has pending proposals ("N action(s) awaiting your approval below").
- Enabling `IsAutoMode` requires a consent dialog: gate the setter so turning it ON raises an event the view handles to show a confirm; only commit `true` if confirmed (implement via an `Interaction<Unit,bool>` or a simple callback passed from the view).

- [ ] **Step 2: Construct in `MainWindowViewModel`**

Where `CopilotVM` is currently created, build the chain:
```csharp
var actionCtx = new Services.AppActions.MainWindowAppActionContext(this);
var registry = Services.AppActions.AppActionRegistry.Default();
var audit = new Services.AppActions.AppActionAuditLog();
AgentTray = new AgentActionTrayViewModel(p => _appAgent!.ExecuteApprovedAsync(p));
_appAgent = new Services.AppActions.AppAgentService(
    registry, actionCtx,
    proposalSink: p => { Dispatcher.UIThread.Post(() => AgentTray.Enqueue(p)); return Task.FromResult(Services.AppActions.AppActionResult.Ok("queued for approval")); },
    autoMode: () => CopilotVM.IsAutoMode,
    audit: audit);
CopilotVM = new CopilotViewModel(copilotDataSource, _appAgent, AgentTray);
```
Add fields `private Services.AppActions.AppAgentService? _appAgent;` and `public AgentActionTrayViewModel AgentTray { get; private set; }`.
Ensure `ConfigureAi` also forwards the key/model to `_appAgent` (set `_appAgent.ApiKey/_appAgent.Model`).

- [ ] **Step 3: Create `AgentActionTrayView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="CryptoAITerminal.TerminalUI.Views.AgentActionTrayView"
             x:CompileBindings="False">
  <Border Background="#0a1622" CornerRadius="6" Padding="8" IsVisible="{Binding HasPending}">
    <StackPanel Spacing="6">
      <TextBlock Text="AGENT WANTS TO:" Foreground="#f4b860" FontWeight="Bold" FontSize="11" />
      <ItemsControl ItemsSource="{Binding Pending}">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <Grid ColumnDefinitions="*,Auto,Auto" Margin="0,2">
              <TextBlock Text="{Binding Preview}" Foreground="#c8dcef" TextWrapping="Wrap" VerticalAlignment="Center" />
              <Button Grid.Column="1" Content="✓" Foreground="#3ddc84" Margin="4,0"
                      Command="{Binding $parent[ItemsControl].DataContext.ApproveCommand}" CommandParameter="{Binding}" />
              <Button Grid.Column="2" Content="✕" Foreground="#ff6b6b"
                      Command="{Binding $parent[ItemsControl].DataContext.RejectCommand}" CommandParameter="{Binding}" />
            </Grid>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </StackPanel>
  </Border>
</UserControl>
```
Add `ApproveCommand`/`RejectCommand` (`ReactiveCommand<AgentActionProposalViewModel,Unit>`) to `AgentActionTrayViewModel` calling `ApproveAsync`/`Reject`.

- [ ] **Step 4: Host tray + AUTO toggle in the Copilot panel**

In the Copilot view XAML (the panel bound to `CopilotVM`), add above the input box:
```xml
<views:AgentActionTrayView DataContext="{Binding AgentTray}" IsVisible="{Binding $parent[Window].DataContext.AgentTray.HasPending}" />
<ToggleSwitch Content="AUTO (agent acts without asking)" IsChecked="{Binding IsAutoMode}" OffContent="AUTO off" OnContent="AUTO ON · testnet-first" />
```

- [ ] **Step 5: Build + full test suite + launch smoke**

Run: `dotnet build CryptoAITerminal.TerminalUI -c Debug` → 0 errors.
Run: `dotnet test CryptoAITerminal.Core.Tests` → all green.
Manual: launch app, open Copilot, type "открой маркеты" → navigates; "поставь лонг BTC 100$ спот" → proposal appears in tray → Approve → ticket fills. Toggle AUTO → consent dialog.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(copilot): agentic copilot with action tray + AUTO toggle"
```

---

## Task 14: Docs + audit surfacing (optional polish)

**Files:**
- Modify: `TRADING_AUDIT.md` / `SETUP.md` — note the agent needs an AI key.
- Optionally surface recent `AppActionAuditLog` entries in the Logs section.

- [ ] **Step 1:** Add a short "AI Agent" paragraph to `SETUP.md` (needs `ANTHROPIC_API_KEY`/`OPENAI_API_KEY`; CONFIRM default, AUTO opt-in + testnet-first).
- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "docs: document the agentic copilot + keys"
```

---

## Self-Review Notes (author)

- **Spec coverage:** action layer (Tasks 1–8), agent + confirm/auto (Task 9), safety gates preserved (bridge Tasks 10–11 reuse gated commands), audit log (Task 8), tray + UI + AUTO consent (Tasks 12–13). Navigation/read/trading/signals/bots/wallet domains all have actions. ✅
- **Money-gate guarantee:** the bridge calls existing `ExecuteBuyMarket`/`PlaceBuyLimit`/DEX/perp commands which already run `RiskManager`/`WalletVM`/testnet gates — the agent path adds no bypass. ✅
- **Type consistency:** `AppActionResult`, `IAppActionContext` members, `ActionProposal`, `AppActionRegistry.ToolName` are used identically across tasks. Tray executor signature `Func<ActionProposal,Task<AppActionResult>>` matches `AppAgentService.ExecuteApprovedAsync`. ✅
- **Known risk:** `MainWindowViewModel` may not construct headless → Task 11 unit test is intentionally light; action logic is covered via `FakeAppActionContext`, bridge verified by build + manual e2e. Flagged in Task 11.
- **Follow-on:** T1–T5 and S1–S6 are separate specs built on this layer (add their own actions + buttons).
