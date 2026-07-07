using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

public sealed class SelectDexTokenAction : IAppAction
{
    public string Id => "dex.select_token";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Select a DEX token by contract address (or symbol) so it can be bought/sold.";
    public object ParamSchema => new { type = "object", properties = new { token = new { type = "string" } }, required = new[] { "token" } };
    public bool IsMutating => false;
    public string Preview(JsonElement a) => $"Select DEX token {ArgReader.Str(a, "token")}";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var t = ArgReader.Str(a, "token");
        return string.IsNullOrWhiteSpace(t) ? Task.FromResult(AppActionResult.Fail("token required")) : ctx.SelectDexTokenAsync(t, ct);
    }
}

public sealed class DexBuyAction : IAppAction
{
    public string Id => "dex.buy";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Buy the selected DEX token by spending native currency (amount_native). Select the token first with dex.select_token.";
    public object ParamSchema => new { type = "object", properties = new { amount_native = new { type = "number" } }, required = new[] { "amount_native" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"DEX BUY spending {ArgReader.Dec(a, "amount_native")} native";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var amt = ArgReader.Dec(a, "amount_native");
        return amt <= 0 ? Task.FromResult(AppActionResult.Fail("amount_native must be > 0")) : ctx.DexBuyAsync(amt, ct);
    }
}

public sealed class DexSellAction : IAppAction
{
    public string Id => "dex.sell";
    public AppActionCategory Category => AppActionCategory.Trading;
    public string Description => "Sell an amount of the selected DEX token back to native currency (amount_tokens).";
    public object ParamSchema => new { type = "object", properties = new { amount_tokens = new { type = "number" } }, required = new[] { "amount_tokens" } };
    public bool IsMutating => true;
    public string Preview(JsonElement a) => $"DEX SELL {ArgReader.Dec(a, "amount_tokens")} tokens";
    public Task<AppActionResult> ExecuteAsync(JsonElement a, IAppActionContext ctx, CancellationToken ct)
    {
        var amt = ArgReader.Dec(a, "amount_tokens");
        return amt <= 0 ? Task.FromResult(AppActionResult.Fail("amount_tokens must be > 0")) : ctx.DexSellAsync(amt, ct);
    }
}
