using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// "Morning briefing" card. Ties together the book (positions/P&L), the news pulse,
/// sentiment and the scanner into one AI narrative + a RISK_ON/NEUTRAL/RISK_OFF
/// posture. Backed by <see cref="DailyBriefingService"/> (Claude when keyed, else an
/// offline heuristic). The host supplies a gather delegate so this VM stays decoupled
/// from the data panels; the Claude key/model are shared via <see cref="ConfigureAi"/>.
/// </summary>
public sealed class DailyBriefingViewModel : ReactiveObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "briefing-settings.json");

    private sealed record BriefingSettings(bool AutoEnabled, int AutoHour, string LastRunDate);

    private readonly DailyBriefingService _service = new();
    private readonly Func<BriefingInput> _gather;
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    private string _summary = "Press Refresh to generate your morning briefing.";
    private string _signal = "";
    private string _source = "";
    private bool _running;
    private string _generatedAt = "";
    private bool _autoEnabled = true;
    private int _autoHour = 9;
    private DateTime _lastRunDate = DateTime.MinValue;

    /// <summary>Raised after an automatic morning run so the host can push a toast.</summary>
    public event Action<string>? BriefingReady;

    public DailyBriefingViewModel(Func<BriefingInput> gather)
    {
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync, outputScheduler: App.UiScheduler);
        LoadSettings();
        _autoTimer.Tick += OnAutoTick;
        _autoTimer.Start();
    }

    /// <summary>Auto-generate a briefing once each morning (and on launch if missed).</summary>
    public bool AutoEnabled
    {
        get => _autoEnabled;
        set { this.RaiseAndSetIfChanged(ref _autoEnabled, value); SaveSettings(); }
    }

    /// <summary>Local hour (0-23) at/after which the morning briefing runs.</summary>
    public int AutoHour
    {
        get => _autoHour;
        set { this.RaiseAndSetIfChanged(ref _autoHour, Math.Clamp(value, 0, 23)); SaveSettings(); }
    }

    private async void OnAutoTick(object? sender, EventArgs e)
    {
        if (!_autoEnabled || Running) return;
        var now = DateTime.Now;
        if (now.Date <= _lastRunDate.Date) return; // already ran today
        if (now.Hour < _autoHour) return;          // not yet time
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(Summary))
            BriefingReady?.Invoke("🌅 Утренний брифинг\n" + Summary);
    }

    private void SaveSettings()
    {
        try
        {
            AtomicJsonFile.Write(SettingsPath,
                new BriefingSettings(_autoEnabled, _autoHour, _lastRunDate.ToString("yyyy-MM-dd")));
        }
        catch { /* best-effort */ }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = AtomicJsonFile.Read<BriefingSettings>(SettingsPath);
            if (s is null) return;
            _autoEnabled = s.AutoEnabled;
            _autoHour = Math.Clamp(s.AutoHour, 0, 23);
            if (DateTime.TryParse(s.LastRunDate, out var d)) _lastRunDate = d;
        }
        catch { /* ignore corrupt settings */ }
    }

    /// <summary>Share the Claude key/model from the AI Bot tab.</summary>
    public void ConfigureAi(string? apiKey, string? model = null) => _service.ConfigureAi(apiKey, model);

    public ObservableCollection<string> Bullets { get; } = [];

    public string Summary     { get => _summary;     private set => this.RaiseAndSetIfChanged(ref _summary, value); }
    public string Source      { get => _source;      private set => this.RaiseAndSetIfChanged(ref _source, value); }
    public bool   Running     { get => _running;     private set => this.RaiseAndSetIfChanged(ref _running, value); }
    public string GeneratedAt { get => _generatedAt; private set => this.RaiseAndSetIfChanged(ref _generatedAt, value); }

    public string Signal
    {
        get => _signal;
        private set
        {
            this.RaiseAndSetIfChanged(ref _signal, value);
            this.RaisePropertyChanged(nameof(SignalLabel));
            this.RaisePropertyChanged(nameof(SignalBrush));
            this.RaisePropertyChanged(nameof(HasSignal));
        }
    }

    public bool   HasSignal   => !string.IsNullOrEmpty(_signal);
    public string SignalLabel => _signal switch
    {
        "RISK_ON"  => "RISK-ON",
        "RISK_OFF" => "RISK-OFF",
        ""         => "",
        _          => "NEUTRAL"
    };
    public string SignalBrush => _signal switch
    {
        "RISK_ON"  => "#21E6C1",
        "RISK_OFF" => "#FF6B6B",
        _          => "#8FA3B8"
    };

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private async Task RefreshAsync()
    {
        if (Running) return;
        Running = true;
        try
        {
            var input = _gather();
            var result = await _service.BuildAsync(input, CancellationToken.None);

            Summary = result.Summary;
            Signal = result.Signal;
            Source = result.Source;
            GeneratedAt = DateTime.Now.ToString("HH:mm");

            Bullets.Clear();
            foreach (var b in result.Bullets) Bullets.Add(b);

            _lastRunDate = DateTime.Now;
            SaveSettings();
        }
        catch (Exception ex)
        {
            Summary = $"Couldn't build the briefing: {ex.Message}";
            Signal = "";
        }
        finally
        {
            Running = false;
        }
    }
}
