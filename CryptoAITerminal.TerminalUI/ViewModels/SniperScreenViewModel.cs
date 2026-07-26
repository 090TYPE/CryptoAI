using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Host view model for <c>SniperView</c>. The screen is two desks under one tab bar: the sniper
/// itself owns the control rail, the candidate tabs, analytics and the detail panel, while the
/// "DEX Trending" tab is a separate feed with its own filters and AI ranking.
///
/// The property names deliberately match the ones the view used to reach through
/// <see cref="MainWindowViewModel"/>, so composing the two desks here is the entire change — no
/// binding path in the view had to move, and neither desk learns about the other.
/// </summary>
public sealed class SniperScreenViewModel : ReactiveObject
{
    public SniperScreenViewModel(SniperViewModel sniper, DexTrendingViewModel dexTrending)
    {
        SniperVM      = sniper;
        DexTrendingVM = dexTrending;
    }

    /// <summary>Control rail, candidate tabs, analytics and the right-hand detail panel.</summary>
    public SniperViewModel SniperVM { get; }

    /// <summary>Backs the DEX Trending tab: filter row, AI picks and the token table.</summary>
    public DexTrendingViewModel DexTrendingVM { get; }
}
