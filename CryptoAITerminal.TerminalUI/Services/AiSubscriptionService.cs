using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>What the server says this licence is entitled to today.</summary>
/// <param name="Edition">Tier name from the licence, e.g. Lite / Pro / Max.</param>
/// <param name="Used">Tokens spent today.</param>
/// <param name="Cap">Daily allowance for this tier.</param>
/// <param name="ResetsUtc">When the counter rolls over.</param>
public sealed record AiSubscription(string? Edition, long Used, long Cap, DateTime ResetsUtc)
{
    public long Remaining => Math.Max(0, Cap - Used);

    /// <summary>0..1 of the allowance consumed. Clamped so a cap change mid-day cannot exceed 1.</summary>
    public double Fraction => Cap <= 0 ? 0 : Math.Clamp((double)Used / Cap, 0, 1);
}

/// <summary>
/// Reads the AI allowance from the server.
///
/// Exists because a limit the customer cannot see is indistinguishable from a broken product: the
/// AI panels simply start returning worse answers and nothing says why. The terminal already
/// degrades to its own deterministic output when the server refuses — this is what lets it say so.
///
/// Reuses the routing the AI calls already use (<see cref="ChatClient.ServerBaseUrl"/> and the
/// licence token provider), so there is nothing extra to configure and nothing that can drift out
/// of step with where the AI calls actually go.
/// </summary>
public static class AiSubscriptionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>True when this terminal is bound to a CryptoAI server rather than a customer key.</summary>
    public static bool IsServerBound => !string.IsNullOrWhiteSpace(ChatClient.ServerBaseUrl);

    /// <summary>
    /// The current allowance, or null when unbound, unlicensed, or the server cannot be reached.
    /// Never throws: this feeds a settings panel, and a status line that cannot render is worse
    /// than one that says nothing.
    /// </summary>
    public static async Task<AiSubscription?> FetchAsync(CancellationToken ct = default)
    {
        if (!IsServerBound) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                ChatClient.ServerBaseUrl!.TrimEnd('/') + "/api/ai/budget");

            var token = ChatClient.LicenseTokenProvider?.Invoke();
            if (string.IsNullOrWhiteSpace(token)) return null;   // no licence yet — nothing to report
            req.Headers.Add("X-License", token);

            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return new AiSubscription(
                root.TryGetProperty("edition", out var e) ? e.GetString() : null,
                root.TryGetProperty("used", out var u) && u.TryGetInt64(out var uv) ? uv : 0,
                root.TryGetProperty("cap", out var c) && c.TryGetInt64(out var cv) ? cv : 0,
                root.TryGetProperty("resetsUtc", out var r) && r.TryGetDateTime(out var rv)
                    ? rv
                    : DateTime.UtcNow.Date.AddDays(1));
        }
        catch
        {
            return null;
        }
    }
}
