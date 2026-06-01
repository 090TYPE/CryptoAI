using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Current app identity — single source of truth for version + repo.</summary>
public static class AppInfo
{
    public const string Version  = "1.0.0";
    public const string RepoSlug = "090TYPE/CryptoAI";
    public static string ReleasesUrl => $"https://github.com/{RepoSlug}/releases";
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? Error);

/// <summary>
/// Checks GitHub Releases for a newer version. Network/parse failures are
/// non-fatal — they return a result with <c>Error</c> set and
/// <c>IsUpdateAvailable=false</c>, never throwing into the UI.
/// </summary>
public sealed class UpdateCheckService
{
    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly string _repoSlug;

    public UpdateCheckService(string? currentVersion = null, string? repoSlug = null, HttpClient? http = null)
    {
        _currentVersion = currentVersion ?? AppInfo.Version;
        _repoSlug       = repoSlug ?? AppInfo.RepoSlug;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("CryptoAITerminal-UpdateCheck"))
        {
            // header may already be set when an HttpClient is injected — ignore
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var fallbackUrl = $"https://github.com/{_repoSlug}/releases";
        try
        {
            var url = $"https://api.github.com/repos/{_repoSlug}/releases/latest";
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return new UpdateCheckResult(false, _currentVersion, _currentVersion, fallbackUrl,
                    $"GitHub API {(int)res.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? fallbackUrl : fallbackUrl;

            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateCheckResult(false, _currentVersion, _currentVersion, fallbackUrl, "No tag_name in release");

            var newer = IsNewer(_currentVersion, tag);
            return new UpdateCheckResult(newer, _currentVersion, Normalize(tag), htmlUrl, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, _currentVersion, _currentVersion, fallbackUrl, ex.Message);
        }
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a strictly higher semantic version
    /// than <paramref name="current"/>. Leading 'v' and pre-release suffixes are
    /// tolerated; unparseable inputs return false (fail safe — no false prompt).
    /// </summary>
    public static bool IsNewer(string current, string latest)
    {
        var c = ParseVersion(current);
        var l = ParseVersion(latest);
        if (c is null || l is null) return false;
        return l > c;
    }

    private static Version? ParseVersion(string raw)
    {
        var s = Normalize(raw);
        // Drop any pre-release / build suffix: keep the leading numeric dotted part.
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].Trim('.');
        if (s.Length == 0) return null;

        // Version requires at least major.minor — pad a bare "1" to "1.0".
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : null;
    }

    private static string Normalize(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        return s.Trim();
    }
}
