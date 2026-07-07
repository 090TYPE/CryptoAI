using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>The catalog of everything the AI can do. Pure — holds no app state.</summary>
public sealed class AppActionRegistry
{
    public IReadOnlyList<IAppAction> All { get; }
    private readonly Dictionary<string, IAppAction> _byId;

    public AppActionRegistry(IReadOnlyList<IAppAction> actions)
    {
        All = actions;
        _byId = actions.ToDictionary(a => a.Id, System.StringComparer.OrdinalIgnoreCase);
    }

    public IAppAction? Find(string id) => _byId.TryGetValue(id, out var a) ? a : null;

    /// <summary>Model tool names can't contain dots — map id "nav.goto" ⇄ "nav_goto".</summary>
    public static string ToolName(string id) => id.Replace('.', '_');
    public IAppAction? FindByToolName(string toolName) =>
        All.FirstOrDefault(a => ToolName(a.Id) == toolName);

    /// <summary>The full default action set.</summary>
    public static AppActionRegistry Default() => new(new IAppAction[]
    {
        new NavGotoAction(), new ReadBalanceAction(), new ReadPositionsAction(), new ReadMarketAction(),
        new SetTicketAction(), new ArmLimitAction(), new ArmTpSlAction(), new PlaceMarketAction(),
        new ClosePositionAction(),
        // DEX spot buy/sell (gated via ctx.BuyAsync/SellAsync). perp.place is intentionally
        // descoped in v1: no context method places a perp order — only perp.set_mode toggles
        // live/paper; perp order entry stays on the perps desk UI.
        new SelectDexTokenAction(), new DexBuyAction(), new DexSellAction(), new SetPerpModeAction(),
        new AddAlertAction(), new ApplySignalAction(),
        new ConfigureGridBotAction(), new SelectWalletAction(),
    });
}
