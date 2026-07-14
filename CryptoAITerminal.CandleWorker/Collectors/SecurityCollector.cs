using System.Text.Json;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Scam/risk verdicts for every tracked token via GoPlus + Honeypot.is (EVM) and
/// RugCheck (Solana). Free — no key. Writes honeypot flag, buy/sell tax, risk score.
/// </summary>
public sealed class SecurityCollector : IDataCollector
{
    private readonly TrackedTokenRepository _tracked;
    private readonly SecurityRepository _security;
    private readonly TokenSecurityService _svc;

    public SecurityCollector(TrackedTokenRepository tracked, SecurityRepository security, TokenSecurityService svc)
    {
        _tracked = tracked;
        _security = security;
        _svc = svc;
    }

    public string Name => "security";
    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var active = await _tracked.ListActiveAsync(ct);
        var count = 0;

        foreach (var t in active)
        {
            try
            {
                var r = await _svc.ScanAsync(t.TokenAddress, t.Chain, ct);
                if (r.ScanFailed) continue;

                var details = JsonSerializer.Serialize(new
                {
                    flags = r.Flags,
                    mintable = r.HasMintFunction,
                    ownershipRenounced = r.IsOwnershipRenounced,
                    blacklist = r.HasBlacklist,
                    proxy = r.IsProxy,
                    hiddenOwner = r.HiddenOwner,
                    top10Pct = r.TopHolderConcentrationPercent,
                    source = r.Source
                });

                await _security.UpsertAsync(t.Chain, t.TokenAddress,
                    r.IsHoneypot, r.BuyTaxPercent, r.SellTaxPercent, r.Verdict, r.SecurityScore, details, ct);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* one token's scan failing must not stop the rest */ }
        }

        return count;
    }
}
