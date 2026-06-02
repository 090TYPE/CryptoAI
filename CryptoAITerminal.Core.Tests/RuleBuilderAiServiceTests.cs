using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class RuleBuilderAiServiceTests
{
    private static readonly RuleBuilderAiService Svc = new() { ApiKey = "" }; // offline parser

    [Fact]
    public async Task ParsesRsiDipAndNotify()
    {
        var r = await Svc.BuildAsync("When ETHUSDT RSI drops below 28, notify me");
        Assert.NotNull(r.Rule);
        Assert.True(r.IsFallback);

        var cond = Assert.Single(r.Rule!.Conditions);
        Assert.Equal(ConditionType.RsiBelow, cond.Type);
        Assert.Equal("ETHUSDT", cond.Symbol);
        Assert.Equal(28m, cond.Param2);

        Assert.Contains(r.Rule.Actions, a => a.Type == ActionType.Notify);
    }

    [Fact]
    public async Task Parses24hDropAndCloseAll()
    {
        var r = await Svc.BuildAsync("If BTCUSDT falls 6% close all positions");
        Assert.NotNull(r.Rule);
        Assert.Contains(r.Rule!.Conditions, c => c.Type == ConditionType.Price24hChangeBelow && c.Param1 == -6m);
        Assert.Contains(r.Rule.Actions, a => a.Type == ActionType.CloseAllPositions);
    }

    [Fact]
    public async Task UnparseableCondition_ReturnsNullRuleWithNote()
    {
        var r = await Svc.BuildAsync("do something clever with my portfolio");
        Assert.Null(r.Rule);
        Assert.True(r.IsFallback);
        Assert.False(string.IsNullOrWhiteSpace(r.Note));
    }
}
