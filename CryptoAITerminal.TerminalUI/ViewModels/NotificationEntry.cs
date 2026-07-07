using System;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One fired notification kept in the in-app notification registry.</summary>
public sealed class NotificationEntry
{
    public NotificationEntry(
        string message, DateTime timeLocal,
        string? symbol = null, string? tokenAddress = null, string? chain = null)
    {
        Message = message;
        TimeLabel = timeLocal.ToString("dd.MM HH:mm:ss");
        Symbol = symbol;
        TokenAddress = tokenAddress;
        Chain = chain;
    }

    public string Message { get; }
    public string TimeLabel { get; }

    /// <summary>Trading symbol this notification refers to, if any (enables click-to-open).</summary>
    public string? Symbol { get; }

    /// <summary>On-chain token address this notification refers to, if any (DEX deep-link).</summary>
    public string? TokenAddress { get; }

    /// <summary>Chain id for the token address, if known (DEX deep-link).</summary>
    public string? Chain { get; }

    /// <summary>Any deep-link target — a symbol or a token address — makes the entry actionable.</summary>
    public bool HasAction => !string.IsNullOrEmpty(Symbol) || !string.IsNullOrEmpty(TokenAddress);

    public string ActionLabel => HasAction
        ? $"→ Открыть {(string.IsNullOrEmpty(Symbol) ? "токен" : Symbol)}"
        : string.Empty;
}
