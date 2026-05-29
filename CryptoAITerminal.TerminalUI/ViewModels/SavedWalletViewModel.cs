using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class SavedWalletViewModel : ReactiveObject
{
    private string _provider = string.Empty;
    private string _network = string.Empty;
    private string _address = string.Empty;
    private bool _isReadOnly = true;
    private string _note = string.Empty;

    public string Provider
    {
        get => _provider;
        set => this.RaiseAndSetIfChanged(ref _provider, value);
    }

    public string Network
    {
        get => _network;
        set => this.RaiseAndSetIfChanged(ref _network, value);
    }

    public string Address
    {
        get => _address;
        set => this.RaiseAndSetIfChanged(ref _address, value);
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
    }

    public string Note
    {
        get => _note;
        set => this.RaiseAndSetIfChanged(ref _note, value);
    }

    public string DisplayAddress =>
        string.IsNullOrWhiteSpace(Address) || Address.Length <= 12
            ? Address
            : $"{Address[..6]}...{Address[^4..]}";

    public string ModeLabel => IsReadOnly ? "Watch profile" : "Trading profile";
    public string Summary => $"{Provider} · {Network} · {ModeLabel}";
}
