using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

// в”Ђв”Ђ Input context (real, live market data supplied by the caller) в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

/// <summary>One live market row the signal engine reasons over. Prices are real вЂ”
/// the model is told to use them and never invent numbers.</summary>
public sealed record AiSignalMarketRow(
    string Sym,        // base asset, e.g. "BTC"
    string Display,    // e.g. "BTC/USDT"
    string Exch,       // e.g. "Binance"
    decimal Price,
    decimal ChangePct,
    decimal SpreadPct,
    decimal Activity,
    string LogoText,
    string LogoBg);

/// <summary>Everything the provider needs: the live market rows plus pre-formatted
/// DEX-trending lines. Owned/produced by the host (which has the live data).</summary>
public sealed record AiSignalDeskContext(
    IReadOnlyList<AiSignalMarketRow> Markets,
    IReadOnlyList<string> DexTrending);

// в”Ђв”Ђ Output DTOs (AI narrative + labels; prices come from context) в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

public sealed record AiDeskRegime(string Label, string Tone, string Summary, string Meta);
public sealed record AiDeskNewsBullet(string Text, string Tone);
public sealed record AiDeskNews(string Bias, string Summary, IReadOnlyList<AiDeskNewsBullet> Bullets);
public sealed record AiDeskSignal(string Sym, string Dir, double Conf, string Tf, string Exch, string Reason);
public sealed record AiDeskOpportunity(string Sym, string Exch, int Score, string Bias, string Reason);
public sealed record AiDeskInsight(string Label, string Signal, string Tone, string Summary);
public sealed record AiDeskCoach(
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Leaks,
    IReadOnlyList<string> Suggestions);

public sealed record AiSignalDeskResult(
    AiDeskRegime Regime,
    AiDeskNews News,
    IReadOnlyList<AiDeskSignal> Signals,
    IReadOnlyList<AiDeskOpportunity> Opportunities,
    IReadOnlyList<AiDeskInsight> Insights,
    AiDeskCoach Coach,
    string Source);

