using System;
using Avalonia.Controls;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Views;

public partial class BotsView : UserControl
{
    private bool _botLogHooked;

    public BotsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Auto-scrolls the bot terminal log to the bottom when new lines arrive.
    /// Lives here (not in MainWindow) because the named ScrollViewer moved into this
    /// UserControl's namescope when the Bots page was extracted.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_botLogHooked || DataContext is not MainWindowViewModel { AIBotVM: { } botVm })
        {
            return;
        }

        var scrollViewer = this.FindControl<ScrollViewer>("BotLogScrollViewer");
        if (scrollViewer is null)
        {
            return;
        }

        _botLogHooked = true;
        botVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AIBotViewModel.BotLog))
            {
                Dispatcher.UIThread.Post(() => scrollViewer.ScrollToEnd());
            }
        };
    }
}
