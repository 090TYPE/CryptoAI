using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Brains of the global Ctrl+K command bar. Turns a free-text line into one of two
/// intents: <b>navigate</b> to a workspace section, or <b>ask</b> a question (which the
/// host routes to the AI copilot). Navigation is matched deterministically against a
/// destination registry — so the front door is instant, free and works even when no AI
/// provider/quota is available; only genuine questions spend a model call.
///
/// Destination keys are exactly the tokens <c>MainWindowViewModel.SelectMainTab</c>
/// accepts (see NormalizeSectionKey), so the host just forwards the key.
/// </summary>
public sealed class AiCommandPaletteService
{
    public enum Intent { Navigate, Question }

    /// <param name="Intent">What the user meant.</param>
    /// <param name="SectionKey">Target section key for navigation (null for a question).</param>
    /// <param name="SectionLabel">Human label for the destination (for the confirmation line).</param>
    /// <param name="Query">The (trimmed) original text — used as the question.</param>
    public sealed record Result(Intent Intent, string? SectionKey, string? SectionLabel, string Query);

    /// <summary>One navigable destination: the SelectMainTab key, a label, and match keywords.</summary>
    private sealed record Destination(string Key, string Label, string[] Keywords);

    // Verbs that signal "take me there". Matching one lets even an ambiguous keyword
    // (e.g. "market") resolve to navigation rather than a question.
    private static readonly string[] NavVerbs =
    [
        "open", "show", "go to", "goto", "go", "navigate to", "navigate", "switch to",
        "switch", "view", "jump to", "jump", "take me to",
        "открой", "открыть", "покажи", "показать", "перейди", "перейти", "переключи",
        "переключись", "вкладка", "вкладку", "раздел",
    ];

    // Keys mirror NormalizeSectionKey. Keywords are lowercase; include en + ru terms.
    private static readonly Destination[] Destinations =
    [
        new("dashboard",  "Dashboard",        ["dashboard", "home", "overview", "главная", "дашборд", "обзор"]),
        new("markets",    "Markets",          ["markets", "market list", "рынки", "список рынков"]),
        new("trading",    "Trading",          ["trading", "trade", "order ticket", "торговля", "трейдинг", "ордер"]),
        new("portfolio",  "Portfolio",        ["portfolio", "wallet", "balance", "портфель", "кошелек", "кошелёк", "баланс"]),
        new("ai-signals", "AI Signals",       ["ai signals", "signals", "ai bot", "сигналы", "аи сигналы"]),
        new("sniper",     "Sniper",           ["sniper", "snipe", "new coins", "снайпер", "новые монеты"]),
        new("dex",        "DEX",              ["dex", "decentralized", "декс", "децентрализ"]),
        new("positions",  "All Positions",    ["positions", "open positions", "позиции", "открытые позиции"]),
        new("risk",       "Risk",             ["risk", "risk center", "limits", "риск", "лимиты"]),
        new("bots",       "Bots",             ["bots", "rule bot", "bot console", "боты", "бот"]),
        new("rules",      "Composite Bot",    ["rules", "composite", "rule builder", "правила", "конструктор правил"]),
        new("backtest",   "Backtest",         ["backtest", "back test", "бэктест", "бектест"]),
        new("journal",    "Trade Journal",    ["journal", "trade journal", "trade log", "журнал", "журнал сделок"]),
        new("funding",    "Funding Rates",    ["funding", "funding rate", "funding rates", "фандинг"]),
        new("arb",        "Arbitrage",        ["arb", "arbitrage", "cross exchange", "арбитраж"]),
        new("copy",       "Copy Trading",     ["copy", "copy trading", "copy trade", "копитрейдинг", "копирование"]),
        new("statarb",    "Stat Arb",         ["stat arb", "statarb", "pairs", "pairs trading", "статарб", "пары"]),
        new("news",       "News Feed",        ["news", "news feed", "sentiment", "новости", "сентимент"]),
        new("onchain",    "On-Chain",         ["onchain", "on chain", "on-chain", "metrics", "mvrv", "nupl", "ончейн", "метрики"]),
        new("whale",      "Whale Tracker",    ["whale", "whales", "whale tracker", "киты", "кит"]),
        new("liquidation","Liq Heatmap",      ["liquidation", "liquidations", "liq map", "liq heatmap", "ликвидации", "ликвидаций"]),
        new("analytics",  "Analytics",        ["analytics", "execution analytics", "аналитика"]),
        new("gas",        "Gas Monitor",      ["gas", "gas monitor", "fees", "газ", "комиссии"]),
        new("router",     "Best Execution",   ["router", "best execution", "execution router", "роутер", "маршрут"]),
        new("scanner",    "Market Scanner",   ["scanner", "market scanner", "screener", "сканер", "скринер"]),
        new("settings",   "Settings",         ["settings", "preferences", "config", "настройки", "конфиг", "ai provider", "provider", "claude", "chatgpt", "openai", "api key"]),
        new("help",       "Help",             ["help", "guide", "помощь", "гайд", "справка"]),
    ];

    /// <summary>Parses one command-bar line into a navigate-or-ask intent.</summary>
    public Result Parse(string? text)
    {
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0)
            return new Result(Intent.Question, null, null, query);

        var lower = query.ToLowerInvariant();

        // Detect & strip a leading navigation verb.
        var hasVerb = false;
        var stripped = lower;
        foreach (var verb in NavVerbs.OrderByDescending(v => v.Length))
        {
            if (lower == verb || lower.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                hasVerb = true;
                stripped = lower[verb.Length..].Trim();
                break;
            }
        }

        // Best destination match (longest matching keyword wins) over the stripped text,
        // falling back to the full text so an embedded verb still resolves.
        var best = MatchDestination(stripped) ?? MatchDestination(lower);

        // Navigate when the user clearly meant it: an explicit verb (e.g. "open scanner"),
        // or a line that is essentially just the destination name ("scanner", "ai signals").
        // The ≤2-word guard keeps phrasings like "summarize my positions" as questions.
        if (best is not null && (hasVerb || WordCount(stripped) <= 2))
            return new Result(Intent.Navigate, best.Key, best.Label, query);

        return new Result(Intent.Question, null, null, query);
    }

    private static Destination? MatchDestination(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        Destination? best = null;
        var bestLen = 0;
        foreach (var d in Destinations)
        {
            foreach (var kw in d.Keywords)
            {
                if (kw.Length <= bestLen) continue;
                if (text == kw || ContainsWord(text, kw))
                {
                    best = d;
                    bestLen = kw.Length;
                }
            }
        }
        return best;
    }

    // Whole-word/phrase containment so "gas" doesn't match inside "gasoline".
    private static bool ContainsWord(string haystack, string needle)
    {
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            var endIdx = idx + needle.Length;
            var afterOk = endIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[endIdx]);
            if (beforeOk && afterOk) return true;
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static int WordCount(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
