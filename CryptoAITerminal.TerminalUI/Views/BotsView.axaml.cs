using Avalonia.Controls;

namespace CryptoAITerminal.TerminalUI.Views;

/// <summary>
/// The redesigned Bots tab — a self-contained interactive "bot desk" that renders
/// <see cref="ViewModels.BotsDesk.BotsDeskViewModel"/> (exposed by the main window
/// view model as <c>BotsDesk</c>). All behaviour lives in the view model.
/// </summary>
public partial class BotsView : UserControl
{
    public BotsView()
    {
        InitializeComponent();
    }
}
