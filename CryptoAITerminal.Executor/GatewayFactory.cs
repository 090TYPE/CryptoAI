using System.Text.Json;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.Gateway.Bybit;
using CryptoAITerminal.Gateway.KuCoin;
using CryptoAITerminal.Gateway.OKX;

namespace CryptoAITerminal.Executor;

/// <summary>
/// Builds a keyed <see cref="IExchangeGateway"/> from decrypted per-user credentials.
/// Credentials JSON: { "key": "...", "secret": "...", "passphrase": "..." (OKX/KuCoin) }.
///
/// Binance is not yet supported server-side — its gateway reads process-level env keys and has no
/// per-instance credential path (needs a keyed constructor; Track 4 follow-up). Futures land in 4.3.
/// </summary>
public sealed class GatewayFactory : IGatewayFactory
{
    private sealed record Creds(string? Key, string? Secret, string? Passphrase);

    public IExchangeGateway Create(string exchange, string market, string credentialsJson)
    {
        if (!string.Equals(market, "spot", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"market '{market}' not supported yet (spot only in Phase 4.1)");

        var c = ParseCreds(credentialsJson);
        return exchange.ToLowerInvariant() switch
        {
            "binance" => new BinanceGateway(null, c.Key, c.Secret),
            "bybit"   => new BybitGateway(null, c.Key, c.Secret),
            "okx"     => new OKXGateway(null, c.Key, c.Secret, c.Passphrase),
            "kucoin"  => new KucoinGateway(null, c.Key, c.Secret, c.Passphrase),
            _ => throw new NotSupportedException($"exchange '{exchange}' not supported")
        };
    }

    private static Creds ParseCreds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new Creds(
                r.TryGetProperty("key", out var k) ? k.GetString() : null,
                r.TryGetProperty("secret", out var s) ? s.GetString() : null,
                r.TryGetProperty("passphrase", out var p) ? p.GetString() : null);
        }
        catch (JsonException) { return new Creds(null, null, null); }
    }
}
