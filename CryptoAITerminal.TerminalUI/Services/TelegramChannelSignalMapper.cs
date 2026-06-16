using System.Globalization;
using System.Text;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// A raw new-message event observed on a monitored Telegram channel/group.
/// Emitted by <see cref="TelegramUserClientService"/> on a background thread.
/// </summary>
public sealed record TelegramChannelMessage(
    long   ChannelId,
    string ChannelTitle,
    long   MessageId,
    string Text);

/// <summary>
/// Pure formatting helpers that turn a <see cref="ParsedTelegramSignal"/> into the
/// human-readable strings the UI signal queue shows. Kept free of any ViewModel or
/// Telegram dependency so the mapping is unit-testable.
/// </summary>
public static class TelegramChannelSignalMapper
{
    /// <summary>
    /// One-line summary, e.g. "from Whale Calls • entry 65000 • TP 66000/67000 • SL 63000 • 10x".
    /// Missing fields are omitted rather than shown as blanks.
    /// </summary>
    public static string DescribeSignal(ParsedTelegramSignal signal, string channelTitle)
    {
        var parts = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(channelTitle))
        {
            parts.Append("from ").Append(channelTitle.Trim());
        }

        if (signal.Entry is { } entry)
        {
            Append(parts, "entry " + Num(entry));
        }

        if (signal.Targets.Count > 0)
        {
            var targets = new StringBuilder("TP ");
            for (var i = 0; i < signal.Targets.Count; i++)
            {
                if (i > 0) targets.Append('/');
                targets.Append(Num(signal.Targets[i]));
            }
            Append(parts, targets.ToString());
        }

        if (signal.StopLoss is { } stop)
        {
            Append(parts, "SL " + Num(stop));
        }

        if (signal.Leverage is { } lev)
        {
            Append(parts, lev + "x");
        }

        return parts.ToString();
    }

    private static void Append(StringBuilder sb, string segment)
    {
        if (sb.Length > 0) sb.Append(" • ");
        sb.Append(segment);
    }

    private static string Num(decimal value) =>
        value.ToString("0.########", CultureInfo.InvariantCulture);
}
