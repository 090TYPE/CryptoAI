using System.Linq;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppActionRegistryTests
{
    [Fact]
    public void All_action_ids_are_unique()
    {
        var reg = AppActionRegistry.Default();
        var ids = reg.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_action_has_description_and_schema()
    {
        foreach (var a in AppActionRegistry.Default().All)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
            Assert.NotNull(a.ParamSchema);
        }
    }

    [Fact]
    public void Find_resolves_by_id()
    {
        var reg = AppActionRegistry.Default();
        Assert.NotNull(reg.Find("nav.goto"));
        Assert.Null(reg.Find("does.not.exist"));
    }

    [Fact]
    public void ToolName_replaces_dots_with_underscores()
        => Assert.Equal("nav_goto", AppActionRegistry.ToolName("nav.goto"));
}
