using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Rate-limit classification and exponential backoff shared by the terminal's REST pollers.
/// <para>
/// Polling at the normal rate through a 429 only extends the ban: Binance answers 418 (IP ban)
/// after enough ignored 429s, and DexScreener/Deribit simply keep refusing. Every read poller
/// therefore slows down on a rate-limit answer and returns to its base rate after a clean pass.
/// </para>
/// READ-ONLY BY CONTRACT: never retry an order placement through this. A 429 on
/// <c>PlaceOrder</c> does not prove the order was rejected, and a retry can double the position.
/// </summary>
internal static class RestBackoff
{
    /// <summary>Upper bound on a single pause, including one taken from a Retry-After header.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Doubles <paramref name="current"/>, capped at <paramref name="max"/>.
    /// Used by pollers that own a timer or loop delay instead of a per-request retry.
    /// </summary>
    public static TimeSpan Grow(TimeSpan current, TimeSpan max)
    {
        var next = current + current;
        return next > max ? max : next;
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or anything it wraps) is the exchange saying "too fast".
    /// Binance answers 418 once an IP ban starts, and several exchange SDKs report the ban as a
    /// plain message with no typed status, so the text is checked too.
    /// </summary>
    public static bool IsRateLimit(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException http && IsRateLimit(http.StatusCode))
                return true;

            var message = e.Message;
            if (message.Contains("429", StringComparison.Ordinal) ||
                message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsRateLimit(HttpStatusCode? status)
        => status == HttpStatusCode.TooManyRequests || (int?)status == 418;

    /// <summary>
    /// GET that survives a rate limit: on 429/418 or a 5xx it waits — honouring Retry-After when
    /// the server sends one — and tries again with a doubling pause. Anything else (404, 400, a
    /// transport failure) throws immediately, because retrying it would only burn quota.
    /// </summary>
    public static async Task<string> GetStringAsync(
        HttpClient http, string url, CancellationToken ct,
        int maxAttempts = 3, TimeSpan? firstPause = null)
    {
        var pause = firstPause ?? TimeSpan.FromSeconds(2);

        for (var attempt = 1; ; attempt++)
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var status = resp.StatusCode;
            if (attempt >= maxAttempts || !(IsRateLimit(status) || (int)status >= 500))
                throw new HttpRequestException(
                    $"GET {url} failed with {(int)status} {resp.ReasonPhrase}", null, status);

            var wait = RetryAfterOf(resp) ?? pause;
            await Task.Delay(wait, ct).ConfigureAwait(false);
            pause = Grow(pause, MaxPause);
        }
    }

    /// <summary>
    /// Retry-After as an absolute wait, clamped to <see cref="MaxPause"/> — the header is remote
    /// input and an hour-long value would freeze the poller for the rest of the session.
    /// </summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
    {
        var header = resp.Headers.RetryAfter;
        if (header is null) return null;

        var delta = header.Delta;
        if (delta is null && header.Date is { } date) delta = date - DateTimeOffset.UtcNow;
        if (delta is null || delta.Value <= TimeSpan.Zero) return null;

        return delta.Value > MaxPause ? MaxPause : delta.Value;
    }
}
