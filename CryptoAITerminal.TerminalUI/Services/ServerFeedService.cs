using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>One shared AI digest published by the server (same content for every user).</summary>
public sealed record ServerDigest(string Kind, string Title, string? Body, DateTime CreatedUtc);

/// <summary>One personal event delivered to this terminal (alert, AI anomaly, bot).</summary>
public sealed record InboxMessage(Guid Id, string Kind, string Title, string? Message, DateTime CreatedUtc, DateTime? ReadUtc);

/// <summary>
/// Reads what the server produced: the 30 shared AI digest streams and this user's inbox.
/// Best-effort — returns empty when the server URL or license isn't configured (app stays local).
/// Mirrors <see cref="FavoritesSyncService"/>: same endpoint/licence resolution.
/// </summary>
public sealed class ServerFeedService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Func<string?> _baseUrl;
    private readonly Func<string?> _token;

    public ServerFeedService(
        Func<string?>? baseUrlResolver = null,
        Func<string?>? tokenProvider = null,
        HttpClient? http = null)
    {
        _baseUrl = baseUrlResolver ?? ServerEndpoint.ResolveBaseUrl;
        _token = tokenProvider ?? (() => new LicenseService().GetToken());
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl()) && !string.IsNullOrWhiteSpace(_token());

    /// <summary>Shared AI digests. <paramref name="kind"/> null = every stream.</summary>
    public async Task<IReadOnlyList<ServerDigest>> GetDigestsAsync(string? kind = null, int limit = 20, CancellationToken ct = default)
    {
        var q = $"/api/digests?limit={limit}" + (string.IsNullOrWhiteSpace(kind) ? "" : $"&kind={Uri.EscapeDataString(kind)}");
        return await GetAsync<ServerDigest>(q, ct);
    }

    /// <summary>This user's events.</summary>
    public async Task<IReadOnlyList<InboxMessage>> GetInboxAsync(bool unreadOnly = false, int limit = 50, CancellationToken ct = default)
        => await GetAsync<InboxMessage>($"/api/inbox?unread={(unreadOnly ? "true" : "false")}&limit={limit}", ct);

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, "/api/inbox/unread-count", ct);
        if (body is null) return 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("unread", out var u) && u.TryGetInt32(out var n) ? n : 0;
        }
        catch { return 0; }
    }

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, $"/api/inbox/{id}/read", ct) is not null;

    public async Task<bool> MarkAllReadAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "/api/inbox/read-all", ct) is not null;

    // ── plumbing ──────────────────────────────────────────────────────────────
    private async Task<IReadOnlyList<T>> GetAsync<T>(string path, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, path, ct);
        if (body is null) return Array.Empty<T>();
        try { return JsonSerializer.Deserialize<List<T>>(body, Json) ?? new List<T>(); }
        catch { return Array.Empty<T>(); }
    }

    private async Task<string?> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var baseUrl = _baseUrl();
        var token = _token();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            using var req = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);
            req.Headers.Add("X-License", token);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch
        {
            return null; // offline / server down — terminal keeps working locally
        }
    }
}
