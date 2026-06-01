using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.LicenseBot;

public sealed record CryptoInvoice(long InvoiceId, string PayUrl, string Status);

/// <summary>
/// Minimal client for the Crypto Pay API (@CryptoBot / @send). Lets the bot
/// charge in a fiat amount (e.g. RUB) while the customer pays the crypto
/// equivalent in USDT/TON/etc. No external payment provider account needed —
/// just a Crypto Pay app token from @CryptoBot → Crypto Pay → Create App.
///
/// Docs: https://help.crypt.bot/crypto-pay-api
/// </summary>
public sealed class CryptoPayClient
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _assets;

    /// <param name="apiBase">Mainnet: https://pay.crypt.bot/api/  · Testnet: https://testnet-pay.crypt.bot/api/</param>
    public CryptoPayClient(string token, string assets, string apiBase, HttpClient? http = null)
    {
        _token  = token;
        _assets = assets;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.BaseAddress ??= new Uri(apiBase.EndsWith('/') ? apiBase : apiBase + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token);

    /// <summary>Create a fiat-priced invoice; the customer pays the crypto equivalent.</summary>
    public async Task<CryptoInvoice?> CreateFiatInvoiceAsync(
        decimal fiatAmount, string fiatCurrency, string description, string payload,
        int expiresInSeconds = 1800, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["currency_type"]   = "fiat",
            ["fiat"]            = fiatCurrency,
            ["amount"]          = fiatAmount.ToString("0.##", CultureInfo.InvariantCulture),
            ["accepted_assets"] = _assets,
            ["description"]     = description.Length > 1024 ? description[..1024] : description,
            ["payload"]         = payload,
            ["expires_in"]      = expiresInSeconds.ToString(CultureInfo.InvariantCulture),
            ["allow_comments"]  = "false",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "createInvoice")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("Crypto-Pay-API-Token", _token);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
        if (!root.TryGetProperty("result", out var r)) return null;

        var id = r.GetProperty("invoice_id").GetInt64();
        var status = r.TryGetProperty("status", out var st) ? st.GetString() ?? "active" : "active";
        var url = r.TryGetProperty("bot_invoice_url", out var bu) ? bu.GetString()
                : r.TryGetProperty("pay_url", out var pu) ? pu.GetString()
                : null;
        return url is null ? null : new CryptoInvoice(id, url, status);
    }

    /// <summary>Returns the invoice status: "active" | "paid" | "expired" (or null on error).</summary>
    public async Task<string?> GetInvoiceStatusAsync(long invoiceId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"getInvoices?invoice_ids={invoiceId}");
        req.Headers.Add("Crypto-Pay-API-Token", _token);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
        if (!root.TryGetProperty("result", out var result)) return null;

        // result may be { items: [...] } or an array depending on API version.
        JsonElement items = result;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var it))
            items = it;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;

        var first = items[0];
        return first.TryGetProperty("status", out var s) ? s.GetString() : null;
    }
}
