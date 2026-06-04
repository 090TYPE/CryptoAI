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
/// "AI Options Advisor" card. Given the Deribit IV/skew/put-call snapshot for BTC or
/// ETH plus a directional view, <see cref="OptionsStrategyService"/> suggests a fitting
/// options structure (deterministic) with an AI-written rationale when keyed. The host
/// supplies a delegate that turns (asset, direction) into the market input from the
/// Sentiment tab's live snapshot. Educational — never trades.
/// </summary>
public sealed class OptionsStrategyViewModel : ReactiveObject
{
    private readonly OptionsStrategyService _service = new();
    private readonly Func<string, string, OptionsStrategyInput?> _buildInput;

    private string _asset = "BTC";
    private string _direction = "neutral";
    private string _strategy = "";
    private string _ivRegime = "";
    private string _rationale = "Pick an asset and view, then press Suggest.";
    private string _source = "";
    private bool _running;

    public OptionsStrategyViewModel(Func<string, string, OptionsStrategyInput?> buildInput)
    {
        _buildInput = buildInput ?? throw new ArgumentNullException(nameof(buildInput));
        SuggestCommand = ReactiveCommand.CreateFromTask(SuggestAsync, outputScheduler: App.UiScheduler);
    }

    public void ConfigureAi(string? apiKey, string? model = null) => _service.ConfigureAi(apiKey, model);

    public IReadOnlyList<string> AvailableAssets { get; } = ["BTC", "ETH"];
    public IReadOnlyList<string> AvailableDirections { get; } = ["bullish", "neutral", "bearish"];

    public string Asset
    {
        get => _asset;
        set => this.RaiseAndSetIfChanged(ref _asset, value ?? "BTC");
    }

    public string Direction
    {
        get => _direction;
        set => this.RaiseAndSetIfChanged(ref _direction, value ?? "neutral");
    }

    public ObservableCollection<string> Considerations { get; } = [];

    public string Rationale { get => _rationale; private set => this.RaiseAndSetIfChanged(ref _rationale, value); }
    public string Source    { get => _source;    private set => this.RaiseAndSetIfChanged(ref _source, value); }
    public bool   Running   { get => _running;   private set => this.RaiseAndSetIfChanged(ref _running, value); }

    public string Strategy
    {
        get => _strategy;
        private set { this.RaiseAndSetIfChanged(ref _strategy, value); this.RaisePropertyChanged(nameof(HasResult)); }
    }

    public string IvRegime
    {
        get => _ivRegime;
        private set { this.RaiseAndSetIfChanged(ref _ivRegime, value); this.RaisePropertyChanged(nameof(IvRegimeBrush)); }
    }

    public bool   HasResult => !string.IsNullOrEmpty(_strategy);
    public string IvRegimeBrush => _ivRegime switch
    {
        "High"     => "#FF6B6B",
        "Low"      => "#21E6C1",
        "Moderate" => "#F4B860",
        _          => "#8FA3B8"
    };

    public ReactiveCommand<Unit, Unit> SuggestCommand { get; }

    private async Task SuggestAsync()
    {
        if (Running) return;
        Running = true;
        try
        {
            var input = _buildInput(Asset, Direction);
            if (input is null)
            {
                Rationale = "No options data loaded yet — open the Sentiment tab to fetch the Deribit snapshot, then retry.";
                Strategy = "";
                IvRegime = "";
                Considerations.Clear();
                return;
            }

            var r = await _service.RecommendAsync(input, CancellationToken.None);

            Strategy = r.Strategy;
            IvRegime = r.IvRegime;
            Rationale = r.Rationale;
            Source = r.Source;
            Considerations.Clear();
            foreach (var c in r.Considerations) Considerations.Add(c);
        }
        catch (Exception ex)
        {
            Rationale = $"Couldn't build a suggestion: {ex.Message}";
            Strategy = "";
        }
        finally
        {
            Running = false;
        }
    }
}
