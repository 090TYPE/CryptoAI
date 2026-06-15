using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// A trading signal extracted from a free-text Telegram channel message.
/// <see cref="IsValid"/> is true only when both a symbol and a direction were found.
/// </summary>
public sealed record ParsedTelegramSignal(
    bool IsValid,
    string Symbol,
    string Side,
    decimal? Entry,
    IReadOnlyList<decimal> Targets,
    decimal? StopLoss,
    int? Leverage,
    string RawText)
{
    public static ParsedTelegramSignal Invalid(string raw) =>
        new(false, string.Empty, string.Empty, null, Array.Empty<decimal>(), null, null, raw);
}

/// <summary>
/// Heuristic parser for the common crypto-call channel formats, e.g.
/// "🚀 LONG $BTC / Entry: 65000 / Targets: 66000, 67000 / Stop: 63000 / 10x".
/// Pure and deterministic so it can be unit-tested without any Telegram connection.
/// </summary>
public static class TelegramSignalParser
{
    // Strip thousands separators ("66,000" -> "66000") without eating list commas ("66000, 67000").
    private static readonly Regex ThousandsSeparator = new(@",(?=\d{3}(?:\D|$))", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex SymbolPair = new(@"\b([A-Z]{2,10})\s*[\/\-]?\s*(USDT|USD|BUSD|USDC|BTC|PERP)\b", RegexOptions.Compiled);
    private static readonly Regex SymbolTag = new(@"[#$]([A-Z]{2,10})\b", RegexOptions.Compiled);
    private static readonly Regex LeverageX = new(@"(\d{1,3})\s*[Xx]\b", RegexOptions.Compiled);
    private static readonly Regex LeverageWord = new(@"LEVERAGE[:\s]*(\d{1,3})", RegexOptions.Compiled);

    public static ParsedTelegramSignal Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ParsedTelegramSignal.Invalid(message ?? string.Empty);
        }

        var normalized = ThousandsSeparator.Replace(message, string.Empty);
        var upper = normalized.ToUpperInvariant();

        var side = DetectSide(upper);
        var symbol = DetectSymbol(upper);
        if (side is null || symbol is null)
        {
            return ParsedTelegramSignal.Invalid(message);
        }

        var lines = upper.Split('\n');
        var entry = FirstNumberAfterKeyword(lines, "ENTRY ZONE", "BUY ZONE", "BUY-ZONE", "ENTRY");
        var stop = FirstNumberAfterKeyword(lines, "STOP LOSS", "STOP-LOSS", "STOPLOSS", "STOP", "SL");
        var targets = ParseTargets(lines);
        var leverage = DetectLeverage(upper);

        return new ParsedTelegramSignal(true, symbol, side, entry, targets, stop, leverage, message);
    }

    private static string? DetectSide(string upper)
    {
        var isLong = Regex.IsMatch(upper, @"\b(LONG|BUY)\b");
        var isShort = Regex.IsMatch(upper, @"\b(SHORT|SELL)\b");
        if (isLong == isShort)
        {
            return null; // neither, or contradictory — not a clean signal
        }

        return isLong ? "BUY" : "SELL";
    }

    private static string? DetectSymbol(string upper)
    {
        var pair = SymbolPair.Match(upper);
        if (pair.Success)
        {
            return pair.Groups[1].Value + pair.Groups[2].Value;
        }

        var tag = SymbolTag.Match(upper);
        return tag.Success ? tag.Groups[1].Value : null;
    }

    private static int? DetectLeverage(string upper)
    {
        var word = LeverageWord.Match(upper);
        if (word.Success && int.TryParse(word.Groups[1].Value, out var w) && w > 0)
        {
            return w;
        }

        var x = LeverageX.Match(upper);
        return x.Success && int.TryParse(x.Groups[1].Value, out var v) && v > 0 ? v : null;
    }

    // Returns the first price that appears *after* a keyword (so "Entry 100 SL 90"
    // yields 100 for ENTRY and 90 for SL, not whichever number comes first on the line).
    private static decimal? FirstNumberAfterKeyword(string[] lines, params string[] keywords)
    {
        foreach (var line in lines)
        {
            var after = EarliestIndexAfterKeyword(line, keywords);
            if (after < 0)
            {
                continue;
            }

            var match = Number.Match(line.Substring(after));
            if (match.Success && TryDecimal(match.Value, out var value))
            {
                return value;
            }
        }

        return null;
    }

    // Prices on any "Targets:" / "TP1" / "TP2" line. The TARGETS?/TP\d* label is consumed
    // first so a "TP1" index is never mistaken for a price.
    private static IReadOnlyList<decimal> ParseTargets(string[] lines)
    {
        var result = new List<decimal>();
        foreach (var line in lines)
        {
            var label = Regex.Match(line, @"\bTARGETS?\b|\bTP\d*\b");
            if (!label.Success)
            {
                continue;
            }

            foreach (Match match in Number.Matches(line.Substring(label.Index + label.Length)))
            {
                if (TryDecimal(match.Value, out var value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    private static int EarliestIndexAfterKeyword(string line, string[] keywords)
    {
        var best = -1;
        foreach (var keyword in keywords)
        {
            var match = Regex.Match(line, $@"\b{Regex.Escape(keyword)}\b");
            if (!match.Success)
            {
                continue;
            }

            var after = match.Index + match.Length;
            if (best < 0 || after < best)
            {
                best = after;
            }
        }

        return best;
    }

    private static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
