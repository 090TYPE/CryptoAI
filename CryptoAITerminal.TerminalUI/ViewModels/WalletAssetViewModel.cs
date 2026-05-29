using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class WalletAssetViewModel : ReactiveObject
{
    private string _title = string.Empty;
    private string _value = string.Empty;
    private string _subtitle = string.Empty;

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public string Value
    {
        get => _value;
        set => this.RaiseAndSetIfChanged(ref _value, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => this.RaiseAndSetIfChanged(ref _subtitle, value);
    }
}
