using Avalonia.Controls;

namespace CryptoAITerminal.TerminalUI.Views;

/// <summary>
/// Overlay layer for the Bots desk: the columns / wizard / confirm / side-panel
/// modals plus the toast. Inherits the <c>BotsDeskViewModel</c> DataContext from
/// its host <see cref="BotsView"/>.
/// </summary>
public partial class BotsModals : UserControl
{
    public BotsModals()
    {
        InitializeComponent();
    }
}
