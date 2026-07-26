using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Views;

public partial class MainWindow : Window
{
    private readonly record struct LocalizationKey(AvaloniaObject Target, string PropertyName);

    private static readonly string[] SplashMessages =
    [
        "Syncing market feeds...",
        "Booting strategy engine...",
        "Linking risk controls...",
        "Preparing live workspace..."
    ];

    private readonly DispatcherTimer _splashTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    // Сканирует визуальное дерево, чтобы регистрировать новые TextBlock/Button/Tab/Expander
    // под локализацию. Останавливается после нескольких стабильных тиков подряд (см.
    // _stableScanTicks); перезапускается на LanguageChanged и при смене раздела сайдбара.
    private readonly DispatcherTimer _localizationScanTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private const int LocalizationScanStableThreshold = 3;
    private int _localizationScanStableTicks;
    private readonly UiLocalizationService _localization = UiLocalizationService.Instance;
    private readonly Dictionary<LocalizationKey, string> _sourceTexts = [];
    private readonly HashSet<LocalizationKey> _observedProperties = [];
    // Keyed by target rather than a flat list, so registrations belonging to a control that has
    // left the visual tree can be disposed and dropped. LocalizationKey holds a strong reference to
    // its AvaloniaObject, and nothing ever removed entries: every row, modal and popup the app had
    // ever rendered stayed alive here for the lifetime of the window.
    private readonly Dictionary<AvaloniaObject, List<IDisposable>> _localizationSubscriptions = [];
    private DateTime _splashStartedAt;
    private bool _splashCompleted;
    private bool _isApplyingLocalization;
    private readonly DispatcherTimer _tickerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _tickerOffset;

    public MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>
    /// Когда true — окно действительно закроется (вызвано из меню трея «Выход»).
    /// Когда false — X / Alt+F4 скрывают окно в трей вместо закрытия.
    /// </summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        if (Avalonia.Controls.Design.IsDesignMode)
        {
            return;
        }

        DataContext = new MainWindowViewModel();
        Opened += OnOpened;
        _splashTimer.Tick += OnSplashTick;
        _localizationScanTimer.Tick += (_, _) => RunLocalizationScanTick();
        _localization.LanguageChanged += OnLanguageChanged;
        _localization.AiTranslator = (batch, ct) => AiUiTranslator.TranslateAsync(batch, ct);
        _localization.TranslationsUpdated += OnTranslationsUpdated;
        this.Closing += (s, e) =>
        {
            // Не разрешено реальное закрытие → скрываем в трей
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
                App.Tray?.ShowInfo("Crypto AI Terminal",
                    "Приложение свёрнуто в трей. Двойной клик по иконке — открыть.");
                return;
            }

            // Реальное закрытие — чистим ресурсы
            _splashTimer.Stop();
            _localizationScanTimer.Stop();
            _localization.LanguageChanged -= OnLanguageChanged;
            _localization.TranslationsUpdated -= OnTranslationsUpdated;
            foreach (var subscriptions in _localizationSubscriptions.Values)
                foreach (var subscription in subscriptions)
                    subscription.Dispose();
            _localizationSubscriptions.Clear();

            // Every bot that can have live orders resting on an exchange, not just the Rule bot.
            // A running grid leaves a full ladder of limit orders behind; closing the terminal used
            // to walk away from them, and they stayed live with nothing watching them.
            StopBotOnShutdown("Rule bot", () => ViewModel?.AIBotVM?.StopBotAsync());
            StopBotOnShutdown("Grid bot", () => ViewModel?.GridBotVM?.StopAsync());
            StopBotOnShutdown("DCA bot", () => ViewModel?.DcaBotVM?.StopAsync());

