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
