using System.Net.Http;
using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class DexScreenerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public DexScreenerClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://api.dexscreener.com/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexTokenInfo>> GetLatestTokensAsync(
        IEnumerable<string>? chainIds = null,
        int maxTokensPerChain = 30,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("token-profiles/latest/v1", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var profiles = await JsonSerializer.DeserializeAsync<List<TokenProfileDto>>(stream, JsonOptions, cancellationToken)
            ?? new List<TokenProfileDto>();

        var allowedChains = chainIds?
            .Where(static chainId => !string.IsNullOrWhiteSpace(chainId))
            .Select(static chainId => chainId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effectiveMaxTokensPerChain = Math.Clamp(maxTokensPerChain, 5, 150);

        var profileGroups = profiles
            .Where(static profile => !string.IsNullOrWhiteSpace(profile.ChainId) && !string.IsNullOrWhiteSpace(profile.TokenAddress))
            .Where(profile => allowedChains is null || allowedChains.Count == 0 || allowedChains.Contains(profile.ChainId!))
            .GroupBy(profile => profile.ChainId!, StringComparer.OrdinalIgnoreCase);

        var pairTasks = profileGroups.Select(async group =>
        {
            var tokensByAddress = group
                .GroupBy(profile => profile.TokenAddress!, StringComparer.OrdinalIgnoreCase)
                .Select(static profilesGroup => profilesGroup.First())
                .Take(effectiveMaxTokensPerChain)
                .ToList();

            if (tokensByAddress.Count == 0)
            {
                return Array.Empty<DexTokenInfo>();
            }

            var joinedAddresses = string.Join(',', tokensByAddress.Select(profile => profile.TokenAddress));
            var requestUri = $"tokens/v1/{group.Key}/{joinedAddresses}";

            using var tokenResponse = await _httpClient.GetAsync(requestUri, cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();

            await using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);
            var pairs = await JsonSerializer.DeserializeAsync<List<PairDto>>(tokenStream, JsonOptions, cancellationToken)
                ?? new List<PairDto>();

            return pairs
                .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
                .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
                .Select(MapPair)
                .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
                .ToArray();
        });

        var results = await Task.WhenAll(pairTasks);
        var tokens = results
            .SelectMany(static group => group)
            .ToList();

        if (allowedChains is { Count: > 0 })
        {
            var missingChains = allowedChains
                .Where(chainId => !tokens.Any(token => string.Equals(token.ChainId, chainId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (missingChains.Length > 0)
            {
                var fallbackTokens = await GetSearchFallbackTokensAsync(missingChains, effectiveMaxTokensPerChain, cancellationToken);
                tokens.AddRange(fallbackTokens);
            }
        }

        return tokens
            .GroupBy(token => $"{token.ChainId}::{token.TokenAddress}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(token => token.LiquidityUsd).First())
            .OrderByDescending(token => token.LiquidityUsd)
            .ThenByDescending(token => token.Volume24h)
            .ToList();
    }

    public async Task<IReadOnlyList<DexTokenInfo>> SearchTokensAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetLatestTokensAsync(cancellationToken: cancellationToken);
        }

        var requestUri = $"latest/dex/search?q={Uri.EscapeDataString(query.Trim())}";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponseDto>(stream, JsonOptions, cancellationToken);
        var pairs = payload?.Pairs ?? new List<PairDto>();

        return pairs
            .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
            .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
            .Select(MapPair)
            .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
            .OrderByDescending(token => token.LiquidityUsd)
            .ThenByDescending(token => token.Volume24h)
            .ToList();
    }

    public async Task<IReadOnlyList<DexTokenInfo>> GetTokensByAddressesAsync(
        string chainId,
        IEnumerable<string> tokenAddresses,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chainId))
        {
            return [];
        }

        var addresses = tokenAddresses
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Select(static address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addresses.Count == 0)
        {
            return [];
        }

        var results = new List<DexTokenInfo>();
        foreach (var batch in Batch(addresses, 30))
        {
            var joinedAddresses = string.Join(',', batch);
            var requestUri = $"tokens/v1/{chainId}/{joinedAddresses}";

            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pairs = await JsonSerializer.DeserializeAsync<List<PairDto>>(stream, JsonOptions, cancellationToken)
                ?? new List<PairDto>();

            results.AddRange(pairs
                .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
                .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
                .Select(MapPair)
                .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress)));
        }

        return results
            .GroupBy(token => token.TokenAddress, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(token => token.LiquidityUsd).First())
            .OrderByDescending(token => token.LiquidityUsd)
            .ThenByDescending(token => token.Volume24h)
            .ToList();
    }

    public async Task<IReadOnlyList<DexTokenInfo>> GetLaunchScoutTokensAsync(
        IEnumerable<string> chainIds,
        int maxTokensPerChain = 60,
        CancellationToken cancellationToken = default)
    {
        var normalizedChains = chainIds
            .Where(static chainId => !string.IsNullOrWhiteSpace(chainId))
            .Select(static chainId => chainId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedChains.Length == 0)
        {
            return [];
        }

        var normalizedCap = Math.Clamp(maxTokensPerChain, 10, 320);
        var launchTasks = normalizedChains.Select(chainId => GetLaunchScoutTokensForChainAsync(chainId, normalizedCap, cancellationToken));
        var results = await Task.WhenAll(launchTasks);

        return results
            .SelectMany(static group => group)
            .GroupBy(token => $"{token.ChainId}::{token.TokenAddress}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(token => token.LiquidityUsd).First())
            .OrderByDescending(token => token.LastUpdatedUtc)
            .ThenBy(token => token.LiquidityUsd)
            .ThenByDescending(token => token.Volume24h)
            .ToList();
    }

    public Task<IReadOnlyList<DexTokenInfo>> GetMomentumScoutTokensAsync(
        IEnumerable<string> chainIds,
        int maxTokensPerChain = 60,
        CancellationToken cancellationToken = default)
    {
        return GetScoutTokensAsync(chainIds, maxTokensPerChain, GetMomentumScoutSearchQueries, cancellationToken);
    }

    public Task<IReadOnlyList<DexTokenInfo>> GetNarrativeScoutTokensAsync(
        IEnumerable<string> chainIds,
        int maxTokensPerChain = 60,
        CancellationToken cancellationToken = default)
    {
        return GetScoutTokensAsync(chainIds, maxTokensPerChain, GetNarrativeScoutSearchQueries, cancellationToken);
    }

    public Task<IReadOnlyList<DexTokenInfo>> GetQuoteRouteScoutTokensAsync(
        IEnumerable<string> chainIds,
        int maxTokensPerChain = 60,
        CancellationToken cancellationToken = default)
    {
        return GetScoutTokensAsync(chainIds, maxTokensPerChain, GetQuoteRouteScoutSearchQueries, cancellationToken);
    }

    private async Task<IReadOnlyList<DexTokenInfo>> GetLaunchScoutTokensForChainAsync(
        string chainId,
        int maxTokensPerChain,
        CancellationToken cancellationToken)
    {
        var searchQueries = GetLaunchScoutSearchQueries(chainId);
        if (searchQueries.Count == 0)
        {
            return [];
        }

        var pairs = new List<PairDto>();
        foreach (var searchQuery in searchQueries)
        {
            var requestUri = $"latest/dex/search?q={Uri.EscapeDataString(searchQuery)}";
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SearchResponseDto>(stream, JsonOptions, cancellationToken);
            if (payload?.Pairs is { Count: > 0 })
            {
                pairs.AddRange(payload.Pairs);
            }
        }

        var normalizedCap = Math.Clamp(maxTokensPerChain, 10, 320);
        return pairs
            .Where(pair => string.Equals(pair.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
            .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
            .Select(MapPair)
            .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
            .Take(normalizedCap)
            .ToList();
    }

    private async Task<IReadOnlyList<DexTokenInfo>> GetScoutTokensAsync(
        IEnumerable<string> chainIds,
        int maxTokensPerChain,
        Func<string, IReadOnlyList<string>> queryFactory,
        CancellationToken cancellationToken)
    {
        var normalizedChains = chainIds
            .Where(static chainId => !string.IsNullOrWhiteSpace(chainId))
            .Select(static chainId => chainId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedChains.Length == 0)
        {
            return [];
        }

        var normalizedCap = Math.Clamp(maxTokensPerChain, 10, 320);
        var scoutTasks = normalizedChains.Select(chainId => GetScoutTokensForChainAsync(chainId, normalizedCap, queryFactory(chainId), cancellationToken));
        var results = await Task.WhenAll(scoutTasks);

        return results
            .SelectMany(static group => group)
            .GroupBy(token => $"{token.ChainId}::{token.TokenAddress}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(token => token.LiquidityUsd).First())
            .OrderByDescending(token => token.Volume24h)
            .ThenByDescending(token => token.PriceChange5m)
            .ThenByDescending(token => token.LiquidityUsd)
            .ToList();
    }

    private async Task<IReadOnlyList<DexTokenInfo>> GetScoutTokensForChainAsync(
        string chainId,
        int maxTokensPerChain,
        IReadOnlyList<string> searchQueries,
        CancellationToken cancellationToken)
    {
        if (searchQueries.Count == 0)
        {
            return [];
        }

        var pairs = new List<PairDto>();
        foreach (var searchQuery in searchQueries)
        {
            var requestUri = $"latest/dex/search?q={Uri.EscapeDataString(searchQuery)}";
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SearchResponseDto>(stream, JsonOptions, cancellationToken);
            if (payload?.Pairs is { Count: > 0 })
            {
                pairs.AddRange(payload.Pairs);
            }
        }

        var preferredDexId = GetPreferredDexId(chainId);
        var normalizedCap = Math.Clamp(maxTokensPerChain, 10, 320);

        var preferred = pairs
            .Where(pair => string.Equals(pair.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .Where(pair => string.IsNullOrWhiteSpace(preferredDexId) ||
                           string.Equals(pair.DexId, preferredDexId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
            .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
            .Select(MapPair)
            .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
            .Take(normalizedCap)
            .ToList();

        if (preferred.Count >= normalizedCap || string.IsNullOrWhiteSpace(preferredDexId))
        {
            return preferred;
        }

        var seen = preferred
            .Select(token => token.TokenAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var supplemental = pairs
            .Where(pair => string.Equals(pair.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
            .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
            .Select(MapPair)
            .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
            .Where(token => seen.Add(token.TokenAddress))
            .Take(Math.Max(0, normalizedCap - preferred.Count))
            .ToList();

        preferred.AddRange(supplemental);
        return preferred;
    }

    private static DexTokenInfo MapPair(PairDto pair)
    {
        return new DexTokenInfo
        {
            ChainId = pair.ChainId ?? string.Empty,
            DexId = pair.DexId ?? string.Empty,
            PairAddress = pair.PairAddress ?? string.Empty,
            TokenAddress = pair.BaseToken?.Address ?? string.Empty,
            Symbol = pair.BaseToken?.Symbol ?? string.Empty,
            Name = pair.BaseToken?.Name ?? string.Empty,
            QuoteSymbol = pair.QuoteToken?.Symbol ?? string.Empty,
            Url = pair.Url ?? string.Empty,
            PriceUsd = ParseDecimal(pair.PriceUsd),
            PriceNative = ParseDecimal(pair.PriceNative),
            PriceChange5m = pair.PriceChange?.M5 ?? 0,
            PriceChange1h = pair.PriceChange?.H1 ?? 0,
            PriceChange24h = pair.PriceChange?.H24 ?? 0,
            Volume24h = pair.Volume?.H24 ?? 0,
            LiquidityUsd = pair.Liquidity?.Usd ?? 0,
            MarketCap = pair.MarketCap ?? pair.Fdv ?? 0,
            LastUpdatedUtc = DateTime.UtcNow
        };
    }

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private async Task<IReadOnlyList<DexTokenInfo>> GetSearchFallbackTokensAsync(
        IReadOnlyCollection<string> chainIds,
        int maxTokensPerChain,
        CancellationToken cancellationToken)
    {
        var fallbackTasks = chainIds.Select(chainId => GetSearchFallbackTokensForChainAsync(chainId, maxTokensPerChain, cancellationToken));
        var results = await Task.WhenAll(fallbackTasks);

        return results
            .SelectMany(static group => group)
            .ToList();
    }

    private async Task<IReadOnlyList<DexTokenInfo>> GetSearchFallbackTokensForChainAsync(
        string chainId,
        int maxTokensPerChain,
        CancellationToken cancellationToken)
    {
        var searchQueries = GetFallbackSearchQueries(chainId);
        if (searchQueries.Count == 0)
        {
            return [];
        }

        var preferredDexId = GetPreferredDexId(chainId);
        var pairs = new List<PairDto>();

        foreach (var searchQuery in searchQueries)
        {
            var requestUri = $"latest/dex/search?q={Uri.EscapeDataString(searchQuery)}";
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SearchResponseDto>(stream, JsonOptions, cancellationToken);
            if (payload?.Pairs is { Count: > 0 })
            {
                pairs.AddRange(payload.Pairs);
            }
        }

        var normalizedCap = Math.Clamp(maxTokensPerChain, 5, 220);

        var preferred = pairs
            .Where(pair => string.Equals(pair.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .Where(pair => string.IsNullOrWhiteSpace(preferredDexId) ||
                           string.Equals(pair.DexId, preferredDexId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
            .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
            .Select(MapPair)
            .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
            .Take(normalizedCap)
            .ToList();

        if (preferred.Count >= normalizedCap || string.IsNullOrWhiteSpace(preferredDexId))
        {
            return preferred;
        }

        var seen = preferred
            .Select(token => token.TokenAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var supplemental = pairs
            .Where(pair => string.Equals(pair.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.BaseToken?.Address, StringComparer.OrdinalIgnoreCase)
            .Select(static pairGroup => pairGroup.OrderByDescending(pair => pair.Liquidity?.Usd ?? 0).First())
            .Select(MapPair)
            .Where(static token => !string.IsNullOrWhiteSpace(token.TokenAddress))
            .Where(token => seen.Add(token.TokenAddress))
            .Take(Math.Max(0, normalizedCap - preferred.Count))
            .ToList();

        preferred.AddRange(supplemental);
        return preferred;
    }

    private static IReadOnlyList<string> GetFallbackSearchQueries(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "bsc" => ["pancakeswap", "wbnb", "bnb", "usdt", "busd", "cake", "bsc"],
            "ethereum" => ["uniswap", "weth", "eth", "usdt", "usdc"],
            "base" => ["aerodrome", "base", "weth", "usdc"],
            "solana" => ["raydium", "pump", "sol", "usdc"],
            "tron" => ["sunswap", "trx", "usdt", "usdc", "tron"],
            _ => []
        };
    }

    private static IReadOnlyList<string> GetLaunchScoutSearchQueries(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "bsc" => ["pancakeswap", "wbnb", "bnb", "usdt", "usdc", "busd", "cake", "pepe", "ai", "inu", "meme", "moon", "cat", "doge", "pump", "launch", "new", "bsc"],
            "ethereum" => ["uniswap", "weth", "eth", "usdt", "usdc", "pepe", "ai", "inu", "meme", "launch", "new"],
            "base" => ["aerodrome", "base", "weth", "usdc", "meme", "ai", "launch", "new"],
            "solana" => ["raydium", "pump", "sol", "usdc", "meme", "ai", "new", "launch"],
            "tron" => ["sunswap", "trx", "usdt", "usdc", "meme", "ai", "launch", "new", "tron"],
            _ => []
        };
    }

    private static IReadOnlyList<string> GetMomentumScoutSearchQueries(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "bsc" => ["pancakeswap", "wbnb", "usdt", "usdc", "trending", "gainers", "pump", "moonshot"],
            "ethereum" => ["uniswap", "weth", "usdt", "usdc", "trending", "gainers", "launch", "new"],
            "base" => ["aerodrome", "base", "weth", "usdc", "trending", "launch", "new", "meme"],
            "solana" => ["raydium", "pump", "sol", "usdc", "trending", "new", "launch", "boost"],
            "tron" => ["sunswap", "trx", "usdt", "usdc", "trending", "gainers", "launch", "new"],
            _ => []
        };
    }

    private static IReadOnlyList<string> GetNarrativeScoutSearchQueries(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "bsc" => ["meme", "ai", "cat", "doge", "elon", "frog", "moon", "gem"],
            "ethereum" => ["meme", "ai", "pepe", "frog", "cat", "doge", "gem", "microcap"],
            "base" => ["meme", "ai", "base", "builder", "cat", "doge", "viral", "gem"],
            "solana" => ["pump", "meme", "ai", "cat", "dog", "moon", "viral", "gem"],
            "tron" => ["tron", "sun", "meme", "ai", "cat", "doge", "viral", "gem"],
            _ => []
        };
    }

    private static IReadOnlyList<string> GetQuoteRouteScoutSearchQueries(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "bsc" => ["wbnb", "bnb", "usdt", "usdc", "busd", "pancakeswap"],
            "ethereum" => ["weth", "eth", "usdt", "usdc", "uniswap", "sushiswap"],
            "base" => ["weth", "eth", "usdc", "aerodrome", "base", "uniswap"],
            "solana" => ["sol", "wsol", "usdc", "raydium", "jupiter", "pump"],
            "tron" => ["trx", "wtrx", "usdt", "usdc", "sunswap", "tron"],
            _ => []
        };
    }

    private static string GetPreferredDexId(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "bsc" => "pancakeswap",
            "ethereum" => "uniswap",
            "base" => "aerodrome",
            "solana" => "raydium",
            "tron" => "sunswap",
            _ => string.Empty
        };
    }

    private static IEnumerable<List<string>> Batch(IReadOnlyList<string> items, int batchSize)
    {
        for (var index = 0; index < items.Count; index += batchSize)
        {
            var count = Math.Min(batchSize, items.Count - index);
            var batch = new List<string>(count);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                batch.Add(items[index + itemIndex]);
            }

            yield return batch;
        }
    }

    private sealed class TokenProfileDto
    {
        public string? ChainId { get; set; }
        public string? TokenAddress { get; set; }
    }

    private sealed class SearchResponseDto
    {
        public List<PairDto>? Pairs { get; set; }
    }

    private sealed class PairDto
    {
        public string? ChainId { get; set; }
        public string? DexId { get; set; }
        public string? Url { get; set; }
        public string? PairAddress { get; set; }
        public string? PriceNative { get; set; }
        public string? PriceUsd { get; set; }
        public TokenDto? BaseToken { get; set; }
        public TokenDto? QuoteToken { get; set; }
        public PriceChangeDto? PriceChange { get; set; }
        public VolumeDto? Volume { get; set; }
        public LiquidityDto? Liquidity { get; set; }
        public decimal? Fdv { get; set; }
        public decimal? MarketCap { get; set; }
    }

    private sealed class TokenDto
    {
        public string? Address { get; set; }
        public string? Name { get; set; }
        public string? Symbol { get; set; }
    }

    private sealed class PriceChangeDto
    {
        public decimal? M5 { get; set; }
        public decimal? H1 { get; set; }
        public decimal? H24 { get; set; }
    }

    private sealed class VolumeDto
    {
        public decimal? H24 { get; set; }
    }

    private sealed class LiquidityDto
    {
        public decimal? Usd { get; set; }
    }
}
