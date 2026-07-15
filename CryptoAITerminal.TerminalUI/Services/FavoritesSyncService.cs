using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Pushes the local DEX watchlist to the server so it is tracked 24/7. Best-effort:
/// never throws, silently no-ops when the server URL or license isn't configured
/// (trial users stay fully local). The single caller is <c>SaveWatchlist()</c>.
/// </summary>
public sealed class FavoritesSyncService
{
    private readonly HttpClient _http;
    private readonly Func<string?> _baseUrl;
    private readonly Func<string?> _token;

    public FavoritesSyncService(
        Func<string?>? baseUrlResolver = null,
        Func<string?>? tokenProvider = null,
        HttpClient? http = null)
    {
        _baseUrl = baseUrlResolver ?? ServerEndpoint.ResolveBaseUrl;
        _token = tokenProvider ?? (() => new LicenseService().GetToken());
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>True when both a server URL and a license token are available.</summary>
    public bool CanSync => !string.IsNullOrWhiteSpace(_baseUrl()) && !string.IsNullOrWhiteSpace(_token());

    /// <summary>PUT the full watchlist to /api/favorites. Returns false on any failure (best-effort).</summary>
    public async Task<bool> PushAsync(IEnumerable<DexWatchEntry> entries, CancellationToken ct = default)
    {
        var baseUrl = _baseUrl();
        var token = _token();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var payload = entries
                .Select(e => new { chain = e.ChainId, tokenAddress = e.TokenAddress, symbol = e.Symbol })
                .ToList();

            using var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/favorites")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Add("X-License", token);

            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false; // offline / server down — local save already succeeded
        }
    }

    /// <summary>GET the server's copy of the watchlist (multi-device pull). Empty on any failure.</summary>
    public async Task<IReadOnlyList<DexWatchEntry>> PullAsync(CancellationToken ct = default)
    {
        var baseUrl = _baseUrl();
        var token = _token();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            return Array.Empty<DexWatchEntry>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/favorites");
            req.Headers.Add("X-License", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<DexWatchEntry>();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<DexWatchEntry>();

            var list = new List<DexWatchEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var chain = el.TryGetProperty("chain", out var c) ? c.GetString() : null;
                var token2 = el.TryGetProperty("tokenAddress", out var t) ? t.GetString() : null;
                var sym = el.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(chain) && !string.IsNullOrWhiteSpace(token2))
                    list.Add(new DexWatchEntry(chain!, token2!, sym ?? string.Empty));
            }
            return list;
        }
        catch
        {
            return Array.Empty<DexWatchEntry>();
        }
    }
}
