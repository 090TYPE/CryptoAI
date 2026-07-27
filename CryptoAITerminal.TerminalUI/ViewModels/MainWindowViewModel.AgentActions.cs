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
        "onchain", "copy", "statarb", "analytics", "settings",
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

    internal void SetTicketSideFromAgent(bool isBuy) => OrderTicketVM.SelectedOrderSide = isBuy ? "BUY" : "SELL";

    internal void SelectOrderTypeFromAgent(string type) =>
        OrderTicketVM.SelectedOrderType = string.Equals(type, "limit", StringComparison.OrdinalIgnoreCase) ? "Limit" : "Market";

    internal void SelectMarketModeFromAgent(string mode) =>
        OrderTicketVM.SelectedCexMarketMode = string.Equals(mode, "futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";

    internal void SetQuantityFromAgent(decimal quantity) => OrderTicketVM.TradeQuantity = quantity;

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

        OrderTicketVM.TradeQuantity = Math.Round(usd / px, 6);
        return AppActionResult.Ok($"Sized {OrderTicketVM.TradeQuantity} {BaseAssetSymbol} (~{usd:N2} USDT @ {px:N2})");
    }

    internal void SetLeverageFromAgent(int leverage) => OrderTicketVM.ManualFuturesLeverage = leverage;

    internal void SetLimitPriceFromAgent(decimal price) => OrderTicketVM.LimitPrice = price;

    internal void SetTakeProfitFromAgent(decimal price) => OrderTicketVM.TakeProfitPrice = price;

    internal void SetStopLossFromAgent(decimal price) => OrderTicketVM.StopLossPrice = price;

    internal AppActionResult ArmLimitFromAgent(bool isBuy)
    {
        if (OrderTicketVM.LimitPrice <= 0m)
        {
            return AppActionResult.Fail("set a limit price first");
        }

        if (OrderTicketVM.TradeQuantity <= 0m)
        {
            return AppActionResult.Fail("set a quantity first");
        }

        var ok = isBuy ? PlaceBuyLimit() : PlaceSellLimit();
        return ok
            ? AppActionResult.Ok($"{(isBuy ? "BUY" : "SELL")} LIMIT armed at {OrderTicketVM.LimitPrice:N2} for {OrderTicketVM.TradeQuantity} {BaseAssetSymbol}")
            : AppActionResult.Fail("limit rejected — see the log");
    }

    internal AppActionResult ArmTakeProfitFromAgent()
    {
        if (PositionQuantity == 0m)
        {
            return AppActionResult.Fail("no open position to protect");
        }

        if (OrderTicketVM.TakeProfitPrice <= 0m)
        {
            return AppActionResult.Fail("set a take-profit price first");
        }

        return ArmTakeProfit()
            ? AppActionResult.Ok($"Take-profit armed at {OrderTicketVM.TakeProfitPrice:N2}")
            : AppActionResult.Fail("take-profit rejected — see the log");
    }

    internal AppActionResult ArmStopLossFromAgent()
    {
        if (PositionQuantity == 0m)
        {
            return AppActionResult.Fail("no open position to protect");
        }

        if (OrderTicketVM.StopLossPrice <= 0m)
        {
            return AppActionResult.Fail("set a stop-loss price first");
        }

        return ArmStopLoss()
            ? AppActionResult.Ok($"Stop-loss armed at {OrderTicketVM.StopLossPrice:N2}")
            : AppActionResult.Fail("stop-loss rejected — see the log");
    }

    internal async Task<AppActionResult> PlaceMarketFromAgent(bool isBuy)
    {
        if (OrderTicketVM.TradeQuantity <= 0m)
        {
            return AppActionResult.Fail("set a quantity first");
        }

        var ok = isBuy ? await ExecuteBuyMarket() : await ExecuteSellMarket();
        return ok
            ? AppActionResult.Ok($"Market {(isBuy ? "BUY" : "SELL")} placed for {OrderTicketVM.TradeQuantity} {BaseAssetSymbol}")
            : AppActionResult.Fail("order was blocked by a risk/wallet/execution guard — see the log");
    }

    internal async Task<AppActionResult> ClosePositionFromAgent()
    {
        if (PositionQuantity == 0m)
        {
            return AppActionResult.Fail("no open position to close");
        }

        return await ExecuteClosePosition()
            ? AppActionResult.Ok("Close-position placed")
            : AppActionResult.Fail("close was blocked by a risk/wallet/execution guard — see the log");
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
        OrderTicketVM.SelectedOrderSide = isSell ? "SELL" : "BUY";
        SelectMainTab("trading");
        return AppActionResult.Ok($"Applied {OrderTicketVM.SelectedOrderSide} signal for {SelectedTradingSymbol} to the ticket");
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
