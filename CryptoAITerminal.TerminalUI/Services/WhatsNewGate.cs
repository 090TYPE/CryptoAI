using System;
using System.IO;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Decides whether the "What's New" overlay should appear after an update, and
/// persists the last-seen app version. The decision is a pure function of two
/// version strings; all file IO is failure-tolerant.
/// </summary>
public sealed class WhatsNewGate
{
    private readonly string _markerPath;

    public WhatsNewGate(string? markerPath = null)
        => _markerPath = markerPath ?? DefaultMarkerPath;

    private static string DefaultMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal",
        ".last-version");

    /// <summary>
    /// True only when <paramref name="lastSeen"/> is a parseable version strictly
    /// lower than <paramref name="current"/> (i.e. a real update happened). A missing,
    /// empty, equal, higher, or unparseable marker returns false — never nag.
    /// </summary>
    public static bool ShouldShow(string? lastSeen, string current)
    {
        var last = ParseVersion(lastSeen);
        var cur  = ParseVersion(current);
        if (last is null || cur is null) return false;
        return last < cur;
    }

    /// <summary>Reads the persisted last-seen version, or null if absent/unreadable.</summary>
    public string? ReadLastSeen()
    {
        try
        {
            if (!File.Exists(_markerPath)) return null;
            var s = File.ReadAllText(_markerPath).Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    /// <summary>Persists the last-seen version. Failures are non-fatal.</summary>
    public void WriteLastSeen(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
            File.WriteAllText(_markerPath, version);
        }
        catch
        {
            // Non-fatal: worst case the overlay may re-show next launch.
        }
    }

    private static Version? ParseVersion(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].Trim('.');
        if (s.Length == 0) return null;
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : null;
    }
}
