namespace CryptoAITerminal.WhaleTracker.Models;

public sealed class WhaleAlert
{
    public required WhaleTransfer Transfer { get; init; }
    public LabeledWallet? FromLabel { get; init; }
    public LabeledWallet? ToLabel   { get; init; }

    /// "LargeTransfer" | "WalletActivity"
    public required string AlertType { get; init; }

    public bool IsLabeledWalletActivity => FromLabel is not null || ToLabel is not null;

    public string ActiveLabel => FromLabel?.Label ?? ToLabel?.Label ?? string.Empty;
    public string ActiveCategory => FromLabel?.Category ?? ToLabel?.Category ?? string.Empty;
}
