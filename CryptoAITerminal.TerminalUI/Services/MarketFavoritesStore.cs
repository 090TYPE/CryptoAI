using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Remembers which rows on the Markets desk the user starred.
/// </summary>
/// <remarks>
/// The star used to live only in <c>CexMarketItemViewModel.IsFavorite</c>, which is rebuilt from
/// scratch on every launch — so every star was silently lost on restart while the tracked markets
/// themselves survived, which is what made it look like the coins had disappeared.
/// Best-effort like the other stores here: a read never throws, a failed write is not fatal.
/// </remarks>
public sealed class MarketFavoritesStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _path;

    public MarketFavoritesStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal",
            "market-favorites.json");
    }

    /// <summary>
    /// Stable identity of a market row — venue plus ticker, upper-cased. The same pair already
    /// identifies a market when one is removed, so a star follows the row it was put on even
    /// when two venues list the same ticker.
    /// </summary>
    public static string KeyOf(string? exchange, string? symbol) =>
        ((exchange ?? "").Trim() + "|" + (symbol ?? "").Trim()).ToUpperInvariant();

    public HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new HashSet<string>(StringComparer.Ordinal);
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.Ordinal);
            var keys = JsonSerializer.Deserialize<List<string>>(json, Options);
            return keys is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(keys.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void Save(IEnumerable<string> keys)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var ordered = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(ordered, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
