using System.Xml.Linq;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Aggregates crypto news from the same RSS feeds the desktop app reads, de-duped by URL.
/// Free — no key. (CryptoPanic keyed feed is added as a separate collector.)
/// </summary>
public sealed class NewsCollector : IDataCollector
{
    private static readonly (string Source, string Url)[] Feeds =
    {
        ("CoinTelegraph",    "https://cointelegraph.com/rss"),
        ("CoinDesk",         "https://www.coindesk.com/arc/outboundfeeds/rss/"),
        ("Decrypt",          "https://decrypt.co/feed"),
        ("TheBlock",         "https://www.theblock.co/rss.xml"),
        ("BitcoinMagazine",  "https://bitcoinmagazine.com/.rss/full/"),
    };

    private readonly HttpClient _http;
    private readonly NewsRepository _news;

    public NewsCollector(NewsRepository news)
    {
        _news = news;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
    }

    public string Name => "news";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var inserted = 0;
        foreach (var feed in Feeds)
        {
            try
            {
                var xml = await _http.GetStringAsync(feed.Url, ct);
                var doc = XDocument.Parse(xml);

                foreach (var item in doc.Descendants("item"))
                {
                    var title = (string?)item.Element("title");
                    var link = (string?)item.Element("link");
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

                    var summary = (string?)item.Element("description");
                    DateTime? published = DateTimeOffset.TryParse((string?)item.Element("pubDate"), out var dto)
                        ? dto.UtcDateTime
                        : null;

                    inserted += await _news.InsertIfNewAsync(feed.Source, title.Trim(), link.Trim(),
                        Trim(summary), published, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* one bad feed shouldn't sink the rest */ }
        }
        return inserted;
    }

    private static string? Trim(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length > 1000 ? s[..1000] : s);
}
