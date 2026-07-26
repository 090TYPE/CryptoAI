using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Host view model for <c>PortfolioView</c>. Almost the whole screen is driven by
/// <see cref="PortfolioDesk.PortfolioDeskViewModel"/>, but three panels show state that the desk
/// does not own — the armed trading session, the meme radar and realized PnL. Those used to be
/// fetched with <c>$parent[Window].DataContext</c>, which tied the view to
/// <see cref="MainWindowViewModel"/>; handing them out from here is what breaks that tie.
/// </summary>
public sealed class PortfolioScreenViewModel : ReactiveObject
{
    public PortfolioScreenViewModel(PortfolioDesk.PortfolioDeskViewModel desk,
                                    WalletWorkspaceViewModel wallet,
                                    DexTrendingViewModel memeRadar,
                                    PnlDashboardViewModel pnl)
    {
        Desk = desk;
        Wallet = wallet;
        MemeRadar = memeRadar;
        Pnl = pnl;
    }

    /// <summary>The portfolio desk itself — header, wallet browser, blotter, right rail, modals.</summary>
    public PortfolioDesk.PortfolioDeskViewModel Desk { get; }

    /// <summary>Live wallet session: arming, self-tests and TRC20 payments in the right rail.</summary>
    public WalletWorkspaceViewModel Wallet { get; }

    /// <summary>Backs the MEME RADAR blotter tab.</summary>
    public DexTrendingViewModel MemeRadar { get; }

    /// <summary>Backs the ANALYTICS tab in the right rail.</summary>
    public PnlDashboardViewModel Pnl { get; }
}
