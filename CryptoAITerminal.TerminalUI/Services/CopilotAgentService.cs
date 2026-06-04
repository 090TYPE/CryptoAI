using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine.Agent;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// In-app conversational AI copilot. The user asks questions in natural language
/// ("what's my biggest risk?", "explain my BTC position", "how is the market?")
/// and Claude answers by inspecting the account and market through a set of
/// <b>strictly read-only</b> tools — it can never place, modify or cancel a trade.
///
/// This mirrors <see cref="AiTraderAgentService"/> (host builds tools, the engine's
/// <see cref="ClaudeAgentRunner"/> only relays) but for an advisory loop. As with
/// every other AI feature, when no API key is configured it degrades to a
/// deterministic offline assistant that answers the common intents directly from
/// the account snapshot, so the copilot still works in the keyless demo.
///
/// The service is decoupled from the (huge) view-model graph via
/// <see cref="CopilotDataSource"/> — a record of optional async delegates the host
/// wires from whatever services it already has. Any unset delegate simply makes the
/// matching tool report "not available", so the copilot degrades gracefully.
/// </summary>
public sealed class CopilotAgentService
{
    /// <summary>One open position, as the copilot sees it.</summary>
    public sealed record PositionLine(
        string Symbol, decimal Qty, decimal AvgEntry, decimal Mark, decimal UnrealizedPnl);

    /// <summary>A point-in-time view of the account the copilot reasons over.</summary>
    /// <param name="Mode">"paper" or "live" — surfaced so the model frames advice correctly.</param>
    public sealed record AccountSnapshot(
        string Mode, decimal CashUsd, decimal RealizedPnlUsd, IReadOnlyList<PositionLine> Positions);

    /// <summary>
    /// Read-only data the copilot can pull. Every delegate is optional; a null one
    /// turns the corresponding tool into a "not available here" reply rather than an
    /// error, so the host can wire only what it has.
    /// </summary>
    public sealed record CopilotDataSource(
        Func<CancellationToken, Task<AccountSnapshot>>? Account = null,
        Func<string, CancellationToken, Task<decimal>>? Price = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? TopOpportunities = null,
        Func<CancellationToken, Task<string>>? MarketPulse = null);

    /// <summary>The copilot's answer to one question.</summary>
    /// <param name="Source">"Claude {model}" or "Offline assistant".</param>
    public sealed record CopilotAnswer(string Text, string Source, bool IsOffline, int ToolCalls);

    private readonly CopilotDataSource _data;
    private readonly HttpClient? _httpForTests;

    public CopilotAgentService(CopilotDataSource? data = null, HttpClient? httpForTests = null)
    {
        _data = data ?? new CopilotDataSource();
        _httpForTests = httpForTests;
    }

