namespace CryptoAITerminal.TerminalUI.ViewModels;

public class SniperLiveReadyStatusViewModel
{
    public string NetworkLabel { get; set; } = string.Empty;
    public string DexLabel { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public string DisplayLabel => $"{NetworkLabel} / {DexLabel}";
    public string AccentHex => IsReady ? "#3DDC84" : "#F5C451";
}
