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
