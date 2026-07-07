using System;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Thin <c>*FromAgent</c> wrappers that let the agentic action layer drive the live
/// trading ticket. Every wrapper REUSES the existing gated command/method (RiskManager,
/// WalletVM execution guard, testnet gates all still run) and returns a structured
/// <see cref="AppActionResult"/>. No order/risk logic is duplicated here — that is the
/// whole point: the agent must not be able to bypass the guards.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Canonical navigation section keys the agent may target (see NormalizeSectionKey).</summary>
    public System.Collections.Generic.IReadOnlyList<string> KnownSectionKeys { get; } = new[]
    {
        "dashboard", "markets", "trading", "portfolio", "ai-signals", "sniper", "dex",
        "logs", "risk", "backtest", "bots", "whale", "tape", "funding", "liquidation",
        "rules", "arb", "scanner", "router", "journal", "gas", "positions", "news",
        "onchain", "copy", "statarb", "settings",
    };

    // ── Trading ticket (CEX) ────────────────────────────────────────────────
    internal AppActionResult TrySelectTradingSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return AppActionResult.Fail("symbol required");
        }

        var match = Markets.FirstOrDefault(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return AppActionResult.Fail($"symbol '{symbol}' is not in the market list");
        }

        SelectedMarket = match;
        return AppActionResult.Ok($"Trading symbol set to {SelectedTradingSymbol}");
    }

    internal void SetTicketSideFromAgent(bool isBuy) => SelectedOrderSide = isBuy ? "BUY" : "SELL";

    internal void SelectOrderTypeFromAgent(string type) =>
        SelectedOrderType = string.Equals(type, "limit", StringComparison.OrdinalIgnoreCase) ? "Limit" : "Market";

    internal void SelectMarketModeFromAgent(string mode) =>
        SelectedCexMarketMode = string.Equals(mode, "futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";

    internal void SetQuantityFromAgent(decimal quantity) => TradeQuantity = quantity;

    internal AppActionResult SetTradeUsdFromAgent(decimal usd)
    {
        if (usd <= 0m)
        {
            return AppActionResult.Fail("usd notional must be > 0");
        }

        var px = CurrentTradePrice;
        if (px <= 0m)
        {
            return AppActionResult.Fail("no current price available to size the order");
        }

        TradeQuantity = Math.Round(usd / px, 6);
        return AppActionResult.Ok($"Sized {TradeQuantity} {BaseAssetSymbol} (~{usd:N2} USDT @ {px:N2})");
    }

    internal void SetLeverageFromAgent(int leverage) => ManualFuturesLeverage = leverage;

    internal void SetLimitPriceFromAgent(decimal price) => LimitPrice = price;

    internal void SetTakeProfitFromAgent(decimal price) => TakeProfitPrice = price;

    internal void SetStopLossFromAgent(decimal price) => StopLossPrice = price;

    internal AppActionResult ArmLimitFromAgent(bool isBuy)
    {
        if (LimitPrice <= 0m)
        {
            return AppActionResult.Fail("set a limit price first");
        }

        if (TradeQuantity <= 0m)
        {
            return AppActionResult.Fail("set a quantity first");
        }

        if (isBuy)
        {
            PlaceBuyLimit();
        }
        else
        {
            PlaceSellLimit();
        }

        return AppActionResult.Ok($"{(isBuy ? "BUY" : "SELL")} LIMIT armed at {LimitPrice:N2} for {TradeQuantity} {BaseAssetSymbol}");
    }

    internal AppActionResult ArmTakeProfitFromAgent()
    {
        if (PositionQuantity == 0m)
        {
            return AppActionResult.Fail("no open position to protect");
        }

        if (TakeProfitPrice <= 0m)
        {
            return AppActionResult.Fail("set a take-profit price first");
        }

        ArmTakeProfit();
        return AppActionResult.Ok($"Take-profit armed at {TakeProfitPrice:N2}");
    }

    internal AppActionResult ArmStopLossFromAgent()
    {
        if (PositionQuantity == 0m)
        {
            return AppActionResult.Fail("no open position to protect");
        }

        if (StopLossPrice <= 0m)
        {
            return AppActionResult.Fail("set a stop-loss price first");
        }

        ArmStopLoss();
        return AppActionResult.Ok($"Stop-loss armed at {StopLossPrice:N2}");
    }

    internal async Task<AppActionResult> PlaceMarketFromAgent(bool isBuy)
    {
        if (TradeQuantity <= 0m)
        {
            return AppActionResult.Fail("set a quantity first");
        }

        if (isBuy)
        {
            await ExecuteBuyMarket();
        }
        else
        {
            await ExecuteSellMarket();
        }

        return AppActionResult.Ok($"Market {(isBuy ? "BUY" : "SELL")} submitted for {TradeQuantity} {BaseAssetSymbol} (subject to risk/execution guards)");
    }

    internal async Task<AppActionResult> ClosePositionFromAgent()
    {
        if (PositionQuantity == 0m)
        {
            return AppActionResult.Fail("no open position to close");
        }

        await ExecuteClosePosition();
        return AppActionResult.Ok("Close-position submitted (subject to risk/execution guards)");
    }

    // ── DEX perps ───────────────────────────────────────────────────────────
    internal AppActionResult SetPerpLiveFromAgent(bool live)
    {
        var perp = DexDeskVM.Perp;
        if (live == perp.IsLiveTrading)
        {
            return AppActionResult.Ok($"Perp live mode already {(live ? "ON" : "OFF")}");
        }

        perp.ToggleLiveTradingCommand.Execute().Subscribe();

        return perp.IsLiveTrading == live
            ? AppActionResult.Ok($"Perp live mode {(live ? "enabled" : "disabled")}")
            : AppActionResult.Fail($"Perp live mode stayed {(perp.IsLiveTrading ? "ON" : "OFF")} — blocked by live-readiness gate");
    }

    // ── Signals + alerts ────────────────────────────────────────────────────
    internal AppActionResult ApplySignalToTicketFromAgent(string symbol, string side)
    {
        var selection = TrySelectTradingSymbol(symbol);
        if (!selection.Success)
        {
            return selection;
        }

        var isSell = string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase)
            || string.Equals(side, "short", StringComparison.OrdinalIgnoreCase);
        SelectedOrderSide = isSell ? "SELL" : "BUY";
        SelectMainTab("trading");
        return AppActionResult.Ok($"Applied {SelectedOrderSide} signal for {SelectedTradingSymbol} to the ticket");
    }

    internal AppActionResult AddPriceAlertFromAgent(string symbol, decimal price, bool above)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return AppActionResult.Fail("symbol required");
        }

        if (price <= 0m)
        {
            return AppActionResult.Fail("alert price must be > 0");
        }

        AlertsVM.NewAlertSymbol = symbol;
        AlertsVM.NewAlertThreshold = price;
        AlertsVM.SelectedCondition = above ? "PriceAbove" : "PriceBelow";
        AlertsVM.AddAlertCommand.Execute().Subscribe();
        return AppActionResult.Ok($"Alert added: {symbol} {(above ? "above" : "below")} {price}");
    }
}
