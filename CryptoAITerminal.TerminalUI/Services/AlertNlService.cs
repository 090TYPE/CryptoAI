using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed record AlertParseResult(
    bool Success, string Symbol, AlertCondition Condition, decimal Threshold,
    string Explanation, string Source, bool IsFallback);

/// <summary>
/// Turns a plain-English request ("ping me if BTC breaks 75k", "tell me when ETH
/// drops 10% in an hour") into a structured <see cref="PriceAlert"/> the alerts panel
/// can add. Uses Claude when a key is configured, otherwise a deterministic regex
/// parser — so the feature works keyless in the demo. The parser is the core value
/// and is fully unit-tested.
/// </summary>
public sealed class AlertNlService
{
    private static readonly string[] ConditionNames = Enum.GetNames(typeof(AlertCondition));

    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<AlertParseResult> ParseAsync(string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return Fail("Type what you want to be alerted about.");

        if (UsesLiveModel)
        {
            try
            {
                var provider = new AlertSpecAiProvider(ApiKey, Model);
                var spec = await provider.ParseAsync(text, ConditionNames, ct).ConfigureAwait(false);
                if (spec is not null &&
                    Enum.TryParse<AlertCondition>(spec.Condition, ignoreCase: true, out var cond) &&
                    !string.IsNullOrWhiteSpace(spec.Symbol))
                {
                    var sym = NormalizeSymbol(spec.Symbol);
                    return new AlertParseResult(true, sym, cond, spec.Threshold,
                        Describe(sym, cond, spec.Threshold), spec.Source, false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return ParseOffline(text);
    }

    /// <summary>Deterministic regex parser — pure and unit-tested.</summary>
    public static AlertParseResult ParseOffline(string text)
    {
        var lower = text.ToLowerInvariant();

        var symbol = ExtractSymbol(text, lower);
        if (symbol is null)
            return Fail("Couldn't find a coin/symbol — try e.g. \"alert when BTC goes above 70000\".");

        // Percent move? (e.g. "10%", "pumps 5 percent")
        var pctMatch = Regex.Match(lower, @"([\d]+(?:\.\d+)?)\s*(?:%|percent|pct)");
        if (pctMatch.Success)
        {
            var pct = decimal.Parse(pctMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var cond = lower.Contains("5m") || lower.Contains("5 min") || lower.Contains("5-min") || lower.Contains("five min")
                        ? AlertCondition.ChangePercent5mAbove
                     : lower.Contains("1h") || lower.Contains("hour") || lower.Contains("60m") || lower.Contains("1 h")
                        ? AlertCondition.ChangePercent1hAbove
                        : AlertCondition.ChangePercent24hAbove;   // default window
            return Ok(symbol, cond, pct);
        }

        // Volume spike?
        if (lower.Contains("volume") && (lower.Contains("spike") || lower.Contains("surge") || Regex.IsMatch(lower, @"[\d.]+\s*[x×]")))
        {
            var mult = Regex.Match(lower, @"([\d]+(?:\.\d+)?)\s*[x×]");
            var m = mult.Success ? decimal.Parse(mult.Groups[1].Value, CultureInfo.InvariantCulture) : 3m;
            return Ok(symbol, AlertCondition.VolumeSpike, m);
        }

        // Absolute price level. Strip timeframe tokens so their digits aren't picked.
        var cleaned = Regex.Replace(lower, @"\b(5m|1h|24h|60m|4h|15m)\b", " ");
        var price = ParseFirstNumber(cleaned);
        if (price is null)
            return Fail($"Found {symbol} but no price/percent — try \"{Short(symbol)} above 70000\" or \"{Short(symbol)} drops 5%\".");

        var below = ContainsAny(lower, "below", "under", "drops", "drop", "falls", "fall", "dips", "dip", "sinks", "less than", "<");
        var above = ContainsAny(lower, "above", "over", "breaks", "break", "hits", "hit", "reaches", "reach", "exceeds", "climbs", "rises", "greater than", ">");
        var condition = below && !above ? AlertCondition.PriceBelow : AlertCondition.PriceAbove;

        return Ok(symbol, condition, price.Value);
    }

    // ── Parsing helpers ─────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> CoinNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bitcoin"] = "BTC", ["btc"] = "BTC",
        ["ethereum"] = "ETH", ["ether"] = "ETH", ["eth"] = "ETH",
        ["solana"] = "SOL", ["sol"] = "SOL",
        ["dogecoin"] = "DOGE", ["doge"] = "DOGE",
        ["ripple"] = "XRP", ["xrp"] = "XRP",
        ["cardano"] = "ADA", ["ada"] = "ADA",
        ["binance"] = "BNB", ["bnb"] = "BNB",
        ["avalanche"] = "AVAX", ["avax"] = "AVAX",
        ["polkadot"] = "DOT", ["dot"] = "DOT",
        ["chainlink"] = "LINK", ["link"] = "LINK",
    };

    private static string? ExtractSymbol(string original, string lower)
    {
        // Known coin names / tickers first.
        foreach (var word in Regex.Split(lower, @"[^a-z]+"))
            if (word.Length >= 2 && CoinNames.TryGetValue(word, out var sym))
                return sym + "USDT";

        // Explicit pair like BTCUSDT / ETH-USDT in the original casing.
        var pair = Regex.Match(original, @"\b([A-Za-z]{2,10})[-/]?(USDT|USD|USDC)\b");
        if (pair.Success)
            return NormalizeSymbol(pair.Groups[1].Value + pair.Groups[2].Value);

        // A standalone uppercase ticker (2–6 letters), avoiding common words.
        var tk = Regex.Match(original, @"\b([A-Z]{2,6})\b");
        if (tk.Success && !StopWords.Contains(tk.Groups[1].Value))
            return tk.Groups[1].Value + "USDT";

        return null;
    }

    private static readonly HashSet<string> StopWords =
        new(StringComparer.Ordinal) { "USD", "USDT", "USDC", "AI", "ME", "IF", "AT", "ON", "TO", "BY" };

    private static string NormalizeSymbol(string s)
    {
        s = s.Trim().ToUpperInvariant().Replace("-", "").Replace("/", "");
        if (s.Length == 0) return s;
        if (s.EndsWith("USDT") || s.EndsWith("USDC")) return s;
        if (s.EndsWith("USD")) return s[..^3] + "USDT";
        return s + "USDT";
    }

    /// <summary>First number in the text, honouring $, thousands commas and k/m suffixes.</summary>
    private static decimal? ParseFirstNumber(string s)
    {
        var m = Regex.Match(s, @"\$?\s*([\d]{1,3}(?:,[\d]{3})+|\d+(?:\.\d+)?)\s*([km])?", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var raw = m.Groups[1].Value.Replace(",", "");
        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var val)) return null;
        var suffix = m.Groups[2].Value.ToLowerInvariant();
        return suffix switch { "k" => val * 1_000m, "m" => val * 1_000_000m, _ => val };
    }

    private static bool ContainsAny(string s, params string[] needles) => needles.Any(s.Contains);

    private static AlertParseResult Ok(string symbol, AlertCondition cond, decimal threshold) =>
        new(true, symbol, cond, threshold, Describe(symbol, cond, threshold), "Parser (offline)", true);

    private static AlertParseResult Fail(string why) =>
        new(false, "", AlertCondition.PriceAbove, 0m, why, "Parser (offline)", true);

    private static string Short(string pair) => pair.EndsWith("USDT") ? pair[..^4] : pair;

    private static string Describe(string symbol, AlertCondition cond, decimal t) => cond switch
    {
        AlertCondition.PriceAbove            => $"Alert when {symbol} price rises above {t:N4}.",
        AlertCondition.PriceBelow            => $"Alert when {symbol} price falls below {t:N4}.",
        AlertCondition.ChangePercent5mAbove  => $"Alert when {symbol} moves more than {t:0.##}% in 5 minutes.",
        AlertCondition.ChangePercent1hAbove  => $"Alert when {symbol} moves more than {t:0.##}% in 1 hour.",
        AlertCondition.ChangePercent24hAbove => $"Alert when {symbol} moves more than {t:0.##}% in 24 hours.",
        AlertCondition.VolumeSpike           => $"Alert when {symbol} volume spikes ×{t:0.#}.",
        _                                    => $"Alert for {symbol}."
    };
}
