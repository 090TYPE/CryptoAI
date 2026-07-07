using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>
/// Wraps a real IAppActionContext for AUTONOMOUS runs. Read/nav/setter calls pass straight
/// through. Trade-executing calls first pass <see cref="AgentGuardrails"/> (allowlist / max-trades /
/// session-budget); on breach the trade is refused and the session stops. In PAPER mode
/// (rails.LiveEnabled == false) trades are simulated (the inner real order command is NOT called),
/// so the autonomous loop can run safely without touching funds. This never bypasses the inner
/// context's own risk/wallet/testnet gates — it only ADDS restrictions on top.
/// </summary>
public sealed class GuardedAppActionContext : IAppActionContext
{
    private readonly IAppActionContext _inner;
    private readonly AgentGuardrails _rails;

    public GuardedAppActionContext(IAppActionContext inner, AgentGuardrails rails)
    {
        _inner = inner;
        _rails = rails;
    }

    // Pass-through: navigation, reads, ticket setters, non-trade mutations.
    public string CurrentSymbol => _inner.CurrentSymbol;
    public IReadOnlyList<string> KnownSections => _inner.KnownSections;
    public AppActionResult NavigateTo(string s) => _inner.NavigateTo(s);
    public Task<decimal> GetBalanceUsdtAsync(CancellationToken ct) => _inner.GetBalanceUsdtAsync(ct);
    public Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct) => _inner.GetOpenPositionsAsync(ct);
    public Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct) => _inner.GetMarketAsync(symbol, ct);
    public Task<decimal> GetTicketNotionalUsdAsync(CancellationToken ct) => _inner.GetTicketNotionalUsdAsync(ct);
    public AppActionResult SetTradingSymbol(string symbol) => _inner.SetTradingSymbol(symbol);
    public AppActionResult SetTicketSide(bool isBuy) => _inner.SetTicketSide(isBuy);
    public AppActionResult SetOrderType(string type) => _inner.SetOrderType(type);
    public AppActionResult SetMarketMode(string mode) => _inner.SetMarketMode(mode);
    public AppActionResult SetQuantity(decimal q) => _inner.SetQuantity(q);
    public AppActionResult SetUsdNotional(decimal usd) => _inner.SetUsdNotional(usd);
    public AppActionResult SetLeverage(int lev) => _inner.SetLeverage(lev);
    public AppActionResult SetLimitPrice(decimal p) => _inner.SetLimitPrice(p);
    public AppActionResult SetTakeProfit(decimal p) => _inner.SetTakeProfit(p);
    public AppActionResult SetStopLoss(decimal p) => _inner.SetStopLoss(p);
    // Arming a LIMIT opens exposure → full guardrail (count + allowlist + stop) and paper-sim.
    // Budget is not enforced here (notional isn't available synchronously) — documented v1 limit, same as DEX.
    public AppActionResult ArmLimit(bool isBuy)
    {
        if (!_rails.TryAuthorize(_inner.CurrentSymbol, 0m, out var reason))
            return AppActionResult.Fail($"guardrail blocked: {reason}");
        if (!_rails.LiveEnabled)
            return AppActionResult.Ok($"PAPER: simulated arm LIMIT {(isBuy ? "buy" : "sell")} {_inner.CurrentSymbol}");
        return _inner.ArmLimit(isBuy);
    }

    // TP/SL reduce risk → not counted against MaxTrades, but must NOT place real orders in PAPER,
    // and must not run after the session has stopped.
    public AppActionResult ArmTakeProfit()
    {
        if (_rails.Stopped) return AppActionResult.Fail($"guardrail blocked: {_rails.StopReason}");
        return !_rails.LiveEnabled
            ? AppActionResult.Ok($"PAPER: simulated take-profit on {_inner.CurrentSymbol}")
            : _inner.ArmTakeProfit();
    }

    public AppActionResult ArmStopLoss()
    {
        if (_rails.Stopped) return AppActionResult.Fail($"guardrail blocked: {_rails.StopReason}");
        return !_rails.LiveEnabled
            ? AppActionResult.Ok($"PAPER: simulated stop-loss on {_inner.CurrentSymbol}")
            : _inner.ArmStopLoss();
    }
    public AppActionResult SetPerpLiveMode(bool live) => _inner.SetPerpLiveMode(live);
    public Task<AppActionResult> ApplySignalToTicketAsync(string symbol, string side, CancellationToken ct) => _inner.ApplySignalToTicketAsync(symbol, side, ct);
    public AppActionResult AddPriceAlert(string symbol, decimal price, bool above) => _inner.AddPriceAlert(symbol, price, above);
    public AppActionResult ConfigureGridBot(string symbol, decimal lower, decimal upper, int levels) => _inner.ConfigureGridBot(symbol, lower, upper, levels);
    public AppActionResult SelectWalletNetwork(string network) => _inner.SelectWalletNetwork(network);
    public Task<AppActionResult> SelectDexTokenAsync(string t, CancellationToken ct) => _inner.SelectDexTokenAsync(t, ct);

    // Trade-executing: guardrail-gated.
    public async Task<AppActionResult> PlaceMarketAsync(bool isBuy, CancellationToken ct)
    {
        var usd = await _inner.GetTicketNotionalUsdAsync(ct).ConfigureAwait(false);
        if (!_rails.TryAuthorize(_inner.CurrentSymbol, usd, out var reason))
            return AppActionResult.Fail($"guardrail blocked: {reason}");
        if (!_rails.LiveEnabled)
            return AppActionResult.Ok($"PAPER: simulated market {(isBuy ? "buy" : "sell")} {_inner.CurrentSymbol} (~${usd})");
        return await _inner.PlaceMarketAsync(isBuy, ct).ConfigureAwait(false);
    }

    public Task<AppActionResult> ClosePositionAsync(CancellationToken ct)
        => !_rails.LiveEnabled
            ? Task.FromResult(AppActionResult.Ok($"PAPER: simulated close {_inner.CurrentSymbol}"))
            : _inner.ClosePositionAsync(ct);

    public async Task<AppActionResult> DexBuyAsync(decimal amountNative, CancellationToken ct)
    {
        // DEX USD value isn't known here -> budget not enforced for DEX in v1; count + allowlist(symbol="DEX") + stop still apply.
        if (!_rails.TryAuthorize("DEX", 0m, out var reason))
            return AppActionResult.Fail($"guardrail blocked: {reason}");
        if (!_rails.LiveEnabled)
            return AppActionResult.Ok($"PAPER: simulated DEX buy {amountNative} native");
        return await _inner.DexBuyAsync(amountNative, ct).ConfigureAwait(false);
    }

    public async Task<AppActionResult> DexSellAsync(decimal amountTokens, CancellationToken ct)
    {
        if (!_rails.TryAuthorize("DEX", 0m, out var reason))
            return AppActionResult.Fail($"guardrail blocked: {reason}");
        if (!_rails.LiveEnabled)
            return AppActionResult.Ok($"PAPER: simulated DEX sell {amountTokens} tokens");
        return await _inner.DexSellAsync(amountTokens, ct).ConfigureAwait(false);
    }
}
