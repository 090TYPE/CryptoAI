using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly DailyBriefingService _service = new();
    private readonly Func<BriefingInput> _gather;

    private string _summary = "Press Refresh to generate your morning briefing.";
    private string _signal = "";
    private string _source = "";
    private bool _running;
    private string _generatedAt = "";

    public DailyBriefingViewModel(Func<BriefingInput> gather)
    {
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync, outputScheduler: App.UiScheduler);
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
