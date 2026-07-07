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
    public string Preview(JsonElement a) => $"Grid {ArgReader.Str(a, "symbol")} {ArgReader.Dec(a, "lower")}-{ArgReader.Dec(a, "upper")} x {ArgReader.Int(a, "levels")}";
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
