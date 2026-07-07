using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// One cell of the global market-stats strip at the top of the Markets board
/// (TOTAL MKT CAP, 24H VOLUME, BTC DOMINANCE, …). Mutable so the strip can tick
/// in place without rebuilding the collection.
/// </summary>
public sealed class MarketStatCellViewModel : ReactiveObject
{
    private string _value = "--";
    private string _valueBrush = "#E8F4FF";
    private string? _delta;
    private string _deltaBrush = "#3DDC84";

    public MarketStatCellViewModel(string label) => Label = label;

    /// <summary>Tiny upper-case caption above the value.</summary>
    public string Label { get; }

    public string Value
    {
        get => _value;
        set => this.RaiseAndSetIfChanged(ref _value, value);
    }

    public string ValueBrush
    {
        get => _valueBrush;
        set => this.RaiseAndSetIfChanged(ref _valueBrush, value);
    }

    /// <summary>Third line: a signed % delta or a regime label. Null hides it.</summary>
    public string? Delta
    {
        get => _delta;
        set
        {
            this.RaiseAndSetIfChanged(ref _delta, value);
            this.RaisePropertyChanged(nameof(HasDelta));
        }
    }

    public bool HasDelta => !string.IsNullOrEmpty(_delta);

    public string DeltaBrush
    {
        get => _deltaBrush;
        set => this.RaiseAndSetIfChanged(ref _deltaBrush, value);
    }
}
