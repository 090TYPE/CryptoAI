using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Persists DEX price samples to disk so history survives app restarts.
/// Each token gets its own JSON file: {cacheDir}/{chainId}_{tokenAddress}.json
/// Old samples (>30 days) are pruned automatically on save.
/// </summary>
public sealed class DexPriceHistoryCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _cacheDir;

    public DexPriceHistoryCache(string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal", "dex-price-cache");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Loads cached samples for a token. Returns empty list if no cache exists.
    /// </summary>
    public List<DexPriceSampleDto> Load(string chainId, string tokenAddress)
    {
        var path = GetPath(chainId, tokenAddress);
        if (!File.Exists(path)) return new List<DexPriceSampleDto>();
        try
        {
            var json    = File.ReadAllText(path);
            var samples = JsonSerializer.Deserialize<List<DexPriceSampleDto>>(json, JsonOpts)
                          ?? new List<DexPriceSampleDto>();
            return samples
                .Where(s => DateTime.UtcNow - s.TimestampUtc < MaxAge)
                .OrderBy(s => s.TimestampUtc)
                .ToList();
        }
        catch { return new List<DexPriceSampleDto>(); }
    }

    /// <summary>
    /// Merges new samples into existing cache and saves.
    /// De-duplicates by timestamp (1-second resolution), prunes old entries.
    /// </summary>
    public void Save(string chainId, string tokenAddress, IEnumerable<DexPriceSampleDto> newSamples)
    {
        var path     = GetPath(chainId, tokenAddress);
        var existing = Load(chainId, tokenAddress);
        var cutoff   = DateTime.UtcNow - MaxAge;

        var merged = existing
            .Concat(newSamples)
            .Where(s => s.TimestampUtc >= cutoff && s.PriceUsd > 0)
            .GroupBy(s => new DateTime(s.TimestampUtc.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc))
            .Select(g => g.OrderByDescending(s => s.PriceUsd).First())
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(merged, JsonOpts));
        }
        catch { /* disk full / permissions — silently skip */ }
    }

    /// <summary>
    /// Merges on-chain scanned candles into the cache as synthetic samples.
    /// One sample per candle at the close price.
    /// </summary>
    public void SaveCandles(string chainId, string tokenAddress, IEnumerable<DexOhlcvPoint> candles)
    {
        var samples = candles
            .Where(c => c.Close > 0)
            .Select(c => new DexPriceSampleDto(c.Timestamp, c.Close));
        Save(chainId, tokenAddress, samples);
    }

    public bool HasCache(string chainId, string tokenAddress) =>
        File.Exists(GetPath(chainId, tokenAddress));

    public void Delete(string chainId, string tokenAddress)
    {
        var path = GetPath(chainId, tokenAddress);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetPath(string chainId, string tokenAddress)
    {
        var safe = string.Join("_", new[] { chainId, tokenAddress }
            .Select(s => string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c == '-'))));
        return Path.Combine(_cacheDir, safe + ".json");
    }
}

public sealed record DexPriceSampleDto(DateTime TimestampUtc, decimal PriceUsd);
