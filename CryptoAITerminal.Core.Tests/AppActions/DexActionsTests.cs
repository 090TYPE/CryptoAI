using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class DexActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task SelectDexToken_routes_token()
    {
        var ctx = new FakeAppActionContext();
        var r = await new SelectDexTokenAction().ExecuteAsync(Args(new { token = "0xabc" }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("SelectDex:0xabc", ctx.Calls);
    }

    [Fact]
    public async Task DexBuy_routes_amount_and_is_mutating()
    {
        var ctx = new FakeAppActionContext();
        var r = await new DexBuyAction().ExecuteAsync(Args(new { amount_native = 0.5 }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        // Fake logs the decimal with the current culture; format the expectation the same
        // way so this passes regardless of locale (ru-RU renders 0.5 as "0,5").
        Assert.Contains($"DexBuy:{0.5m}", ctx.Calls);
        Assert.True(new DexBuyAction().IsMutating);
    }

    [Fact]
    public async Task DexSell_routes_amount()
    {
        var ctx = new FakeAppActionContext();
        var r = await new DexSellAction().ExecuteAsync(Args(new { amount_tokens = 100 }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("DexSell:100", ctx.Calls);
    }

    [Fact]
    public async Task DexBuy_rejects_non_positive_amount()
    {
        var ctx = new FakeAppActionContext();
        var r = await new DexBuyAction().ExecuteAsync(Args(new { amount_native = 0 }), ctx, CancellationToken.None);
        Assert.False(r.Success);
    }
}
