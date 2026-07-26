using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

public enum NewsSentiment { Neutral, Bullish, Bearish }

public sealed record NewsItem
{
    public long     Id           { get; init; }
    public string   Title        { get; init; } = string.Empty;
    public string   Source       { get; init; } = string.Empty;
    public string   Url          { get; init; } = string.Empty;
    public DateTime PublishedAt  { get; init; }
    public NewsSentiment Sentiment { get; init; }
    public IReadOnlyList<string> Currencies { get; init; } = [];
    public bool     IsImportant  { get; init; }
    public int      Votes        { get; init; }
}

/// <summary>
/// Polls multiple crypto RSS feeds (no API key required, always free).
/// Also optionally fetches from CryptoPanic when CRYPTOPANIC_API_KEY is set.
/// Fires <see cref="NewsReceived"/> on thread-pool.
/// </summary>
public sealed class NewsFeedService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string?    _panicToken;
    private CancellationTokenSource? _cts;
    private readonly HashSet<long>   _seenIds = [];

    // One value for the whole app: the server keys its shared cache on `news:{n}`, so a second
    // limit anywhere would fork a second entry and halve the fan-in the endpoint exists for.
    private const int ServerNewsLimit = 100;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(90);
    public bool     IsConfigured => true;
    public string?  LastError    { get; private set; }

    /// <summary>Fired for each new batch (newest first). May fire with empty list on error.</summary>
    public event Action<IReadOnlyList<NewsItem>>? NewsReceived;

    // ── RSS feeds — always free, no key, highly reliable ─────────────────────
    private static readonly (string Name, string Url)[] RssFeeds =
    [
        ("CoinTelegraph",  "https://cointelegraph.com/rss"),
        ("CoinDesk",       "https://www.coindesk.com/arc/outboundfeeds/rss/"),
        ("Decrypt",        "https://decrypt.co/feed"),
        ("The Block",      "https://www.theblock.co/rss.xml"),
        ("Bitcoin Mag",    "https://bitcoinmagazine.com/.rss/full/"),
    ];

    public NewsFeedService()
    {
        _panicToken = Environment.GetEnvironmentVariable("CRYPTOPANIC_API_KEY");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; CryptoAITerminal/1.0)");
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── poll loop ─────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await FetchAndFireAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
            await FetchAndFireAsync(ct);
        }
    }

    private async Task FetchAndFireAsync(CancellationToken ct)
    {
        LastError = null;
        try
        {
            var items = await FetchAllSourcesAsync(ct);
            var fresh = new List<NewsItem>();
            foreach (var item in items)
                if (_seenIds.Add(item.Id))
                    fresh.Add(item);

            while (_seenIds.Count > 5000)
                _seenIds.Remove(_seenIds.First());

            // Always fire — even empty so VM can clear loading state
            NewsReceived?.Invoke(fresh);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            NewsReceived?.Invoke([]);
        }
    }

    // ── aggregator ────────────────────────────────────────────────────────────

    private async Task<List<NewsItem>> FetchAllSourcesAsync(CancellationToken ct)
    {
        // The server already collects these same headlines for every licensed user, so a hundred
        // terminals must not each pull five RSS hosts and CryptoPanic for identical articles.
        if (ServerDataClient.IsBound)
        {
            var served = await ServerDataClient.GetNewsAsync(ServerNewsLimit, ct).ConfigureAwait(false);
            if (served is not null)
            {
                // Empty means a fresh deploy whose news collector has not run yet — showing the RSS
                // feed beats showing an empty panel, so only a non-empty result short-circuits.
                var fromServer = ParseServerNews(served);
                if (fromServer.Count > 0) return fromServer;
            }
        }

        // Fetch all RSS feeds in parallel
        var rssTasks = RssFeeds.Select(feed => FetchRssAsync(feed.Name, feed.Url, ct));
        var rssResults = await Task.WhenAll(rssTasks).ConfigureAwait(false);
        var all = rssResults.SelectMany(x => x).ToList();

        // Optional: CryptoPanic when key is set
        if (!string.IsNullOrWhiteSpace(_panicToken))
        {
            var panicItems = await FetchCryptoPanicAsync(ct).ConfigureAwait(false);
            var existing   = all.Select(i => i.Id).ToHashSet();
            all.AddRange(panicItems.Where(p => !existing.Contains(p.Id)));
        }

        return all.OrderByDescending(i => i.PublishedAt).ToList();
    }

    // ── RSS parser ────────────────────────────────────────────────────────────

    private async Task<List<NewsItem>> FetchRssAsync(
        string sourceName, string url, CancellationToken ct)
    {
        var result = new List<NewsItem>();
        try
        {
            var xml = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";

            var items = doc.Descendants("item");
            foreach (var item in items.Take(30))
            {
                var title   = item.Element("title")?.Value?.Trim() ?? "";
                var link    = item.Element("link")?.Value?.Trim()
                           ?? item.Element("guid")?.Value?.Trim() ?? "";
                var pubDate = item.Element("pubDate")?.Value;
                var desc    = item.Element("description")?.Value ?? "";

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                    continue;

                var publishedAt = DateTime.TryParse(pubDate, out var dt)
                    ? dt.ToUniversalTime()
                    : DateTime.UtcNow;

                // Stable ID from URL hash
                var id = (long)(uint)link.GetHashCode() |
                         ((long)(uint)sourceName.GetHashCode() << 32);

                // Detect currencies from title + description
                var currencies = ExtractCurrencies(title + " " + desc);
                var sentiment  = ClassifySentiment(title, 0, 0);
                var isImportant = BullishKeywords.Concat(BearishKeywords)
                    .Any(kw => title.Contains(kw, StringComparison.OrdinalIgnoreCase));

                result.Add(new NewsItem
                {
                    Id          = id,
                    Title       = title,
                    Source      = sourceName,
                    Url         = link,
                    PublishedAt = publishedAt,
                    Sentiment   = sentiment,
                    Currencies  = currencies,
                    IsImportant = isImportant,
                    Votes       = 0,
                });
            }
        }
        catch { /* this feed failed — skip, others will succeed */ }
        return result;
    }

    // ── CryptoPanic (optional) ────────────────────────────────────────────────

    private async Task<List<NewsItem>> FetchCryptoPanicAsync(CancellationToken ct)
    {
        var result = new List<NewsItem>();
        try
        {
            var url  = $"https://cryptopanic.com/api/v1/posts/?auth_token={_panicToken}&kind=news";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr)) return result;

            foreach (var post in arr.EnumerateArray())
            {
                var id    = post.TryGetProperty("id",    out var idEl)    ? idEl.GetInt64()    : 0;
                var title = post.TryGetProperty("title", out var tEl)     ? tEl.GetString()    ?? "" : "";
                var link  = post.TryGetProperty("url",   out var uEl)     ? uEl.GetString()    ?? "" : "";
                var src   = post.TryGetProperty("source", out var sEl) &&
                            sEl.TryGetProperty("title", out var snEl)     ? snEl.GetString()   ?? "" : "CryptoPanic";

                var pubAt = DateTime.UtcNow;
                if (post.TryGetProperty("published_at", out var pEl))
                    DateTime.TryParse(pEl.GetString(), out pubAt);

                var currencies = new List<string>();
                if (post.TryGetProperty("currencies", out var curArr))
                    foreach (var c in curArr.EnumerateArray())
                        if (c.TryGetProperty("code", out var cEl))
                        {
                            var code = cEl.GetString()?.ToUpperInvariant();
                            if (code is not null) currencies.Add(code);
                        }

                int pos = 0, neg = 0;
                if (post.TryGetProperty("votes", out var votes))
                {
                    pos = votes.TryGetProperty("positive", out var pv) ? pv.GetInt32() : 0;
                    neg = votes.TryGetProperty("negative", out var nv) ? nv.GetInt32() : 0;
                }

                result.Add(new NewsItem
                {
                    Id          = id,
                    Title       = title,
                    Source      = src,
                    Url         = link,
                    PublishedAt = pubAt.ToUniversalTime(),
                    Sentiment   = ClassifySentiment(title, pos, neg),
                    Currencies  = currencies,
                    IsImportant = (pos + neg) >= 10,
                    Votes       = pos - neg,
                });
            }
        }
        catch { /* optional source layered on top of RSS — whatever parsed so far still ships */ }
        return result;
    }

    // ── Server feed parser ────────────────────────────────────────────────────

    /// <summary>
    /// Maps /api/news rows (Source, Title, Url, Summary, PublishedUtc — PascalCase, the endpoint
    /// serializes with default options) onto <see cref="NewsItem"/>. The server stores no sentiment,
    /// votes or id for a row, so those are recomputed with the very expressions the RSS path uses:
    /// a bound terminal must not classify a headline differently from an unbound one.
    /// Returns an empty list on anything unparsable so the caller falls through to RSS.
    /// </summary>
    private static List<NewsItem> ParseServerNews(string json)
    {
        var result = new List<NewsItem>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var title   = row.TryGetProperty("Title",   out var tEl) ? tEl.GetString()?.Trim() ?? "" : "";
                var link    = row.TryGetProperty("Url",     out var uEl) ? uEl.GetString()?.Trim() ?? "" : "";
                var source  = row.TryGetProperty("Source",  out var sEl) ? sEl.GetString()?.Trim() ?? "" : "";
                var summary = row.TryGetProperty("Summary", out var mEl) ? mEl.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                    continue;

                // published_utc is nullable and CryptoPanic rows can land without a date; the RSS
                // path stamps UtcNow in exactly that case. AssumeUniversal is what makes this
                // Kind-proof — the column is TIMESTAMPTZ, so a value that arrives without an
                // offset is still a UTC instant and must not be shifted by the local offset.
                var publishedAt = DateTime.UtcNow;
                if (row.TryGetProperty("PublishedUtc", out var pEl) &&
                    pEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                    DateTime.TryParse(pEl.GetString(),
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.AdjustToUniversal |
                                      System.Globalization.DateTimeStyles.AssumeUniversal,
                                      out var parsed))
                    publishedAt = parsed;

                // Same formula as the RSS path, so _seenIds keeps de-duplicating batches.
                var id = (long)(uint)link.GetHashCode() |
                         ((long)(uint)source.GetHashCode() << 32);

                // Summary stands in for the RSS description — dropping it would silently empty the
                // symbol filter for every server-sourced item.
                var currencies  = ExtractCurrencies(title + " " + summary);
                var isImportant = BullishKeywords.Concat(BearishKeywords)
                    .Any(kw => title.Contains(kw, StringComparison.OrdinalIgnoreCase));

                result.Add(new NewsItem
                {
                    Id          = id,
                    Title       = title,
                    Source      = source,
                    Url         = link,
                    PublishedAt = publishedAt,
                    Sentiment   = ClassifySentiment(title, 0, 0),
                    Currencies  = currencies,
                    IsImportant = isImportant,
                    Votes       = 0,
                });
            }
        }
        // A malformed body must not escalate into FetchAndFireAsync's catch, which would blank the
        // feed and set LastError; an empty list sends the caller to the RSS path instead.
        catch { return []; }

        // Deliberately unsorted: the endpoint already orders by COALESCE(published_utc, fetched_utc)
        // DESC, and re-sorting on PublishedAt would float the UtcNow-stamped rows to the top.
        return result;
    }

    // ── Currency extraction from text ─────────────────────────────────────────

    private static readonly string[] WellKnownCoins =
    [
        "BTC","ETH","BNB","SOL","XRP","ADA","DOGE","AVAX","DOT","MATIC",
        "LINK","UNI","AAVE","LTC","BCH","ATOM","NEAR","FTM","OP","ARB",
        "SUI","APT","INJ","TIA","WIF","PEPE","SHIB","TON","HBAR","VET"
    ];

    private static IReadOnlyList<string> ExtractCurrencies(string text)
    {
        var found = new List<string>();
        foreach (var coin in WellKnownCoins)
        {
            // Match whole word: " BTC ", "(BTC)", "/BTC", etc.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    text, $@"\b{coin}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                found.Add(coin);
        }
        return found;
    }

    // ── Sentiment classifier ──────────────────────────────────────────────────

    internal static NewsSentiment ClassifySentiment(
        string title, int positiveVotes, int negativeVotes)
    {
        if (positiveVotes > negativeVotes * 2 && positiveVotes >= 3) return NewsSentiment.Bullish;
        if (negativeVotes > positiveVotes * 2 && negativeVotes >= 3) return NewsSentiment.Bearish;

        foreach (var kw in BullishKeywords)
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase)) return NewsSentiment.Bullish;
        foreach (var kw in BearishKeywords)
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase)) return NewsSentiment.Bearish;

        return NewsSentiment.Neutral;
    }

    private static readonly string[] BullishKeywords =
    [
        "surge", "rally", "breakout", "all-time high", "ath", "bull", "bullish",
        "adoption", "approval", "launch", "partnership", "upgrade", "listing",
        "recovery", "record", "milestone", "etf", "institutional", "soars",
        "jumps", "rises", "gains", "moon", "pump", "buy", "accumulate"
    ];

    private static readonly string[] BearishKeywords =
    [
        "crash", "dump", "plunge", "drop", "bear", "bearish", "ban", "hack",
        "exploit", "stolen", "lawsuit", "regulation", "crackdown", "scam", "rug",
        "collapse", "liquidat", "fall", "concern", "risk", "warning", "sell-off",
        "tumbles", "slumps", "sinks", "loses", "declines", "panic", "fear"
    ];

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
