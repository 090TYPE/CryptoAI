namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Exposes the redesigned portfolio-terminal view model. It reuses the already
/// constructed <see cref="PortfolioRebalanceVM"/> (live holdings/prices) and the
/// wallet workspace's saved-wallet list, so it is created lazily on first bind
/// once those dependencies exist.
/// </summary>
public partial class MainWindowViewModel
{
    private PortfolioDesk.PortfolioDeskViewModel? _portfolioDeskVM;

    public PortfolioDesk.PortfolioDeskViewModel PortfolioDeskVM =>
        _portfolioDeskVM ??= new PortfolioDesk.PortfolioDeskViewModel(PortfolioRebalanceVM, WalletVM);
}
