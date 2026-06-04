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
/// "AI Risk Check" card. The user describes a proposed order; the
/// <see cref="PreTradeRiskService"/> scores it against the whole book (size, exposure,
/// concentration, leverage, daily-loss proximity) and returns APPROVE/CAUTION/BLOCK
/// with reasons. Verdict/score are deterministic; an AI rationale is layered on when a
/// key is configured. Advisory only — it never touches the live order path.
/// The host supplies a delegate that fills the account context for a typed order.
/// </summary>
public sealed class PreTradeRiskViewModel : ReactiveObject
{
    private readonly PreTradeRiskService _service = new();
    private readonly Func<string, string, decimal, int, PreTradeRiskInput> _buildInput;

    private string _symbol = "BTCUSDT";
    private string _side = "buy";
    private decimal _orderUsd = 50m;
    private int _leverage = 1;

    private string _verdict = "";
    private int _score;
    private string _rationale = "Describe an order and press Check.";
    private string _source = "";
    private bool _running;

    public PreTradeRiskViewModel(Func<string, string, decimal, int, PreTradeRiskInput> buildInput)
    {
        _buildInput = buildInput ?? throw new ArgumentNullException(nameof(buildInput));
        CheckCommand = ReactiveCommand.CreateFromTask(CheckAsync, outputScheduler: App.UiScheduler);
    }

    public void ConfigureAi(string? apiKey, string? model = null) => _service.ConfigureAi(apiKey, model);

    public IReadOnlyList<string> AvailableSides { get; } = ["buy", "sell"];

    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, (value ?? "").ToUpperInvariant());
    }

    public string Side
    {
        get => _side;
        set => this.RaiseAndSetIfChanged(ref _side, value ?? "buy");
    }

    public decimal OrderUsd
    {
        get => _orderUsd;
        set => this.RaiseAndSetIfChanged(ref _orderUsd, Math.Max(0m, value));
    }

    public int Leverage
    {
        get => _leverage;
        set => this.RaiseAndSetIfChanged(ref _leverage, Math.Clamp(value, 1, 125));
    }

    public ObservableCollection<string> Reasons { get; } = [];

    public string Rationale { get => _rationale; private set => this.RaiseAndSetIfChanged(ref _rationale, value); }
    public string Source    { get => _source;    private set => this.RaiseAndSetIfChanged(ref _source, value); }
    public bool   Running   { get => _running;   private set => this.RaiseAndSetIfChanged(ref _running, value); }

    public int Score
    {
        get => _score;
        private set { this.RaiseAndSetIfChanged(ref _score, value); this.RaisePropertyChanged(nameof(ScoreLabel)); }
    }

    public string Verdict
    {
        get => _verdict;
        private set
        {
            this.RaiseAndSetIfChanged(ref _verdict, value);
            this.RaisePropertyChanged(nameof(VerdictBrush));
            this.RaisePropertyChanged(nameof(HasVerdict));
        }
    }

    public bool   HasVerdict  => !string.IsNullOrEmpty(_verdict);
    public string ScoreLabel  => $"{_score}/100";
    public string VerdictBrush => _verdict switch
    {
        "APPROVE" => "#21E6C1",
        "CAUTION" => "#F4B860",
        "BLOCK"   => "#FF6B6B",
        _         => "#8FA3B8"
    };

    public ReactiveCommand<Unit, Unit> CheckCommand { get; }

    private async Task CheckAsync()
    {
        if (Running) return;
        Running = true;
        try
        {
            var input = _buildInput(Symbol, Side, OrderUsd, Leverage);
            var r = await _service.EvaluateAsync(input, CancellationToken.None);

            Verdict = r.Verdict;
            Score = r.Score;
            Rationale = r.Rationale;
            Source = r.Source;

            Reasons.Clear();
            foreach (var reason in r.Reasons) Reasons.Add(reason);
        }
        catch (Exception ex)
        {
            Rationale = $"Couldn't evaluate: {ex.Message}";
            Verdict = "";
        }
        finally
        {
            Running = false;
        }
    }
}
