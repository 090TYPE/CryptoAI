using System.Collections.Concurrent;
using System.Net;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// The data counterpart of <see cref="ChatClient"/>'s server routing, and it lives beside it on
/// purpose: <see cref="ChatClient.ServerBaseUrl"/> and <see cref="ChatClient.LicenseTokenProvider"/>
/// are the single source of truth for "is this terminal bound to a CryptoAI server", and having a
/// second copy of that state is how the two halves drift apart.
///
/// Why this exists: several client services fetch data the server already collects and caches for
/// everyone — news, sentiment, gas, on-chain metrics, liquidations, whales, DEX token analytics.
/// A hundred terminals each polling CryptoPanic, the explorers and Coinglass directly means a
/// hundred times the upstream quota for identical data, and it forces the user to supply keys for
/// providers we already pay for. When bound to a server those reads belong to the server.
///
/// Contract, deliberately the same as <see cref="ChatClient"/>'s: **returns null when not bound to
/// a server**, so every caller keeps its existing direct-fetch path as the offline fallback and
/// nothing breaks for an unbound terminal.
///
/// Returns raw JSON rather than typed models so each calling service keeps deserializing into its
/// own shape — wiring a service up stays a small local change instead of a refactor.
///
/// The per-path ETag store is what makes the server's 304 worth anything: without a client-side
/// copy of the last body there is nothing for "not modified" to refer to.
/// </summary>
public static class ServerDataClient
{
    private sealed record Cached(string ETag, string Body);

    private static readonly ConcurrentDictionary<string, Cached> Store = new(StringComparer.Ordinal);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static long _requests;
    private static long _notModified;
    private static long _failures;

    /// <summary>True when the terminal is bound to a server and these reads should be used.</summary>
    public static bool IsBound => !string.IsNullOrWhiteSpace(ChatClient.ServerBaseUrl);

    /// <summary>Requests issued to the server (a 304 still counts — it was still a round trip).</summary>
    public static long Requests => Interlocked.Read(ref _requests);

    /// <summary>Answered 304, i.e. served from the local copy with no body transferred.</summary>
    public static long NotModified => Interlocked.Read(ref _notModified);

    /// <summary>Requests that failed and fell back to the caller's direct path.</summary>
    public static long Failures => Interlocked.Read(ref _failures);

    /// <summary>Drop the cached bodies (e.g. after switching servers or licences).</summary>
    public static void Reset() => Store.Clear();

    // ── Shared reads (identical for every user; the server caches them once) ──────
    public static Task<string?> GetNewsAsync(int limit = 50, CancellationToken ct = default)
        => GetAsync($"/api/news?limit={limit}", ct);

    public static Task<string?> GetSentimentAsync(CancellationToken ct = default)
        => GetAsync("/api/sentiment", ct);

    public static Task<string?> GetGasAsync(CancellationToken ct = default)
        => GetAsync("/api/gas", ct);

    public static Task<string?> GetOnChainAsync(string asset = "btc", CancellationToken ct = default)
        => GetAsync($"/api/onchain?asset={Uri.EscapeDataString(asset)}", ct);

    public static Task<string?> GetWhalesAsync(int limit = 50, CancellationToken ct = default)
        => GetAsync($"/api/whales?limit={limit}", ct);

    public static Task<string?> GetLiquidationsAsync(int limit = 50, CancellationToken ct = default)
        => GetAsync($"/api/liquidations?limit={limit}", ct);

    public static Task<string?> GetDigestsAsync(string? kind = null, int limit = 20, CancellationToken ct = default)
        => GetAsync(kind is null ? $"/api/digests?limit={limit}" : $"/api/digests?kind={Uri.EscapeDataString(kind)}&limit={limit}", ct);

    public static Task<string?> GetTokenAsync(string chain, string token, CancellationToken ct = default)
        => GetAsync($"/api/dex/token/{Uri.EscapeDataString(chain)}/{Uri.EscapeDataString(token)}", ct);

    public static Task<string?> GetCandlesAsync(string chain, string token, string tf = "1m", CancellationToken ct = default)
        => GetAsync($"/api/dex/candles?chain={Uri.EscapeDataString(chain)}&token={Uri.EscapeDataString(token)}&tf={tf}", ct);

    // ── Per-user composition over those same shared parts ────────────────────────
    /// <summary>The whole watchlist tab in one request instead of 1 + N.</summary>
    public static Task<string?> GetWatchlistAsync(CancellationToken ct = default)
        => GetAsync("/api/watchlist", ct);

    /// <summary>Analytics for an explicit set, as <c>chain:token</c> pairs.</summary>
    public static Task<string?> GetTokensAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var joined = string.Join(",", ids);
        return string.IsNullOrEmpty(joined)
            ? Task.FromResult<string?>(null)
            : GetAsync($"/api/dex/tokens?ids={Uri.EscapeDataString(joined)}", ct);
    }

    /// <summary>Remaining AI allowance for this licence, so the UI can warn before a 429.</summary>
    public static Task<string?> GetAiBudgetAsync(CancellationToken ct = default)
        => GetAsync("/api/ai/budget", ct);

    /// <summary>
    /// GET a server path, sending <c>If-None-Match</c> when we already hold a copy. Returns the
    /// stored body on 304, the fresh body on 200, and null on anything else — including "not bound
    /// to a server" — so the caller falls back to its own direct fetch.
    /// </summary>
    public static async Task<string?> GetAsync(string path, CancellationToken ct = default, HttpClient? http = null)
    {
        var baseUrl = ChatClient.ServerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        var url = baseUrl.TrimEnd('/') + path;
        var client = http ?? SharedHttp;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            var license = ChatClient.LicenseTokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(license)) req.Headers.Add("X-License", license);

            if (Store.TryGetValue(path, out var cached))
                req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

            Interlocked.Increment(ref _requests);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                Interlocked.Increment(ref _notModified);
                // A 304 for a path we somehow have no copy of is not usable — treat as a miss.
                return cached?.Body;
            }

            if (!resp.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _failures);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var etag = resp.Headers.ETag?.ToString();
            if (!string.IsNullOrEmpty(etag))
                Store[path] = new Cached(etag, body);

            return body;
        }
        // An HttpClient timeout arrives as a cancellation the caller never asked for — that is a
        // failure to fall back on, not a cancellation to propagate. The filter is what separates
        // the two; TaskCanceledException derives from OperationCanceledException, so a second
        // catch clause cannot.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failures);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            Interlocked.Increment(ref _failures);
            return null;
        }
    }
}
