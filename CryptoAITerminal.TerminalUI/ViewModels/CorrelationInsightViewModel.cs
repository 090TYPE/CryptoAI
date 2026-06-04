using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// "AI Correlation Insight" card. Builds a correlation matrix from recent daily
/// closes for a small symbol set, then has <see cref="CorrelationInsightService"/>
/// read it into a DIVERSIFIED / BALANCED / CONCENTRATED posture with the tightest
/// clusters. Held symbols (from the open book) are weighted more heavily. The host
/// supplies the price-fetch and held-symbols delegates so the VM stays decoupled.
/// </summary>
public sealed class CorrelationInsightViewModel : ReactiveObject
{
    private readonly CorrelationInsightService _service = new();
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<(DateTime ts, decimal close)>>> _fetchCloses;
    private readonly Func<IReadOnlyCollection<string>> _heldSymbols;

    private string _symbols = "BTCUSDT, ETHUSDT, SOLUSDT, BNBUSDT";
    private string _summary = "Press Analyze to read your diversification.";
    private string _signal = "";
    private string _source = "";
    private bool _running;

    public CorrelationInsightViewModel(
        Func<string, CancellationToken, Task<IReadOnlyList<(DateTime ts, decimal close)>>> fetchCloses,
        Func<IReadOnlyCollection<string>> heldSymbols)
    {
        _fetchCloses = fetchCloses ?? throw new ArgumentNullException(nameof(fetchCloses));
        _heldSymbols = heldSymbols ?? (() => Array.Empty<string>());
        AnalyzeCommand = ReactiveCommand.CreateFromTask(AnalyzeAsync, outputScheduler: App.UiScheduler);
    }

    public void ConfigureAi(string? apiKey, string? model = null) => _service.ConfigureAi(apiKey, model);

    public ObservableCollection<string> Bullets { get; } = [];

    public string Symbols
    {
        get => _symbols;
        set => this.RaiseAndSetIfChanged(ref _symbols, value ?? "");
    }

    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }
    public string Source  { get => _source;  private set => this.RaiseAndSetIfChanged(ref _source, value); }
    public bool   Running { get => _running; private set => this.RaiseAndSetIfChanged(ref _running, value); }

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
        "DIVERSIFIED"  => "DIVERSIFIED",
        "CONCENTRATED" => "CONCENTRATED",
        ""             => "",
        _              => "BALANCED"
    };
    public string SignalBrush => _signal switch
    {
        "DIVERSIFIED"  => "#21E6C1",
        "CONCENTRATED" => "#FF6B6B",
        _              => "#F4B860"
    };

    public ReactiveCommand<Unit, Unit> AnalyzeCommand { get; }

    private async Task AnalyzeAsync()
    {
        if (Running) return;
        Running = true;
        try
        {
            var symbols = _symbols
                .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .Distinct()
                .Take(12)
                .ToList();

            if (symbols.Count < 2)
            {
                Summary = "Enter at least two symbols (comma-separated).";
                Signal = "";
                return;
            }

            var matrixSvc = new CorrelationMatrixService();
            var fetched = 0;
            foreach (var sym in symbols)
            {
                try
                {
                    var closes = await _fetchCloses(sym, CancellationToken.None);
                    if (closes.Count > 1) { matrixSvc.AddSamples(sym, closes); fetched++; }
                }
                catch { /* skip symbols the gateway can't serve */ }
            }

            if (fetched < 2)
            {
                Summary = "Couldn't load enough price history to compare these symbols.";
                Signal = "";
                Bullets.Clear();
                return;
            }

            var matrix = matrixSvc.Compute();
            var held = _heldSymbols();
            var result = await _service.AnalyzeAsync(matrix, held, CancellationToken.None);

            Summary = result.Summary;
            Signal = result.Signal;
            Source = result.Source;
            Bullets.Clear();
            foreach (var b in result.Bullets) Bullets.Add(b);
        }
        catch (Exception ex)
        {
            Summary = $"Couldn't analyze correlations: {ex.Message}";
            Signal = "";
        }
        finally
        {
            Running = false;
        }
    }
}
