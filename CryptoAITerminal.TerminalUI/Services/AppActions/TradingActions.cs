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
    public bool IsMutating => false;
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
