using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// <see cref="IReleaseNotesService"/> backed by the GitHub Releases REST API.
/// Reads the release whose tag is <c>v{version}</c> and returns its markdown body.
/// Never throws into the UI (except <see cref="OperationCanceledException"/>).
/// </summary>
public sealed class ReleaseNotesService : IReleaseNotesService
{
    private readonly HttpClient _http;
    private readonly string _repoSlug;

    public ReleaseNotesService(string? repoSlug = null, HttpClient? http = null)
    {
        _repoSlug = repoSlug ?? AppInfo.RepoSlug;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("CryptoAITerminal-ReleaseNotes");
    }

    public async Task<string?> GetNotesAsync(string version, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_repoSlug}/releases/tags/v{version}";
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }
}