    private string? _apiKey;
    /// <summary>Key for the active vendor; falls back to <see cref="AiRuntime.ActiveApiKey"/> when not set explicitly (tests set it).</summary>
    public string ApiKey { get => _apiKey ?? CryptoAITerminal.AIEngine.AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    /// <summary>Model for the active vendor; falls back to <see cref="AiRuntime.ActiveModel"/>.</summary>
    public string Model { get => _model ?? CryptoAITerminal.AIEngine.AiRuntime.ActiveModel; set => _model = value; }

    /// <summary>True when a key is set — the agentic path is used; otherwise the offline assistant.</summary>
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Streams every agent step (thinking, tool call, result) for a live UI log.</summary>
    public event Action<AgentEvent>? OnEvent;

    /// <summary>
    /// Answer one user question. Routes to Claude (tool-use loop) when a key is
    /// configured, else to the deterministic offline assistant. Never throws on a
    /// model/network error — it degrades to the offline answer instead.
    /// </summary>
    public async Task<CopilotAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        question = (question ?? string.Empty).Trim();
        if (question.Length == 0)
            return new CopilotAnswer("Ask me about your balance, positions, P&L, risk or the market.",
                "Offline assistant", true, 0);

        if (UsesLiveModel)
        {
            try
            {
                var runner = AgentRunnerFactory.Create(ApiKey, Model, maxIterations: 6, http: _httpForTests);
                var result = await runner.RunAsync(SystemPrompt(), question, BuildTools(), OnEvent, ct)
                    .ConfigureAwait(false);

                if (result.StoppedReason is not ("error" or "cancelled") &&
                    !string.IsNullOrWhiteSpace(result.FinalText))
                    return new CopilotAnswer(result.FinalText, CryptoAITerminal.AIEngine.AiRuntime.ActiveSourceLabel, false, result.ToolCallCount);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        var offline = await AnswerOfflineAsync(question, ct).ConfigureAwait(false);
        return new CopilotAnswer(offline, "Offline assistant", true, 0);
    }

    // ── Prompt ─────────────────────────────────────────────────────────────────

    private static string SystemPrompt() =>
        "You are the built-in AI copilot for CryptoAI Terminal, a crypto trading desktop app. " +
        "You answer the user's questions about THEIR account, positions, P&L, risk and the market, " +
        "using the read-only tools to fetch real numbers. " +
        "You are STRICTLY ADVISORY: you cannot place, change or cancel any orders, and you must never " +
        "claim to have done so — if asked to trade, explain that the user must act in the app themselves. " +
        "Never invent figures; call a tool to get them, and say so plainly if the data isn't available. " +
        "Be concise and specific (a few sentences or a short list). When you lean toward a trade idea, add a " +
        "one-line risk caveat. This is not financial advice.";

    // ── Read-only tools ──────────────────────────────────────────────────────────

    private IReadOnlyList<AgentTool> BuildTools() =>
    [
        new AgentTool(
            "get_account",
            "Get the user's account snapshot: mode (paper/live), cash balance, realized P&L and every open " +
            "position with quantity, average entry, mark price and unrealized P&L.",
            new { type = "object", properties = new { } },
            GetAccountTool),

        new AgentTool(
            "get_price",
            "Get the current mid price for a symbol (e.g. BTCUSDT).",
            new { type = "object", properties = new { symbol = new { type = "string", description = "e.g. ETHUSDT" } }, required = new[] { "symbol" } },
            GetPriceTool),

        new AgentTool(
            "get_top_opportunities",
            "List the market scanner's current top-ranked opportunities (symbol + a short reason), if available.",
            new { type = "object", properties = new { } },
            GetOpportunitiesTool),

        new AgentTool(
            "get_market_pulse",
            "Get the aggregated market pulse — news/sentiment bias and a one-line summary, if available.",
            new { type = "object", properties = new { } },
            GetMarketPulseTool),
    ];

    private async Task<string> GetAccountTool(JsonElement input, CancellationToken ct)
    {
        if (_data.Account is null) return NotAvailable("account data");
        try
        {
            var a = await _data.Account(ct).ConfigureAwait(false);
            return Json(new
            {
                mode = a.Mode,
                cash_usd = Round(a.CashUsd),
                realized_pnl_usd = Round(a.RealizedPnlUsd),
                open_positions = a.Positions.Count,
                total_exposure_usd = Round(Exposure(a)),
                unrealized_pnl_usd = Round(a.Positions.Sum(p => p.UnrealizedPnl)),
                positions = a.Positions.Select(p => new
                {
                    symbol = p.Symbol,
                    qty = Round(p.Qty),
                    avg_entry = Round(p.AvgEntry),
                    mark = Round(p.Mark),
                    unrealized_pnl = Round(p.UnrealizedPnl),
                    notional_usd = Round(Math.Abs(p.Qty) * p.Mark)
                })
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> GetPriceTool(JsonElement input, CancellationToken ct)
    {
        if (_data.Price is null) return NotAvailable("live prices");
        var symbol = Str(input, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return Err("symbol required");
        try
        {
            var mid = await _data.Price(symbol, ct).ConfigureAwait(false);
            if (mid <= 0) return Err($"no price for {symbol}");
            return Json(new { symbol, mid = Round(mid) });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> GetOpportunitiesTool(JsonElement input, CancellationToken ct)
    {
        if (_data.TopOpportunities is null) return NotAvailable("the market scanner");
        try
        {
            var rows = await _data.TopOpportunities(ct).ConfigureAwait(false);
            return Json(new { opportunities = rows });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> GetMarketPulseTool(JsonElement input, CancellationToken ct)
    {
        if (_data.MarketPulse is null) return NotAvailable("market pulse");
        try
        {
            var pulse = await _data.MarketPulse(ct).ConfigureAwait(false);
            return Json(new { pulse });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ── Offline assistant ────────────────────────────────────────────────────────

    /// <summary>
    /// Keyless fallback: matches the question against a few common intents and answers
    /// directly from the account snapshot. Deterministic, so the demo flow always gets a
    /// useful reply — and so it's unit-testable without a network call.
    /// </summary>
    private async Task<string> AnswerOfflineAsync(string question, CancellationToken ct)
    {
        var q = question.ToLowerInvariant();

        if (Mentions(q, "help", "what can you", "что ты умеешь", "что умеешь", "команд"))
            return
                "I'm your read-only AI copilot. Without an API key I answer from your account snapshot. Try:\n" +
                "• \"balance\" — cash and realized P&L\n" +
                "• \"positions\" — open positions and unrealized P&L\n" +
                "• \"pnl\" — total profit/loss\n" +
                "• \"risk\" — exposure and concentration\n" +
                "Add a Claude API key in the AI Bot panel for full natural-language answers.";

        if (_data.Account is null)
            return "I can't reach your account data right now. Open the trading desk and try again, " +
                   "or add a Claude API key for full answers.";

        AccountSnapshot a;
        try { a = await _data.Account(ct).ConfigureAwait(false); }
        catch (Exception ex) { return $"Couldn't read the account: {ex.Message}"; }

        if (Mentions(q, "risk", "exposure", "риск", "expos"))
            return DescribeRisk(a);
        if (Mentions(q, "pnl", "profit", "loss", "p&l", "прибыл", "убыт", "доход"))
            return DescribePnl(a);
        if (Mentions(q, "position", "holding", "позиц", "держ", "portfolio", "портфел"))
            return DescribePositions(a);
        if (Mentions(q, "balance", "cash", "funds", "баланс", "налич", "деньг", "средств"))
            return DescribeBalance(a);

        // Default: a compact account overview plus a nudge to enable the live model.
        return DescribeOverview(a) +
               "\n\n(Offline assistant — add a Claude API key in the AI Bot panel for full answers.)";
    }

    private static string DescribeBalance(AccountSnapshot a)
    {
        var cash = a.CashUsd > 0
            ? $"Cash: {Usd(a.CashUsd)}. "
            : "Cash balance isn't tracked here — see the Wallet tab. ";
        return $"Mode: {a.Mode}. {cash}Realized P&L: {Usd(a.RealizedPnlUsd)}. " +
               $"Open positions: {a.Positions.Count} ({Usd(Exposure(a))} notional).";
    }

    private static string DescribePnl(AccountSnapshot a)
    {
        var upnl = a.Positions.Sum(p => p.UnrealizedPnl);
        var total = a.RealizedPnlUsd + upnl;
        var sb = new StringBuilder();
        sb.Append($"Total P&L: {Usd(total)}  (realized {Usd(a.RealizedPnlUsd)}, unrealized {Usd(upnl)}).");
        if (a.Positions.Count > 0)
        {
            var best = a.Positions.OrderByDescending(p => p.UnrealizedPnl).First();
            var worst = a.Positions.OrderBy(p => p.UnrealizedPnl).First();
            sb.Append($" Best: {best.Symbol} {Usd(best.UnrealizedPnl)}. Worst: {worst.Symbol} {Usd(worst.UnrealizedPnl)}.");
        }
        return sb.ToString();
    }

    private static string DescribePositions(AccountSnapshot a)
    {
        if (a.Positions.Count == 0) return "You have no open positions. Cash: " + Usd(a.CashUsd) + ".";
        var lines = a.Positions
            .OrderByDescending(p => Math.Abs(p.Qty) * p.Mark)
            .Select(p =>
            {
                var dir = p.Qty >= 0 ? "long" : "short";
                return $"• {p.Symbol}: {dir} {Math.Abs(p.Qty):0.######} @ {p.AvgEntry:0.####} " +
                       $"(mark {p.Mark:0.####}, uPnL {Usd(p.UnrealizedPnl)}, {Usd(Math.Abs(p.Qty) * p.Mark)} notional)";
            });
        return $"{a.Positions.Count} open position(s):\n" + string.Join("\n", lines);
    }

    private static string DescribeRisk(AccountSnapshot a)
    {
        var exposure = Exposure(a);
        var equity = a.CashUsd + exposure;
        if (a.Positions.Count == 0)
            return $"No open positions, so directional risk is zero. Cash: {Usd(a.CashUsd)}.";

        var largest = a.Positions.OrderByDescending(p => Math.Abs(p.Qty) * p.Mark).First();
        var largestNotional = Math.Abs(largest.Qty) * largest.Mark;
        var concentration = exposure > 0 ? largestNotional / exposure : 0m;
        var deployed = equity > 0 ? exposure / equity : 0m;

        var sb = new StringBuilder();
        sb.Append($"Exposure: {Usd(exposure)} across {a.Positions.Count} position(s)");
        // Only frame exposure against equity when we actually know the cash balance.
        if (a.CashUsd > 0) sb.Append($" — {deployed:P0} of equity deployed");
        sb.Append($". Most concentrated: {largest.Symbol} at {concentration:P0} of exposure.");
        if (concentration >= 0.5m && a.Positions.Count > 1)
            sb.Append(" ⚠ Over half your risk is in one name — consider trimming for diversification.");
        else if (a.CashUsd > 0 && deployed >= 0.9m)
            sb.Append(" ⚠ Almost fully deployed — little cash buffer left.");
        sb.Append(" Not financial advice.");
        return sb.ToString();
    }

    private static string DescribeOverview(AccountSnapshot a)
    {
        var upnl = a.Positions.Sum(p => p.UnrealizedPnl);
        return $"Account ({a.Mode}): cash {Usd(a.CashUsd)}, {a.Positions.Count} position(s), " +
               $"exposure {Usd(Exposure(a))}, unrealized P&L {Usd(upnl)}, realized {Usd(a.RealizedPnlUsd)}.";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static decimal Exposure(AccountSnapshot a) => a.Positions.Sum(p => Math.Abs(p.Qty) * p.Mark);

    private static bool Mentions(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static string Usd(decimal v) => "$" + v.ToString("N2", CultureInfo.InvariantCulture);

    private static string NotAvailable(string what) => Json(new { error = $"{what} is not available in this context" });

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static decimal Round(decimal v) => Math.Round(v, 8, MidpointRounding.AwayFromZero);
    private static string Json(object o) => JsonSerializer.Serialize(o);
    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
