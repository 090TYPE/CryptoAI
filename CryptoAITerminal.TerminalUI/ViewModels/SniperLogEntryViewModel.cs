using System;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class SniperLogEntryViewModel
{
    public DateTime LocalTime { get; init; } = DateTime.Now;
    public string Message { get; init; } = string.Empty;
    public bool IsPositive { get; init; }
}
