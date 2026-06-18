using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
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
    private readonly List<IDisposable> _localizationSubscriptions = [];
    private DateTime _splashStartedAt;
    private bool _splashCompleted;
    private bool _isApplyingLocalization;

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
            foreach (var subscription in _localizationSubscriptions)
                subscription.Dispose();

            if (ViewModel?.AIBotVM is { } botVm)
            {
                try { botVm.StopBotAsync().Wait(TimeSpan.FromSeconds(5)); }
                catch { /* swallow during shutdown */ }
            }
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

        // Single-key trading hotkeys (fire only when no text-input control is focused)
        AddHandler(KeyDownEvent, OnTradingHotkeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Handles trading hotkeys (B=Buy, S=Sell, 1/2/3=Allocation, Esc=Cancel, F=FocusPair).
    /// Skips when a TextBox, NumericUpDown or ComboBox is focused so typing is never intercepted.
    /// </summary>
    private void OnTradingHotkeyDown(object? sender, KeyEventArgs e)
    {
        // Don't fire if user is typing in an input control
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is TextBox or NumericUpDown or ComboBox) return;

        // Don't steal keys with modifiers (those are handled by Window.KeyBindings)
        if (e.KeyModifiers != KeyModifiers.None) return;

        var vm = ViewModel;
        if (vm is null) return;

        var hs = vm.HotkeySettings;

        if      (e.Key == hs.BuyMarketKey)     { vm.BuyMarketCommand.Execute(Unit.Default).Subscribe();    e.Handled = true; }
        else if (e.Key == hs.SellMarketKey)     { vm.SellMarketCommand.Execute(Unit.Default).Subscribe();   e.Handled = true; }
        else if (e.Key == hs.Allocation25Key)   { vm.HotkeyAlloc25Command.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.Allocation50Key)   { vm.HotkeyAlloc50Command.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.Allocation100Key)  { vm.HotkeyAlloc100Command.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.CancelOrdersKey)   { vm.CancelAllOrdersCommand.Execute(Unit.Default).Subscribe(); e.Handled = true; }
        else if (e.Key == hs.FocusPairKey)      { vm.FocusTradingPairCommand.Execute(Unit.Default).Subscribe(); e.Handled = true; }
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

    private void OnToastClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        ViewModel?.OpenNotificationCenter();
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

    private void ConfigureFullscreenWindow()
    {
        WindowState = WindowState.FullScreen;
    }

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

    /// <summary>
    /// Один тик сканирования. Считает, сколько новых контролов было зарегистрировано.
    /// После <see cref="LocalizationScanStableThreshold"/> тиков подряд без новых
    /// регистраций таймер останавливается, чтобы не жрать CPU на стабильном дереве.
    /// </summary>
    private void RunLocalizationScanTick()
    {
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
        EnglishLanguageButton.Background = isEnglish ? Brush.Parse("#17373B") : Brush.Parse("#0F1721");
        EnglishLanguageButton.Foreground = isEnglish ? Brush.Parse("#F4F7FB") : Brush.Parse("#8FA3B8");
        RussianLanguageButton.Background = isEnglish ? Brush.Parse("#0F1721") : Brush.Parse("#17373B");
        RussianLanguageButton.Foreground = isEnglish ? Brush.Parse("#8FA3B8") : Brush.Parse("#F4F7FB");
    }

    private void AttachLocalizationObservers()
    {
        RegisterTextBlock(SplashStatusText);

        foreach (var visual in this.GetVisualDescendants())
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

        _localizationSubscriptions.Add(control.GetObservable(ToolTip.TipProperty).Subscribe(tip =>
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

        _localizationSubscriptions.Add(toggleSwitch.GetObservable(property).Subscribe(value =>
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

        _localizationSubscriptions.Add(run.GetObservable(Run.TextProperty).Subscribe(text =>
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

        _localizationSubscriptions.Add(textBlock.GetObservable(TextBlock.TextProperty).Subscribe(text =>
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

        _localizationSubscriptions.Add(contentControl.GetObservable(ContentControl.ContentProperty).Subscribe(content =>
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

        _localizationSubscriptions.Add(headeredControl.GetObservable(HeaderedContentControl.HeaderProperty).Subscribe(header =>
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

        _localizationSubscriptions.Add(textBox.GetObservable(TextBox.PlaceholderTextProperty).Subscribe(placeholder =>
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