/// <summary>
/// Turns real live market data into a full AI-generated signal desk: market
/// regime, news digest, per-symbol trade signals, opportunities, insights and a
/// journal-coach read. Mirrors <see cref="MarketInsightAiProvider"/>: one prompt,
/// strict JSON out, prices always taken from the supplied context (never the
/// model). The wire call goes through <see cref="ChatClient"/> so the Claude/
/// ChatGPT toggle applies. Returns <c>null</c> on any empty/invalid response so
/// the caller falls back to its deterministic demo desk.
/// </summary>
public sealed class AiSignalDeskProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public AiSignalDeskProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        // Only fatal when unbound — a bound terminal has no local key and the server holds it.
        if (!ChatClient.CanCallModel(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http = http;
    }

    public async Task<AiSignalDeskResult?> GenerateAsync(AiSignalDeskContext ctx, CancellationToken ct = default)
    {
        if (ctx?.Markets is null || ctx.Markets.Count == 0) return null;

        var system =
            "You are a professional crypto trading signal engine. You are given a live snapshot of " +
            "real market data (prices are REAL вЂ” never invent or change prices). For EACH listed symbol " +
            "produce a trade signal with a direction, a 0..1 confidence and a one-sentence technical reason. " +
            "Also produce an overall market regime, a short news/market digest, a few ranked opportunities, " +
            "a few market-intelligence insights, and a brief trading-journal coach read. " +
            "Reply ONLY with a single compact JSON object вЂ” no prose, no markdown fences. Schema: " +
            "{\"regime\":{\"label\":string,\"tone\":\"bull\"|\"bear\"|\"neutral\",\"summary\":string,\"meta\":string}," +
            "\"news\":{\"bias\":\"BULLISH\"|\"BEARISH\"|\"NEUTRAL\",\"summary\":string,\"bullets\":[{\"text\":string,\"tone\":\"bull\"|\"bear\"|\"warn\"}]}," +
            "\"signals\":[{\"sym\":string,\"dir\":\"BUY\"|\"SELL\"|\"HOLD\",\"conf\":number,\"tf\":string,\"exch\":string,\"reason\":string}]," +
            "\"opportunities\":[{\"sym\":string,\"exch\":string,\"score\":number,\"bias\":\"LONG\"|\"SHORT\",\"reason\":string}]," +
            "\"insights\":[{\"label\":string,\"signal\":string,\"tone\":\"bull\"|\"bear\"|\"warn\",\"summary\":string}]," +
            "\"coach\":{\"summary\":string,\"strengths\":[string],\"leaks\":[string],\"suggestions\":[string]}}. " +
            "Use the exact symbol tokens from the data for \"sym\". Keep every string terse.";

        // 3200, а не 2000: схема требует сигнал на каждый из 14 символов плюс режим, дайджест,
        // возможности, инсайты и разбор журнала. На 2000 ответ обрывался на середине строки, JSON не
        // закрывался, и разбор выбрасывал целиком всё — включая десяток сигналов, которые уже
        // пришли и за которые уже заплачено. Видно это было только как «ответ не удалось
        // использовать», то есть неотличимо от плохой модели.
        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 3200, temperature: 0.4,
            system: system,
            userContent: BuildUserContent(ctx), AiFeatureIds.SignalDesk,
            _http, ct).ConfigureAwait(false);

        // Что действительно отработало, а не что попросил терминал. После того как модель стал
        // выбирать сервер, это разные вещи: подпись «ChatGPT gpt-4o» держалась на локальной
        // настройке клиента, пока ответ на самом деле приходил от той модели, которую сервер подобрал
        // сам. Такая подпись не просто неточна — она уводит диагностику не туда.
        var served = ChatClient.LastServerModel(AiFeatureIds.SignalDesk);
        return Parse(text, served ?? $"{AiRuntime.VendorLabel} {_model}");
    }

    private static string BuildUserContent(AiSignalDeskContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LIVE MARKETS (symbol | last | 24h% | spread% | activity | exchange):");
        foreach (var m in ctx.Markets.Take(14))
            sb.AppendLine(
                $"{m.Sym} | {m.Price} | {m.ChangePct:+0.##;-0.##;0}% | {m.SpreadPct:0.###}% | {m.Activity:0.#} | {m.Exch}");

        if (ctx.DexTrending is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("DEX TRENDING:");
            foreach (var d in ctx.DexTrending.Take(8))
                sb.AppendLine(d);
        }

        sb.AppendLine();
        sb.AppendLine("Return the JSON for every listed market symbol.");
        return sb.ToString();
    }

    // в”Ђв”Ђ Parsing (public so it can be unit-tested without the wire call) в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    public static AiSignalDeskResult? Parse(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var signals = ParseSignals(root);
            if (signals.Count == 0) return null; // nothing usable в†’ let caller fall back

            return new AiSignalDeskResult(
                ParseRegime(root),
                ParseNews(root),
                signals,
                ParseOpportunities(root),
                ParseInsights(root),
                ParseCoach(root),
                source);
        }
        catch (JsonException)
        {
            return Salvage(text, source);
        }
    }

    /// <summary>
    /// Спасает сигналы из оборванного ответа.
    ///
    /// Ответ, упёршийся в лимит длины, оплачен целиком, и в нём обычно уже лежит десяток готовых
    /// сигналов — а выбрасывался он весь, потому что последняя строка не закрыта. Пользователь при
    /// этом видел «ответ не удалось использовать», то есть неотличимо от неудачного вызова.
    ///
    /// Собирается только то, что заведомо целое: объекты внутри "signals", закрытые своей скобкой.
    /// Остальные разделы берутся по умолчанию, а источник помечается, чтобы частичный ответ не
    /// выдавался за полный.
    /// </summary>
    private static AiSignalDeskResult? Salvage(string text, string source)
    {
        var objects = CompleteObjectsInSignals(text);
        if (objects.Count == 0) return null;

        try
        {
            var rebuilt = "{\"signals\":[" + string.Join(",", objects) + "]}";
            using var doc = JsonDocument.Parse(rebuilt);
            var signals = ParseSignals(doc.RootElement);
            if (signals.Count == 0) return null;

            return new AiSignalDeskResult(
                new AiDeskRegime("NEUTRAL", "neutral", "", ""),
                new AiDeskNews("NEUTRAL", "", []),
                signals,
                [], [],
                new AiDeskCoach("", [], [], []),
                source + " · partial");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Целые объекты внутри массива "signals". Скобки считаются вручную, потому что документ
    /// разобрать нельзя — он и оборвался. Кавычки и экранирование учитываются: скобка внутри
    /// текста причины иначе сдвинула бы глубину и склеила два объекта в один.
    /// </summary>
    private static List<string> CompleteObjectsInSignals(string text)
    {
        var found = new List<string>();

        var key = text.IndexOf("\"signals\"", StringComparison.Ordinal);
        if (key < 0) return found;
        var open = text.IndexOf('[', key);
        if (open < 0) return found;

        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        found.Add(text[start..(i + 1)]);
                        start = -1;
                    }
                    break;
                case ']':
                    // Массив закончился — дальше идут другие разделы, их объекты сюда не относятся.
                    if (depth == 0) return found;
                    break;
            }
        }

        return found;
    }

    private static AiDeskRegime ParseRegime(JsonElement root)
    {
        if (!root.TryGetProperty("regime", out var r) || r.ValueKind != JsonValueKind.Object)
            return new AiDeskRegime("NEUTRAL", "neutral", "", "");
        return new AiDeskRegime(
            Str(r, "label", "NEUTRAL"),
            Tone(Str(r, "tone", "neutral")),
            Str(r, "summary", ""),
            Str(r, "meta", ""));
    }

    private static AiDeskNews ParseNews(JsonElement root)
    {
        if (!root.TryGetProperty("news", out var n) || n.ValueKind != JsonValueKind.Object)
            return new AiDeskNews("NEUTRAL", "", []);
        var bullets = new List<AiDeskNewsBullet>();
        if (n.TryGetProperty("bullets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var t = e.ValueKind == JsonValueKind.Object ? Str(e, "text", "") : (e.GetString() ?? "");
                if (string.IsNullOrWhiteSpace(t)) continue;
                var tone = e.ValueKind == JsonValueKind.Object ? Str(e, "tone", "bull") : "bull";
                bullets.Add(new AiDeskNewsBullet(t.Trim(), NewsTone(tone)));
            }
        return new AiDeskNews(Str(n, "bias", "NEUTRAL").ToUpperInvariant(), Str(n, "summary", ""), bullets);
    }

    private static List<AiDeskSignal> ParseSignals(JsonElement root)
    {
        var list = new List<AiDeskSignal>();
        if (!root.TryGetProperty("signals", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            var sym = Str(e, "sym", "");
            if (string.IsNullOrWhiteSpace(sym)) continue;
            list.Add(new AiDeskSignal(
                sym.Trim().ToUpperInvariant(),
                Dir(Str(e, "dir", "HOLD")),
                Clamp01(Num(e, "conf", 0.6)),
                NonEmpty(Str(e, "tf", "1h"), "1h"),
                NonEmpty(Str(e, "exch", ""), ""),
                Str(e, "reason", "")));
        }
        return list;
    }

    private static List<AiDeskOpportunity> ParseOpportunities(JsonElement root)
    {
        var list = new List<AiDeskOpportunity>();
        if (!root.TryGetProperty("opportunities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            var sym = Str(e, "sym", "");
            if (string.IsNullOrWhiteSpace(sym)) continue;
            list.Add(new AiDeskOpportunity(
                sym.Trim(),
                Str(e, "exch", ""),
                (int)Math.Clamp(Num(e, "score", 60), 0, 100),
                Str(e, "bias", "LONG").Trim().ToUpperInvariant() == "SHORT" ? "SHORT" : "LONG",
                Str(e, "reason", "")));
        }
        return list;
    }

    private static List<AiDeskInsight> ParseInsights(JsonElement root)
    {
        var list = new List<AiDeskInsight>();
        if (!root.TryGetProperty("insights", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            var label = Str(e, "label", "");
            if (string.IsNullOrWhiteSpace(label)) continue;
            list.Add(new AiDeskInsight(
                label.Trim().ToUpperInvariant(),
                Str(e, "signal", "NEUTRAL").Trim().ToUpperInvariant(),
                Tone(Str(e, "tone", "warn"), allowWarn: true),
                Str(e, "summary", "")));
        }
        return list;
    }

    private static AiDeskCoach ParseCoach(JsonElement root)
    {
        if (!root.TryGetProperty("coach", out var c) || c.ValueKind != JsonValueKind.Object)
            return new AiDeskCoach("", [], [], []);
        return new AiDeskCoach(
            Str(c, "summary", ""),
            StrList(c, "strengths"),
            StrList(c, "leaks"),
            StrList(c, "suggestions"));
    }

    // в”Ђв”Ђ Small helpers в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    private static string Str(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    private static double Num(JsonElement e, string name, double fallback)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return fallback;
    }

    private static List<string> StrList(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
        return list;
    }

    private static double Clamp01(double v) => Math.Clamp(v > 1 ? v / 100.0 : v, 0.0, 1.0);
    private static string NonEmpty(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

    private static string Dir(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "BUY" or "LONG" => "BUY",
        "SELL" or "SHORT" => "SELL",
        _ => "HOLD",
    };

    private static string Tone(string raw, bool allowWarn = false)
    {
        var t = raw.Trim().ToLowerInvariant();
        if (t is "bull" or "bullish" or "up") return "bull";
        if (t is "bear" or "bearish" or "down") return "bear";
        if (allowWarn && t is "warn" or "warning" or "caution") return "warn";
        return allowWarn ? "warn" : "neutral";
    }

    private static string NewsTone(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        return t is "bear" or "bearish" ? "bear" : t is "warn" or "warning" or "caution" ? "warn" : "bull";
    }
}
