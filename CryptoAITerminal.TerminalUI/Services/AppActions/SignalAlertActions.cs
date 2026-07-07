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
