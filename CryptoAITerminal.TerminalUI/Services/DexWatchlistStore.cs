using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>One saved watchlist token.</summary>
public sealed record DexWatchEntry(string ChainId, string TokenAddress, string Symbol);

/// <summary>Persists the DEX token watchlist to disk. Best-effort — never throws.</summary>
public sealed class DexWatchlistStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _path;

    public DexWatchlistStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal",
            "dex_watchlist.json");
    }

    public List<DexWatchEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<DexWatchEntry>();
            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json)
                ? new List<DexWatchEntry>()
                : JsonSerializer.Deserialize<List<DexWatchEntry>>(json, Options) ?? new List<DexWatchEntry>();
        }
        catch
        {
            return new List<DexWatchEntry>();
        }
    }

    public void Save(IEnumerable<DexWatchEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(entries, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
