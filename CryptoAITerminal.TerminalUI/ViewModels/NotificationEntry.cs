using System;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One fired notification kept in the in-app notification registry.</summary>
public sealed class NotificationEntry
{
    public NotificationEntry(string message, DateTime timeLocal, string? symbol = null)
    {
        Message = message;
        TimeLabel = timeLocal.ToString("dd.MM HH:mm:ss");
        Symbol = symbol;
    }

    public string Message { get; }
    public string TimeLabel { get; }

    /// <summary>Trading symbol this notification refers to, if any (enables click-to-open).</summary>
    public string? Symbol { get; }

    public bool HasAction => !string.IsNullOrEmpty(Symbol);
    public string ActionLabel => HasAction ? $"→ Открыть {Symbol}" : string.Empty;
}
