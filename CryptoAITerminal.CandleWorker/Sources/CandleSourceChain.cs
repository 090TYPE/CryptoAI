using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Sources;

/// <summary>
/// Which source answered, and what it returned.
/// <paramref name="AnySourceFailed"/> separates "a provider is broken" from "every provider
/// answered honestly and this token simply has no data" — without it the first drowns in the second,
/// since an unservable token is retried for as long as it stays tracked.
/// </summary>
public sealed record CandleFetch(
    IReadOnlyList<CandleRow> Rows, string? Source, string? LastError, bool AnySourceFailed);

/// <summary>
/// Tries every configured candle source in order and takes the first one that actually returns
/// data. Free keyless sources come first, so a key is only ever spent when the free path could not
/// answer — a token GeckoTerminal does not index, a rate-limit, or an outage.
///
/// Keys are read from <see cref="ProviderKeyStore"/> per fetch rather than captured at startup:
/// the provider_keys table is editable from the admin console and the collectors are meant to pick
/// up a newly pasted key on the next run without a redeploy. A source whose key is absent or
/// disabled is skipped silently — that is configuration, not failure.
///
/// A source that throws does not stop the chain; its message is kept so a run that ends with no
/// data can say which sources were tried and why each declined, instead of reporting an empty
/// result that looks identical to "this token has no trades".
/// </summary>
public sealed class CandleSourceChain
{
    private readonly IReadOnlyList<ICandleSource> _sources;
    private readonly ProviderKeyStore _keys;
    private readonly ILogger<CandleSourceChain> _log;

    public CandleSourceChain(IEnumerable<ICandleSource> sources, ProviderKeyStore keys, ILogger<CandleSourceChain> log)
    {
        _sources = sources.ToList();
        _keys = keys;
        _log = log;
    }

    /// <summary>Names in the order they will be tried — logged once at startup so the order is visible.</summary>
    public string Order => string.Join(" -> ", _sources.Select(s => s.RequiredKey is null ? s.Name : $"{s.Name}(keyed)"));

    public async Task<CandleFetch> FetchAsync(CandleRequest req, CancellationToken ct)
    {
        string? lastError = null;
        var failed = false;
        var tried = new List<string>();

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();

            string? key = null;
            if (source.RequiredKey is { } needed)
            {
                key = await _keys.GetAsync(needed, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(key)) continue;
            }

            try
            {
                var rows = await source.FetchAsync(req, key, ct).ConfigureAwait(false);

                // null means the source declined and made no call, so it must not be reported as
                // tried: reading "tried: geckoterminal" in a log while it never left the process is
                // exactly the wrong thing to believe when diagnosing a token that yields nothing.
                if (rows is null) continue;

                tried.Add(source.Name);
                if (rows.Count > 0) return new CandleFetch(rows, source.Name, lastError, failed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed = true;
                lastError = $"{source.Name}: {ex.Message}";
                _log.LogDebug(ex, "candle source {Source} failed for {Chain}/{Token}", source.Name, req.Chain, req.TokenAddress);
            }
        }

        // Always explain an empty result. Previously a run where every source returned an empty list
        // without throwing produced no message at all, so "nothing to collect" was indistinguishable
        // from "the worker never got here".
        lastError ??= tried.Count == 0
            ? "no candle source is configured — every keyed source is missing its provider_keys entry"
            : $"no data from any source (tried: {string.Join(", ", tried)})";

        return new CandleFetch([], null, lastError, failed);
    }
}
