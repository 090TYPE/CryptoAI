using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One external wallet being watched on the Copy Trading page.</summary>
public sealed class WatchedWalletItemViewModel : ReactiveObject
{
    private bool _copy;

    public WatchedWalletItemViewModel(string address, string label, string chain, bool copy = false)
    {
        Address = address;
        Label = label;
        Chain = chain;
        _copy = copy;
    }

    public string Address { get; }
    public string Label { get; }
    public string Chain { get; }

    /// <summary>When true, buys from this wallet raise a high-priority "copy this" alert.</summary>
    public bool Copy
    {
        get => _copy;
        set => this.RaiseAndSetIfChanged(ref _copy, value);
    }

    public string DisplayAddress =>
        string.IsNullOrWhiteSpace(Address) || Address.Length <= 12
            ? Address
            : $"{Address[..6]}...{Address[^4..]}";

    public string Summary =>
        $"{(string.IsNullOrWhiteSpace(Label) ? "—" : Label)} · {Chain} · {DisplayAddress}";
}
