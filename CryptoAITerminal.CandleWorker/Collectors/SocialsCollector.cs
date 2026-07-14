using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Enriches token_metadata with GeckoTerminal's token profile: logo, description,
/// website and socials (twitter/telegram). Free — no key. Runs hourly (rarely changes).
/// Passes null for pair/market cap so it never clobbers the market collector's values.
/// </summary>
public sealed class SocialsCollector : IDataCollector
{
    private readonly TrackedTokenRepository _tracked;
    private readonly MetadataRepository _meta;
    private readonly HoldersRepository _holders;
    private readonly GeckoTerminalClient _gecko;

    public SocialsCollector(TrackedTokenRepository tracked, MetadataRepository meta, HoldersRepository holders, GeckoTerminalClient gecko)
    {
        _tracked = tracked;
        _meta = meta;
        _holders = holders;
        _gecko = gecko;
    }

    public string Name => "socials";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var active = await _tracked.ListActiveAsync(ct);
        var count = 0;

        foreach (var t in active)
        {
            try
            {
                var network = ChainMap.ToGeckoNetwork(t.Chain);
                if (network is null) continue;

                var m = await _gecko.GetTokenInfoAsync(network, t.TokenAddress, ct);
                if (m is null) continue;

                await _meta.UpsertAsync(t.Chain, t.TokenAddress,
                    NullIfEmpty(m.Name), NullIfEmpty(m.Symbol),
                    NullIfEmpty(m.ImageUrl), NullIfEmpty(m.Description),
                    NullIfEmpty(m.Website), NullIfEmpty(m.Twitter), NullIfEmpty(m.Telegram),
                    pairAddress: null, marketCap: null, ct);

                // Holder distribution comes free from the same token-info call.
                await _holders.UpsertAsync(t.Chain, t.TokenAddress,
                    m.Holders > 0 ? m.Holders : null,
                    NullIfZero(m.HoldersTop10Pct), NullIfZero(m.Holders11To30Pct),
                    NullIfZero(m.Holders31To50Pct), NullIfZero(m.HoldersRestPct), ct);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip this token, keep going */ }
        }

        return count;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static decimal? NullIfZero(decimal v) => v == 0m ? null : v;
}