            ViewModel?.Dispose();
        };
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ConfigureFullscreenWindow();
        AttachLocalizationObservers();
        RefreshLanguageButtons();
        ApplyLocalizationToObservedControls();
        _localizationScanTimer.Start();
        if (ViewModel is { } vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                // При переключении раздела сайдбара рендерится новое поддерево —
                // нужно подхватить новые контролы для локализации.
                if (args.PropertyName == nameof(MainWindowViewModel.SelectedShellSection))
                    RearmLocalizationScan();

                // Auto-focus the Ctrl+K command bar input the moment it opens.
                else if (args.PropertyName == nameof(MainWindowViewModel.IsCommandPaletteOpen)
                         && vm.IsCommandPaletteOpen)
                {
                    Dispatcher.UIThread.Post(() =>
                        this.FindControl<TextBox>("CommandPaletteBox")?.Focus(),
                        DispatcherPriority.Input);
                }
            };
        }
        StartSplashSequence();

        // Drive the top-bar ticker marquee.
        _tickerTimer.Tick += TickerTick;
        _tickerTimer.Start();

        // The marquee runs at ~60 fps. When the window is hidden to the tray it is
        // pure wasted CPU (nothing on screen), so pause it while hidden and resume
        // on show. Covers the X-to-tray path since Closing calls Hide().
        this.GetObservable(IsVisibleProperty).Subscribe(visible =>
        {
            if (visible) _tickerTimer.Start();
            else _tickerTimer.Stop();
        });

        // Single-key trading hotkeys (fire only when no text-input control is focused)
        AddHandler(KeyDownEvent, OnTradingHotkeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Continuously scrolls the top-bar ticker one frame at a time. The ticker
    /// content is duplicated in XAML, so wrapping the offset at exactly half the strip
    /// width produces a seamless, endless marquee.</summary>
    private void TickerTick(object? sender, EventArgs e)
    {
        if (TickerStrip is not { } strip) return;
        if (strip.RenderTransform is not TranslateTransform xform) return;

        double half = strip.Bounds.Width / 2.0;
        if (half <= 20) return; // markets not laid out yet

        _tickerOffset -= 0.6; // ~36 px/sec at 60 fps
        if (_tickerOffset <= -half) _tickerOffset += half; // seamless wrap
        xform.X = _tickerOffset;
    }

    /// <summary>
    /// Handles trading hotkeys (B=Buy, S=Sell, 1/2/3=Allocation, Cancel-all, F=FocusPair).
    /// Skips when a TextBox, NumericUpDown or ComboBox is focused so typing is never intercepted.
    ///
    /// The handler is attached to the whole window in Tunnel mode, so it sees every key press
    /// anywhere in the app. Anything that moves money is therefore gated on the trading desk being
    /// the visible section: a single unmodified "B" pressed while reading News used to send a live
    /// market order. Navigation-only keys stay global.
    /// </summary>
    private void OnTradingHotkeyDown(object? sender, KeyEventArgs e)
    {
        // Don't fire if user is typing in an input control
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is TextBox or NumericUpDown or ComboBox) return;

        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        // Escape skips the intro. It used to run 3.3s of fixed Task.Delay with no way out.
        if (e.Key == Key.Escape && !_splashCompleted)
        {
            FinishSplash();
            e.Handled = true;
            return;
        }

        // Don't steal keys with modifiers (those are handled by Window.KeyBindings)
        if (e.KeyModifiers != KeyModifiers.None) return;

        var vm = ViewModel;
        if (vm is null) return;

        var hs = vm.HotkeySettings;

        // Focusing the pair switches to the trading desk, so it must stay reachable from anywhere.
        if (e.Key == hs.FocusPairKey)
        {
            vm.FocusTradingPairCommand.Execute(Unit.Default).Subscribe();
            e.Handled = true;
            return;
        }

        var onTradingDesk = string.Equals(vm.SelectedShellSection, "trading", StringComparison.OrdinalIgnoreCase);
        if (!onTradingDesk) return;

        if      (e.Key == hs.BuyMarketKey)     { vm.BuyMarketCommand.Execute(Unit.Default).Subscribe();    e.Handled = true; }
        else if (e.Key == hs.SellMarketKey)     { vm.SellMarketCommand.Execute(Unit.Default).Subscribe();   e.Handled = true; }
        else if (e.Key == hs.Allocation25Key)   { vm.HotkeyAlloc25Command.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.Allocation50Key)   { vm.HotkeyAlloc50Command.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.Allocation100Key)  { vm.HotkeyAlloc100Command.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.CancelOrdersKey)   { vm.CancelAllOrdersCommand.Execute(Unit.Default).Subscribe(); e.Handled = true; }
    }

    private void OnExitClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AllowClose = true;
        Close();
    }

    private void OnSetEnglishLanguageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _localization.SetLanguage(UiLanguage.English);
    }

    private void OnSetRussianLanguageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _localization.SetLanguage(UiLanguage.Russian);
    }

    private void OnToggleSidebarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.IsSidebarCollapsed = !vm.IsSidebarCollapsed;
    }

    private void OnToastClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        ViewModel?.ActivateToast();
    }

    private void OnToastCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.DismissToast();
    }

    private void OnOpenNotificationsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.OpenNotificationCenter();
    }

    private void OnCloseNotificationsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.CloseNotificationCenter();
    }

    private void OnClearNotificationsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ClearNotifications();
    }

    private void OnNotificationEntryClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: NotificationEntry entry })
        {
            ViewModel?.ActivateNotification(entry);
        }
    }

    private void OnLadderWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Delta.Y > 0)
        {
            ViewModel.ScrollLadderByTicks(1);
            e.Handled = true;
            return;
        }

        if (e.Delta.Y < 0)
        {
            ViewModel.ScrollLadderByTicks(-1);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Starts maximised rather than full screen. Forcing FullScreen here meant the terminal could
    /// not share a screen with a chart or a browser, could not be moved to a second monitor and,
    /// with WindowDecorations="None", could not be minimised at all. F11 still gives full screen.
    /// </summary>
    private void ConfigureFullscreenWindow()
    {
        if (WindowState == WindowState.Normal)
            WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Drags the window by its top bar, and double-click maximises, the way a real title bar does.
    /// Only when the press lands on the bar itself — pressing a button or the ticker inside it must
    /// still behave normally.
    /// </summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Border && e.Source is not Grid) return;

        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>
    /// Waits a bounded time for one bot to wind down while the window is closing. Bounded because
    /// a hung exchange call must not stop the app from exiting; anything that fails is written to
    /// the crash log, which is the only durable record at this point.
    /// </summary>
    private static void StopBotOnShutdown(string label, Func<Task?> stop)
    {
        try
        {
            if (stop() is { } task && !task.Wait(TimeSpan.FromSeconds(5)))
                Services.CrashLog.Write("WARN", $"{label} did not stop within 5s on shutdown — orders may still be resting.");
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write("WARN", $"{label} failed to stop on shutdown: {ex.Message}");
        }
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal;

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Maximized : WindowState.FullScreen;

    private void StartSplashSequence()
    {
        if (_splashCompleted)
        {
            return;
        }

        _splashStartedAt = DateTime.UtcNow;
        _splashTimer.Start();
        _ = RunSplashAsync();
    }

    private async Task RunSplashAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(2600));

        // Escape may have finished it already.
        if (_splashCompleted) return;
        FinishSplash();
    }

    /// <summary>Ends the intro immediately. Also the Escape path.</summary>
    private async void FinishSplash()
    {
        if (_splashCompleted) return;
        _splashCompleted = true;
        _splashTimer.Stop();
        SplashStatusText.Text = "Workspace online";
        SplashProgressFill.RenderTransform = new ScaleTransform(1, 1);
        SplashLogoShell.RenderTransform = new ScaleTransform(1, 1);
        SplashSpinner.RenderTransform = new RotateTransform(0);

        MainContentRoot.IsHitTestVisible = true;
        MainContentRoot.Opacity = 1;
        SplashOverlay.Opacity = 0;

        await Task.Delay(TimeSpan.FromMilliseconds(700));
        SplashOverlay.IsVisible = false;
        SplashOverlay.IsHitTestVisible = false;
    }

    private void OnSplashTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _splashStartedAt).TotalSeconds;
        var progress = Math.Clamp(elapsed / 2.15, 0.03, 1);
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        var pulse = 1 + Math.Sin(elapsed * 3.1) * 0.035;
        var messageIndex = Math.Min((int)(elapsed / 0.62), SplashMessages.Length - 1);

        SplashProgressFill.RenderTransform = new ScaleTransform(easedProgress, 1);
        SplashLogoShell.RenderTransform = new ScaleTransform(pulse, pulse);
        SplashSpinner.RenderTransform = new RotateTransform(elapsed * 140);
        SplashStatusText.Text = SplashMessages[messageIndex];
        SplashOverlay.Opacity = 0.97 + Math.Sin(elapsed * 2.6) * 0.03;
        SplashLogoShell.Opacity = 0.92 + Math.Sin(elapsed * 2.8) * 0.08;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        AttachLocalizationObservers();
        RefreshLanguageButtons();
        ApplyLocalizationToObservedControls();
        RearmLocalizationScan();
    }

    // Fresh AI translations arrived from the background translator — re-apply them.
    private void OnTranslationsUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyLocalizationToObservedControls();
            RearmLocalizationScan();
        });
    }

    /// <summary>
    /// Один тик сканирования. Считает, сколько новых контролов было зарегистрировано.
    /// После <see cref="LocalizationScanStableThreshold"/> тиков подряд без новых
    /// регистраций таймер останавливается, чтобы не жрать CPU на стабильном дереве.
    /// </summary>
    private void RunLocalizationScanTick()
    {
        PruneDetachedLocalizationTargets();

        var before = _observedProperties.Count;
        AttachLocalizationObservers();
        var registered = _observedProperties.Count - before;

        if (registered == 0)
        {
            if (++_localizationScanStableTicks >= LocalizationScanStableThreshold)
                _localizationScanTimer.Stop();
        }
        else
        {
            _localizationScanStableTicks = 0;
        }
    }

    /// <summary>
    /// Registers a localization subscription against the control that owns it, so it can be
    /// released when that control leaves the tree.
    /// </summary>
    private void TrackSubscription(AvaloniaObject target, IDisposable subscription)
    {
        if (!_localizationSubscriptions.TryGetValue(target, out var list))
        {
            list = [];
            _localizationSubscriptions[target] = list;
        }
        list.Add(subscription);
    }

    /// <summary>
    /// Drops registrations for controls that are no longer in the visual tree. Switching sections,
    /// opening a modal or scrolling a virtualized list creates and discards controls constantly;
    /// without this the registry — and every control in it — grew for as long as the app ran.
    /// </summary>
    private void PruneDetachedLocalizationTargets()
    {
        List<AvaloniaObject>? dead = null;

        foreach (var target in _localizationSubscriptions.Keys)
        {
            // Only Visuals can be judged this way. Non-visual targets are kept.
            if (target is Visual visual && !visual.IsAttachedToVisualTree())
                (dead ??= []).Add(target);
        }

        if (dead is null) return;

        foreach (var target in dead)
        {
            if (_localizationSubscriptions.Remove(target, out var subscriptions))
            {
                foreach (var subscription in subscriptions)
                    subscription.Dispose();
            }

            _observedProperties.RemoveWhere(k => ReferenceEquals(k.Target, target));

            foreach (var key in new List<LocalizationKey>(_sourceTexts.Keys))
            {
                if (ReferenceEquals(key.Target, target))
                    _sourceTexts.Remove(key);
            }
        }
    }

    /// <summary>Сбросить счётчик и запустить таймер, если он был остановлен.</summary>
    private void RearmLocalizationScan()
    {
        _localizationScanStableTicks = 0;
        if (!_localizationScanTimer.IsEnabled)
            _localizationScanTimer.Start();
    }

    private void RefreshLanguageButtons()
    {
        var isEnglish = _localization.CurrentLanguage == UiLanguage.English;
        EnglishLanguageButton.Background = isEnglish ? Brush.Parse("#21E6C1") : Brush.Parse("Transparent");
        EnglishLanguageButton.Foreground = isEnglish ? Brush.Parse("#04121C") : Brush.Parse("#3D5A72");
        RussianLanguageButton.Background = isEnglish ? Brush.Parse("Transparent") : Brush.Parse("#21E6C1");
        RussianLanguageButton.Foreground = isEnglish ? Brush.Parse("#3D5A72") : Brush.Parse("#04121C");
    }

    private void AttachLocalizationObservers()
    {
        RegisterTextBlock(SplashStatusText);

        foreach (var visual in EnumerateLocalizableVisuals(this))
        {
            if (visual is Control control)
            {
                RegisterToolTip(control);
            }

            switch (visual)
            {
                case TextBlock textBlock:
                    RegisterTextBlock(textBlock);
                    RegisterInlines(textBlock.Inlines);
                    break;
                case ToggleSwitch toggleSwitch:
                    RegisterToggleSwitch(toggleSwitch);
                    break;
                case Button button:
                    RegisterContentControl(button);
                    break;
                case TabItem tabItem:
                    RegisterHeaderedControl(tabItem);
                    break;
                case Expander expander:
                    RegisterHeaderedControl(expander);
                    break;
                case TextBox textBox:
                    RegisterTextBox(textBox);
                    break;
            }
        }
    }

    /// <summary>
    /// Walks the visual tree for localization registration, but does NOT descend into
    /// hidden section pages. The shell keeps all ~27 <c>*View</c> UserControls in the
    /// tree at once and toggles them with <c>IsVisible</c>, so a flat
    /// <c>GetVisualDescendants()</c> re-scanned every inactive page on every tick — the
    /// bulk of a ~15k-control tree — which showed up as stutter on navigation.
    /// Pruning at a hidden <see cref="UserControl"/> skips the 26 off-screen pages
    /// while still fully walking the active page (including its collapsed Expanders,
    /// which are not UserControls, so their behaviour is unchanged). A hidden page
    /// registers the next time it is shown — the scan re-arms on section switch.
    /// </summary>
    private static IEnumerable<Visual> EnumerateLocalizableVisuals(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is UserControl { IsVisible: false })
            {
                continue;
            }

            yield return child;

            foreach (var descendant in EnumerateLocalizableVisuals(child))
            {
                yield return descendant;
            }
        }
    }

    private const string ToolTipPropertyName = "ToolTip";

    /// <summary>
    /// Регистрирует строковый <c>ToolTip.Tip</c> контрола под локализацию. Регистрируем только
    /// если подсказка уже задана строкой (статические тултипы), чтобы не плодить тысячи пустых подписок.
    /// </summary>
    private void RegisterToolTip(Control control)
    {
        if (ToolTip.GetTip(control) is not string)
        {
            return;
        }

        var key = new LocalizationKey(control, ToolTipPropertyName);
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(control, control.GetObservable(ToolTip.TipProperty).Subscribe(tip =>
        {
            if (tip is string text)
            {
                HandleStringChanged(
                    key,
                    text,
                    () => ToolTip.GetTip(control) as string,
                    value => ToolTip.SetTip(control, value));
            }
        }));

        if (ToolTip.GetTip(control) is string initialTip)
        {
            HandleStringChanged(key, initialTip, () => ToolTip.GetTip(control) as string, value => ToolTip.SetTip(control, value));
        }
    }

    /// <summary>
    /// Регистрирует inline-фрагменты (<c>Run</c>) внутри <see cref="TextBlock"/> — у TextBlock с
    /// inline-содержимым свойство <c>Text</c> пустое, поэтому такие тексты иначе не переводятся.
    /// </summary>
    private void RegisterInlines(InlineCollection? inlines)
    {
        if (inlines is null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    RegisterRun(run);
                    break;
                case Span span:
                    RegisterInlines(span.Inlines);
                    break;
            }
        }
    }

    private const string OnContentPropertyName = "OnContent";
    private const string OffContentPropertyName = "OffContent";

    /// <summary>
    /// Регистрирует строковые <c>OnContent</c>/<c>OffContent</c> у <see cref="ToggleSwitch"/> —
    /// видимая подпись переключателя берётся из них, а не из обычного <c>Content</c>.
    /// </summary>
    private void RegisterToggleSwitch(ToggleSwitch toggleSwitch)
    {
        RegisterToggleSwitchContent(
            toggleSwitch, OnContentPropertyName, ToggleSwitch.OnContentProperty,
            () => toggleSwitch.OnContent as string,
            value => toggleSwitch.SetCurrentValue(ToggleSwitch.OnContentProperty, value));
        RegisterToggleSwitchContent(
            toggleSwitch, OffContentPropertyName, ToggleSwitch.OffContentProperty,
            () => toggleSwitch.OffContent as string,
            value => toggleSwitch.SetCurrentValue(ToggleSwitch.OffContentProperty, value));
    }

    private void RegisterToggleSwitchContent(
        ToggleSwitch toggleSwitch,
        string propertyName,
        AvaloniaProperty<object?> property,
        Func<string?> getter,
        Action<string?> setter)
    {
        var key = new LocalizationKey(toggleSwitch, propertyName);
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(toggleSwitch, toggleSwitch.GetObservable(property).Subscribe(value =>
        {
            if (value is string text)
            {
                HandleStringChanged(key, text, getter, setter);
            }
        }));

        if (getter() is string initial)
        {
            HandleStringChanged(key, initial, getter, setter);
        }
    }

    private void RegisterRun(Run run)
    {
        var key = new LocalizationKey(run, nameof(Run.Text));
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(run, run.GetObservable(Run.TextProperty).Subscribe(text =>
            HandleStringChanged(
                key,
                text,
                () => run.Text,
                value => run.SetCurrentValue(Run.TextProperty, value))));

        HandleStringChanged(key, run.Text, () => run.Text, value => run.SetCurrentValue(Run.TextProperty, value));
    }

    private void RegisterTextBlock(TextBlock textBlock)
    {
        var key = new LocalizationKey(textBlock, nameof(TextBlock.Text));
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(textBlock, textBlock.GetObservable(TextBlock.TextProperty).Subscribe(text =>
            HandleStringChanged(
                key,
                text,
                () => textBlock.Text,
                value => textBlock.SetCurrentValue(TextBlock.TextProperty, value))));

        HandleStringChanged(key, textBlock.Text, () => textBlock.Text, value => textBlock.SetCurrentValue(TextBlock.TextProperty, value));
    }

    private void RegisterContentControl(ContentControl contentControl)
    {
        var key = new LocalizationKey(contentControl, nameof(ContentControl.Content));
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(contentControl, contentControl.GetObservable(ContentControl.ContentProperty).Subscribe(content =>
        {
            if (content is string text)
            {
                HandleStringChanged(
                    key,
                    text,
                    () => contentControl.Content as string,
                    value => contentControl.SetCurrentValue(ContentControl.ContentProperty, value));
            }
        }));

        if (contentControl.Content is string initialText)
        {
            HandleStringChanged(key, initialText, () => contentControl.Content as string, value => contentControl.SetCurrentValue(ContentControl.ContentProperty, value));
        }
    }

    private void RegisterHeaderedControl(HeaderedContentControl headeredControl)
    {
        var key = new LocalizationKey(headeredControl, nameof(HeaderedContentControl.Header));
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(headeredControl, headeredControl.GetObservable(HeaderedContentControl.HeaderProperty).Subscribe(header =>
        {
            if (header is string text)
            {
                HandleStringChanged(
                    key,
                    text,
                    () => headeredControl.Header as string,
                    value => headeredControl.SetCurrentValue(HeaderedContentControl.HeaderProperty, value));
            }
        }));

        if (headeredControl.Header is string initialHeader)
        {
            HandleStringChanged(key, initialHeader, () => headeredControl.Header as string, value => headeredControl.SetCurrentValue(HeaderedContentControl.HeaderProperty, value));
        }
    }

    private void RegisterTextBox(TextBox textBox)
    {
        var key = new LocalizationKey(textBox, nameof(TextBox.PlaceholderText));
        if (!_observedProperties.Add(key))
        {
            return;
        }

        TrackSubscription(textBox, textBox.GetObservable(TextBox.PlaceholderTextProperty).Subscribe(placeholder =>
            HandleStringChanged(
                key,
                placeholder,
                () => textBox.PlaceholderText,
                value => textBox.SetCurrentValue(TextBox.PlaceholderTextProperty, value))));

        HandleStringChanged(key, textBox.PlaceholderText, () => textBox.PlaceholderText, value => textBox.SetCurrentValue(TextBox.PlaceholderTextProperty, value));
    }

    private void HandleStringChanged(
        LocalizationKey key,
        string? currentText,
        Func<string?> currentGetter,
        Action<string?> setter)
    {
        if (_isApplyingLocalization || string.IsNullOrWhiteSpace(currentText) || ShouldSkipTranslation(currentText))
        {
            return;
        }

        if (_sourceTexts.TryGetValue(key, out var existingSource))
        {
            var translatedExisting = _localization.Translate(existingSource);
            if (string.Equals(currentText, translatedExisting, StringComparison.Ordinal))
            {
                return;
            }
        }

        _sourceTexts[key] = currentText;

        if (_localization.CurrentLanguage == UiLanguage.Russian)
        {
            var translated = _localization.Translate(currentText);
            if (!string.Equals(currentGetter(), translated, StringComparison.Ordinal))
            {
                ApplyLocalizedValue(setter, translated);
            }
        }
    }

    private void ApplyLocalizationToObservedControls()
    {
        foreach (var entry in _sourceTexts)
        {
            var translated = _localization.CurrentLanguage == UiLanguage.English
                ? entry.Value
                : _localization.Translate(entry.Value);

            switch (entry.Key.Target)
            {
                case TextBlock textBlock when entry.Key.PropertyName == nameof(TextBlock.Text):
                    ApplyLocalizedValue(value => textBlock.SetCurrentValue(TextBlock.TextProperty, value), translated);
                    break;
                case ContentControl contentControl when entry.Key.PropertyName == nameof(ContentControl.Content):
                    ApplyLocalizedValue(value => contentControl.SetCurrentValue(ContentControl.ContentProperty, value), translated);
                    break;
                case HeaderedContentControl headeredControl when entry.Key.PropertyName == nameof(HeaderedContentControl.Header):
                    ApplyLocalizedValue(value => headeredControl.SetCurrentValue(HeaderedContentControl.HeaderProperty, value), translated);
                    break;
                case TextBox textBox when entry.Key.PropertyName == nameof(TextBox.PlaceholderText):
                    ApplyLocalizedValue(value => textBox.SetCurrentValue(TextBox.PlaceholderTextProperty, value), translated);
                    break;
                case Run run when entry.Key.PropertyName == nameof(Run.Text):
                    ApplyLocalizedValue(value => run.SetCurrentValue(Run.TextProperty, value), translated);
                    break;
                case ToggleSwitch onToggle when entry.Key.PropertyName == OnContentPropertyName:
                    ApplyLocalizedValue(value => onToggle.SetCurrentValue(ToggleSwitch.OnContentProperty, value), translated);
                    break;
                case ToggleSwitch offToggle when entry.Key.PropertyName == OffContentPropertyName:
                    ApplyLocalizedValue(value => offToggle.SetCurrentValue(ToggleSwitch.OffContentProperty, value), translated);
                    break;
                case Control control when entry.Key.PropertyName == ToolTipPropertyName:
                    ApplyLocalizedValue(value => ToolTip.SetTip(control, value), translated);
                    break;
            }
        }
    }

    private void ApplyLocalizedValue(Action<string?> setter, string? value)
    {
        _isApplyingLocalization = true;
        try
        {
            setter(value);
        }
        finally
        {
            _isApplyingLocalization = false;
        }
    }

    private static bool ShouldSkipTranslation(string text)
    {
        return text switch
        {
            "CRYPTO AI TERMINAL" => true,
            _ => false
        };
    }
}
