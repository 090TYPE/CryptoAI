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
/// <param name="Cap">Daily allowance for this tier. Meaningless when <paramref name="Unlimited"/>.</param>
/// <param name="ResetsUtc">When the counter rolls over.</param>
/// <param name="Unlimited">
/// Сервер расход считает, но предел не применяет — идёт тестовый период. Отдельное поле, а не
/// <c>Cap == 0</c>: ноль здесь уже означал «сервер не ответил», и без флага снятая квота выглядела
/// бы в настройках ровно как обрыв связи.
/// </param>
/// <param name="Tiered">
/// Действует предел ТАРИФА, а не общий предохранитель. Пока тарифы не применяются, число в
/// <paramref name="Cap"/> — страховка от разгона, одна на всех, и называть её тарифной квотой
/// значит обещать клиенту, что за деньги дадут больше.
/// </param>
public sealed record AiSubscription(string? Edition, long Used, long Cap, DateTime ResetsUtc,
    bool Unlimited = false, bool Tiered = false)
{
    public long Remaining => Unlimited ? 0 : Math.Max(0, Cap - Used);

    /// <summary>0..1 of the allowance consumed. Clamped so a cap change mid-day cannot exceed 1.</summary>
    public double Fraction => Unlimited || Cap <= 0 ? 0 : Math.Clamp((double)Used / Cap, 0, 1);
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
                    : DateTime.UtcNow.Date.AddDays(1),
                // Поле появилось вместе со снятием квот. Сервер постарше его не пришлёт, и тогда
                // false — прежнее поведение, а не «без лимита» по умолчанию.
                root.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True,
                // То же правило: сервер, не знающий про тестовый период, тарифы применяет.
                !root.TryGetProperty("tiered", out var t) || t.ValueKind != JsonValueKind.False);
        }
        catch
        {
            return null;
        }
    }
}
