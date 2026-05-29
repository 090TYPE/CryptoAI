using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.Views;
using System.Reactive.Concurrency;
using System.Threading;

namespace CryptoAITerminal.TerminalUI
{
    public partial class App : Application
    {
        // Exposed for ViewModels that need an explicit scheduler reference.
        // UseReactiveUI() in Program.cs already wires RxApp.MainThreadScheduler;
        // this property mirrors it so existing code that uses App.UiScheduler still compiles.
        public static IScheduler UiScheduler { get; private set; } = CurrentThreadScheduler.Instance;

        /// <summary>Сервис системного трея — доступен из ViewModels для уведомлений.</summary>
        public static SystemTrayService? Tray { get; private set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Keep UiScheduler in sync with the actual SynchronizationContext.
            if (SynchronizationContext.Current is not null)
                UiScheduler = new SynchronizationContextScheduler(SynchronizationContext.Current);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Apply all saved API keys as process env-vars before any service starts
                CredentialsService.LoadAndApplyAll();

                var mainWindow = new MainWindow();

                // ── Системный трей ────────────────────────────────────────────
                Tray = new SystemTrayService();

                Tray.OnShowRequested = () => Dispatcher.UIThread.Post(() =>
                {
                    mainWindow.Show();
                    mainWindow.WindowState = Avalonia.Controls.WindowState.FullScreen;
                    mainWindow.Activate();
                });

                Tray.OnExitRequested = () => Dispatcher.UIThread.Post(() =>
                {
                    mainWindow.AllowClose = true;
                    mainWindow.Close();
                });

                desktop.Exit += (_, _) => Tray.Dispose();

                desktop.MainWindow = mainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
