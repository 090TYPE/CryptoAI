using System.Globalization;
using System.Text.Json;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.Executor;

/// <summary>Production key provider — reads the user's CEX trade key (ciphertext + permissions) from the DB.</summary>
public sealed class SecretsCexKeyProvider : ICexKeyProvider
{
    private readonly SecretsRepository _secrets;
    public SecretsCexKeyProvider(SecretsRepository secrets) => _secrets = secrets;

    public async Task<CexKeyMaterial?> FindAsync(Guid userId, string exchange, CancellationToken ct)
    {
        var m = await _secrets.FindCexKeyAsync(userId, exchange, ct);
        return m is null ? null : new CexKeyMaterial(m.Ciphertext, m.WrappedDek, m.Permissions);
    }
}

/// <summary>
/// Production price source for order sizing (notional → quantity). Uses the Binance public spot
/// ticker — venue-agnostic enough for sizing a DCA buy; not used for execution price.
/// </summary>
public sealed class HttpPriceSource : IPriceSource
{
    private readonly HttpClient _http;
    public HttpPriceSource(HttpClient http) => _http = http;

    public async Task<decimal> GetPriceAsync(string exchange, string symbol, CancellationToken ct)
    {
        var url = $"https://api.binance.com/api/v3/ticker/price?symbol={symbol}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("price", out var p)
            && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var px)
            ? px : 0m;
    }
}
