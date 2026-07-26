using System;
using System.ComponentModel;
using System.Reactive;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Backs the shell screens that have no subject matter of their own — Help and Logout.
///
/// Both are chrome rather than a desk: they show static guidance and drive shell-level actions
/// (safe logout, jump to another tab). Giving them one small view model is what lets their views
/// compile their bindings against something other than <see cref="MainWindowViewModel"/>; the
/// shell keeps ownership of the behaviour and hands the pieces in through the constructor.
/// </summary>
public sealed class ShellScreensViewModel : ReactiveObject
{
    private readonly Func<string> _logoutStatusLabel;

    /// <param name="logoutStatusLabel">
    /// Read lazily so the shell can pass it before the wallet/bot/sniper view models it reads exist.
    /// </param>
    /// <param name="logoutStatusNotifier">
    /// The shell itself. It already tracks every state change that alters the logout status line, so
    /// this view model relays that notification instead of re-subscribing to the same sub-view-models.
    /// </param>
    public ShellScreensViewModel(
        string helpQuickStartSummary,
        string helpSafetySummary,
        Func<string> logoutStatusLabel,
        INotifyPropertyChanged logoutStatusNotifier,
        ReactiveCommand<Unit, Unit> safeLogoutCommand,
        ReactiveCommand<string, Unit> selectMainTabCommand)
    {
        HelpQuickStartSummary = helpQuickStartSummary;
        HelpSafetySummary     = helpSafetySummary;
        _logoutStatusLabel    = logoutStatusLabel;
        SafeLogoutCommand     = safeLogoutCommand;
        SelectMainTabCommand  = selectMainTabCommand;

        // No unsubscribe: the notifier is the shell, which owns this instance, so the handler dies
        // with both of them at the same time.
        logoutStatusNotifier.PropertyChanged += OnNotifierPropertyChanged;
    }

    public string HelpQuickStartSummary { get; }

    public string HelpSafetySummary { get; }

    public string LogoutStatusLabel => _logoutStatusLabel();

    public ReactiveCommand<Unit, Unit> SafeLogoutCommand { get; }

    public ReactiveCommand<string, Unit> SelectMainTabCommand { get; }

    private void OnNotifierPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The shell publishes the status line under the same property name; null means "all changed".
        if (e.PropertyName is null or nameof(LogoutStatusLabel))
        {
            this.RaisePropertyChanged(nameof(LogoutStatusLabel));
        }
    }
}
