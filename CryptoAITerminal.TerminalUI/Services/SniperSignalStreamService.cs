using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperSignalStreamService
{
    private static readonly TimeSpan UiHeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DexSnapshotInterval = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan LaunchScoutInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MomentumScoutInterval = TimeSpan.FromMilliseconds(1700);
    private static readonly TimeSpan NarrativeScoutInterval = TimeSpan.FromMilliseconds(2300);
    private static readonly TimeSpan QuoteRouteScoutInterval = TimeSpan.FromMilliseconds(2800);
    private static readonly TimeSpan DexMaxFailureBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EvmPollInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan BscEvmPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EvmCooldownAfterRpcLimits = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan SolanaPollInterval = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan EnrichmentInterval = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan PendingLaunchTtl = TimeSpan.FromMinutes(8);
    private const int SnapshotRefreshModulo = 3;
    private const int MaxTokensPerChain = 140;
    private const int LaunchScoutMaxTokensPerChain = 220;
    private const int MomentumScoutMaxTokensPerChain = 180;
    private const int NarrativeScoutMaxTokensPerChain = 180;
    private const int QuoteRouteScoutMaxTokensPerChain = 180;
    private const int EvmRpcFailureThreshold = 3;

    private static readonly IReadOnlyList<EvmFactoryMonitorDefinition> EvmDefinitions =
    [
        new(
            "bsc",
            "pancakeswap",
            "BNB",
            "https://bsc-dataseed.binance.org/",
            "0xcA143Ce32Fe78f1f7019d7d551a6402fC5350c73",
            "0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c",
            EvmFactoryEventKind.PairCreated,
            MaxBlockSpan: 1),
        new(
            "ethereum",
            "uniswap",
            "ETH",
            "https://ethereum-rpc.publicnode.com",
            "0x5C69bEe701ef814a2B6a3EDD4B1652CB9cc5aA6f",
            "0xC02aaA39b223FE8D0A0E5C4F27eAD9083C756Cc2",
            EvmFactoryEventKind.PairCreated,
            MaxBlockSpan: 12),
        new(
            "base",
            "aerodrome",
            "ETH",
            "https://mainnet.base.org",
            "0x420DD381b31aEf6683db6B902084cB0FFECe40Da",
            "0x4200000000000000000000000000000000000006",
            EvmFactoryEventKind.PoolCreated,
            MaxBlockSpan: 12)
    ];

    private static readonly string PumpGraduationRpcUrl = "https://api.mainnet-beta.solana.com";

    private static readonly IReadOnlyList<MempoolMonitorDefinition> MempoolDefinitions =
    [
        new(
            "ethereum",
            "https://ethereum-rpc.publicnode.com",
            [
                "0x7a250d5630B4cF539739dF2C5dAcb4c659F2488D", // Uniswap V2
                "0x68b3465833fb72A70ecDF485E0e4C7bD8665Fc45"  // Uniswap V3 SwapRouter02
            ]),
        new(
            "base",
            "https://mainnet.base.org",
            [
                "0x4752ba5DBc23f44D87826276BF6Fd6b1C372aD24", // Uniswap V2
                "0xcF77a3Ba9A5CA399B7c97c74d54e5b9F59C5c10",  // Aerodrome
                "0x2626664c2603336E57B271c5C0b26F421741e481"  // Uniswap V3
            ]),
    ];

    private static readonly IReadOnlyList<SolanaProgramMonitorDefinition> SolanaDefinitions =
    [
        new(
            "solana",
            "raydium-launchlab",
            "LanMV9sAd7wArD4vJFi2qDdfnVhFxYSUg6eADduJ3uj"),
        new(
            "solana",
            "raydium-cpmm",
            "CPMMoo8L3F4NbTegBCKVNunggL7H1ZpdTHKxQB5qKP1C"),
        new(
            "solana",
            "orca-whirlpool",
            "whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc"),
        new(
            "solana",
            "pump-program",
            "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P")
    ];

    private readonly DexScreenerClient _dexClient;

    public SniperSignalStreamService(DexScreenerClient dexClient)
    {
        _dexClient = dexClient;
    }

    public ChannelReader<SniperSignalMessage> Start(IEnumerable<string> chainIds, CancellationToken cancellationToken)
    {
        var normalizedChains = chainIds
            .Where(static chainId => !string.IsNullOrWhiteSpace(chainId))
            .Select(static chainId => chainId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var channel = Channel.CreateUnbounded<SniperSignalMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _ = RunAsync(channel.Writer, normalizedChains, cancellationToken);
        return channel.Reader;
    }

    private async Task RunAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        CancellationToken cancellationToken)
    {
        var indexedTokenKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var pendingLaunches = new ConcurrentDictionary<string, PendingLaunchSignal>(StringComparer.OrdinalIgnoreCase);
        var sourceStates = new ConcurrentDictionary<string, SignalSourceState>(StringComparer.OrdinalIgnoreCase);
        var sourceTasks = new List<Task>
        {
            RunDexSnapshotLoopAsync(writer, chainIds, indexedTokenKeys, pendingLaunches, sourceStates, cancellationToken),
            RunLaunchScoutLoopAsync(writer, chainIds, indexedTokenKeys, sourceStates, cancellationToken),
            RunMomentumScoutLoopAsync(writer, chainIds, indexedTokenKeys, sourceStates, cancellationToken),
            RunNarrativeScoutLoopAsync(writer, chainIds, indexedTokenKeys, sourceStates, cancellationToken),
            RunQuoteRouteScoutLoopAsync(writer, chainIds, indexedTokenKeys, sourceStates, cancellationToken),
            RunPendingEnrichmentLoopAsync(writer, indexedTokenKeys, pendingLaunches, sourceStates, cancellationToken)
        };

        sourceTasks.AddRange(
            EvmDefinitions
                .Where(ShouldRunOnChainMonitor)
                .Where(definition => chainIds.Contains(definition.ChainId, StringComparer.OrdinalIgnoreCase))
                .Select(definition => RunEvmMonitorLoopAsync(writer, definition, pendingLaunches, cancellationToken)));
        sourceTasks.AddRange(
            SolanaDefinitions
                .Where(definition => chainIds.Contains(definition.ChainId, StringComparer.OrdinalIgnoreCase))
                .Select(definition => RunSolanaMonitorLoopAsync(writer, definition, indexedTokenKeys, sourceStates, cancellationToken)));
        sourceTasks.AddRange(
            MempoolDefinitions
                .Where(def => chainIds.Contains(def.ChainId, StringComparer.OrdinalIgnoreCase))
                .Select(def => RunMempoolMonitorLoopAsync(writer, def, pendingLaunches, cancellationToken)));

        if (chainIds.Contains("solana", StringComparer.OrdinalIgnoreCase))
        {
            sourceTasks.Add(RunPumpGraduationLoopAsync(writer, pendingLaunches, cancellationToken));
        }

        try
        {
            await Task.WhenAll(sourceTasks);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task RunDexSnapshotLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, PendingLaunchSignal> pendingLaunches,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        var warm = true;
        var failureBackoff = DexSnapshotInterval;
        var iteration = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = (await _dexClient.GetLatestTokensAsync(chainIds, MaxTokensPerChain, cancellationToken))
                    .Where(static token => token.LiquidityUsd > 0m && !string.IsNullOrWhiteSpace(token.TokenAddress))
                    .ToList();
                ApplySourceState(snapshot, sourceStates, "indexed", "Indexed snapshot");

                if (warm)
                {
                    foreach (var token in snapshot)
                    {
                        indexedTokenKeys.TryAdd(BuildTokenKey(token.ChainId, token.TokenAddress), 0);
                    }

                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.WarmSnapshot,
                            snapshot,
                            $"Hybrid warm cache primed with {snapshot.Count} indexed pairs.",
                            SourceKind: "indexed",
                            SourceLabel: "Indexed snapshot"),
                        cancellationToken);
                    warm = false;
                }
                else
                {
                    var discovered = new List<DexTokenInfo>();
                    foreach (var token in snapshot)
                    {
                        var tokenKey = BuildTokenKey(token.ChainId, token.TokenAddress);
                        if (indexedTokenKeys.TryAdd(tokenKey, 0))
                        {
                            pendingLaunches.TryRemove(tokenKey, out _);
                            discovered.Add(token);
                        }
                    }

                    if (discovered.Count > 0)
                    {
                        await writer.WriteAsync(
                            new SniperSignalMessage(
                                SniperSignalMessageKind.FreshBatch,
                                discovered,
                                $"Indexed {discovered.Count} fresh pair(s) from the market-data layer.",
                                SourceKind: "indexed",
                                SourceLabel: "Indexed snapshot"),
                            cancellationToken);
                    }

                    if (iteration % SnapshotRefreshModulo == 0 || discovered.Count > 0)
                    {
                        await writer.WriteAsync(
                            new SniperSignalMessage(
                                SniperSignalMessageKind.SnapshotRefresh,
                                snapshot,
                                discovered.Count > 0
                                    ? "Snapshot refreshed after new listings."
                                    : "Snapshot heartbeat refreshed from the market-data layer.",
                                SourceKind: "indexed",
                                SourceLabel: "Indexed snapshot"),
                            cancellationToken);
                    }
                }

                failureBackoff = DexSnapshotInterval;
                iteration++;
                await Task.Delay(DexSnapshotInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"Market-data layer degraded: {ex.Message}",
                        failureBackoff,
                        SourceKind: "indexed",
                        SourceLabel: "Indexed snapshot"),
                    cancellationToken);

                await Task.Delay(failureBackoff, cancellationToken);
                var nextBackoff = failureBackoff + failureBackoff;
                failureBackoff = nextBackoff <= DexMaxFailureBackoff ? nextBackoff : DexMaxFailureBackoff;
            }
        }
    }

    private async Task RunLaunchScoutLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var scoutSnapshot = (await _dexClient.GetLaunchScoutTokensAsync(chainIds, LaunchScoutMaxTokensPerChain, cancellationToken))
                    .Where(static token => token.LiquidityUsd > 0m && !string.IsNullOrWhiteSpace(token.TokenAddress))
                    .ToList();
                ApplySourceState(scoutSnapshot, sourceStates, "launch-scout", "Launch scout");

                var discovered = new List<DexTokenInfo>();
                foreach (var token in scoutSnapshot)
                {
                    var tokenKey = BuildTokenKey(token.ChainId, token.TokenAddress);
                    if (indexedTokenKeys.TryAdd(tokenKey, 0))
                    {
                        discovered.Add(token);
                    }
                }

                if (discovered.Count > 0)
                {
                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.FreshBatch,
                            discovered,
                            $"Launch scout surfaced {discovered.Count} very fresh pair(s) before the broader indexed layer caught up.",
                            SourceKind: "launch-scout",
                            SourceLabel: "Launch scout"),
                        cancellationToken);
                }

                await Task.Delay(LaunchScoutInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"Launch scout degraded: {ex.Message}",
                        LaunchScoutInterval,
                        SourceKind: "launch-scout",
                        SourceLabel: "Launch scout"),
                    cancellationToken);
                await Task.Delay(LaunchScoutInterval, cancellationToken);
            }
        }
    }

    private Task RunMomentumScoutLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        return RunScoutLoopAsync(
            writer,
            chainIds,
            indexedTokenKeys,
            sourceStates,
            "momentum-scout",
            "Momentum scout",
            MomentumScoutInterval,
            MomentumScoutMaxTokensPerChain,
            ct => _dexClient.GetMomentumScoutTokensAsync(chainIds, MomentumScoutMaxTokensPerChain, ct),
            cancellationToken);
    }

    private Task RunNarrativeScoutLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        return RunScoutLoopAsync(
            writer,
            chainIds,
            indexedTokenKeys,
            sourceStates,
            "narrative-scout",
            "Narrative scout",
            NarrativeScoutInterval,
            NarrativeScoutMaxTokensPerChain,
            ct => _dexClient.GetNarrativeScoutTokensAsync(chainIds, NarrativeScoutMaxTokensPerChain, ct),
            cancellationToken);
    }

    private Task RunQuoteRouteScoutLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        return RunScoutLoopAsync(
            writer,
            chainIds,
            indexedTokenKeys,
            sourceStates,
            "quote-route-scout",
            "Quote-route scout",
            QuoteRouteScoutInterval,
            QuoteRouteScoutMaxTokensPerChain,
            ct => _dexClient.GetQuoteRouteScoutTokensAsync(chainIds, QuoteRouteScoutMaxTokensPerChain, ct),
            cancellationToken);
    }

    private async Task RunScoutLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        IReadOnlyCollection<string> chainIds,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        string sourceKind,
        string sourceLabel,
        TimeSpan pollInterval,
        int maxTokensPerChain,
        Func<CancellationToken, Task<IReadOnlyList<DexTokenInfo>>> fetchAsync,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var scoutSnapshot = (await fetchAsync(cancellationToken))
                    .Where(static token => token.LiquidityUsd > 0m && !string.IsNullOrWhiteSpace(token.TokenAddress))
                    .Take(Math.Max(1, chainIds.Count) * maxTokensPerChain)
                    .ToList();
                ApplySourceState(scoutSnapshot, sourceStates, sourceKind, sourceLabel);

                var discovered = new List<DexTokenInfo>();
                foreach (var token in scoutSnapshot)
                {
                    var tokenKey = BuildTokenKey(token.ChainId, token.TokenAddress);
                    if (indexedTokenKeys.TryAdd(tokenKey, 0))
                    {
                        discovered.Add(token);
                    }
                }

                if (discovered.Count > 0)
                {
                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.FreshBatch,
                            discovered,
                            $"{sourceLabel} surfaced {discovered.Count} additional pair(s) outside the primary indexed flow.",
                            SourceKind: sourceKind,
                            SourceLabel: sourceLabel),
                        cancellationToken);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"{sourceLabel} degraded: {ex.Message}",
                        pollInterval,
                        SourceKind: sourceKind,
                        SourceLabel: sourceLabel),
                    cancellationToken);
                await Task.Delay(pollInterval, cancellationToken);
            }
        }
    }

    private async Task RunEvmMonitorLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        EvmFactoryMonitorDefinition definition,
        ConcurrentDictionary<string, PendingLaunchSignal> pendingLaunches,
        CancellationToken cancellationToken)
    {
        var monitor = new EvmDexFactoryMonitor(definition);
        var consecutiveRpcFailures = 0;
        var isCoolingDown = false;
        var rpcFailureThreshold = definition.ChainId.Equals("bsc", StringComparison.OrdinalIgnoreCase)
            ? 1
            : EvmRpcFailureThreshold;
        var pollInterval = definition.ChainId.Equals("bsc", StringComparison.OrdinalIgnoreCase)
            ? BscEvmPollInterval
            : EvmPollInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var launches = await monitor.PollAsync(cancellationToken);
                if (isCoolingDown)
                {
                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.SourceStatus,
                            Array.Empty<DexTokenInfo>(),
                            $"On-chain {definition.ChainId}/{definition.DexId} detector recovered after RPC cooldown. Fresh launch polling is active again.",
                            SourceKind: "on-chain",
                            SourceLabel: $"{definition.ChainId}/{definition.DexId} on-chain"),
                        cancellationToken);
                    isCoolingDown = false;
                }

                consecutiveRpcFailures = 0;
                if (launches.Count > 0)
                {
                    foreach (var launch in launches)
                    {
                        var tokenKey = BuildTokenKey(launch.ChainId, launch.TokenAddress);
                        pendingLaunches[tokenKey] = new PendingLaunchSignal(
                            launch,
                            DateTime.UtcNow,
                            AttemptCount: 0,
                            SourceLabel: $"{definition.ChainId}/{definition.DexId} on-chain");
                    }

                    var byDex = launches
                        .GroupBy(static launch => $"{launch.ChainId}:{launch.DexId}", StringComparer.OrdinalIgnoreCase)
                        .Select(static group => $"{group.Key} x{group.Count()}")
                        .ToArray();
                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.SourceStatus,
                            launches.Select(static launch => launch.ToPlaceholderToken()).ToList(),
                            $"On-chain launch detector saw {launches.Count} new pool event(s): {string.Join(", ", byDex)}.",
                            SourceKind: "on-chain",
                            SourceLabel: $"{definition.ChainId}/{definition.DexId} on-chain"),
                        cancellationToken);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (IsRpcThrottleOrTimeout(ex))
                {
                    consecutiveRpcFailures++;
                    if (consecutiveRpcFailures >= rpcFailureThreshold)
                    {
                        isCoolingDown = true;
                        await writer.WriteAsync(
                            new SniperSignalMessage(
                                SniperSignalMessageKind.SourceStatus,
                                Array.Empty<DexTokenInfo>(),
                                $"On-chain {definition.ChainId}/{definition.DexId} detector is pausing for {EvmCooldownAfterRpcLimits.TotalSeconds:0}s because the public RPC is rate-limiting log scans. Market-data fallback remains active, so token tracking continues.",
                                SourceKind: "on-chain",
                                SourceLabel: $"{definition.ChainId}/{definition.DexId} on-chain"),
                            cancellationToken);
                        await Task.Delay(EvmCooldownAfterRpcLimits, cancellationToken);
                        consecutiveRpcFailures = 0;
                        continue;
                    }

                    await Task.Delay(pollInterval, cancellationToken);
                    continue;
                }
                else
                {
                    consecutiveRpcFailures = 0;
                }

                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"On-chain {definition.ChainId}/{definition.DexId} detector failed: {ex.Message}",
                        pollInterval,
                        SourceKind: "on-chain",
                        SourceLabel: $"{definition.ChainId}/{definition.DexId} on-chain"),
                    cancellationToken);
                await Task.Delay(pollInterval, cancellationToken);
            }
        }
    }

    private async Task RunPendingEnrichmentLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, PendingLaunchSignal> pendingLaunches,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PruneExpiredPendingSignals(pendingLaunches);
                var pendingByChain = pendingLaunches.Values
                    .GroupBy(static pending => pending.Signal.ChainId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var chainGroup in pendingByChain)
                {
                    var tokens = chainGroup
                        .Select(static pending => pending.Signal.TokenAddress)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (tokens.Count == 0)
                    {
                        continue;
                    }

                    var enriched = await _dexClient.GetTokensByAddressesAsync(chainGroup.Key, tokens, cancellationToken);
                    ApplySourceState(enriched, sourceStates, "launch-enriched", $"{chainGroup.Key} launch enrichment");
                    var fresh = new List<DexTokenInfo>();
                    foreach (var token in enriched)
                    {
                        var tokenKey = BuildTokenKey(token.ChainId, token.TokenAddress);
                        if (indexedTokenKeys.TryAdd(tokenKey, 0))
                        {
                            fresh.Add(token);
                        }

                        pendingLaunches.TryRemove(tokenKey, out _);
                    }

                    foreach (var unresolved in chainGroup)
                    {
                        var tokenKey = BuildTokenKey(unresolved.Signal.ChainId, unresolved.Signal.TokenAddress);
                        if (pendingLaunches.TryGetValue(tokenKey, out var existing))
                        {
                            pendingLaunches[tokenKey] = existing with { AttemptCount = existing.AttemptCount + 1 };
                        }
                    }

                    if (fresh.Count > 0)
                    {
                        await writer.WriteAsync(
                            new SniperSignalMessage(
                                SniperSignalMessageKind.FreshBatch,
                                fresh,
                                $"On-chain launches enriched into {fresh.Count} actionable pair(s).",
                                SourceKind: "launch-enriched",
                                SourceLabel: $"{chainGroup.Key} launch enrichment"),
                            cancellationToken);
                    }
                }

                await Task.Delay(EnrichmentInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"Signal enrichment failed: {ex.Message}",
                        EnrichmentInterval,
                        SourceKind: "launch-enriched",
                        SourceLabel: "Launch enrichment"),
                    cancellationToken);
                await Task.Delay(EnrichmentInterval, cancellationToken);
            }
        }
    }

    private async Task RunSolanaMonitorLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        SolanaProgramMonitorDefinition definition,
        ConcurrentDictionary<string, byte> indexedTokenKeys,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        CancellationToken cancellationToken)
    {
        using var monitor = new SolanaProgramActivityMonitor(definition);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signals = await monitor.PollAsync(cancellationToken);
                if (signals.Count > 0)
                {
                    var directCandidates = signals
                        .SelectMany(static signal => signal.TokenCandidates)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.SourceStatus,
                            Array.Empty<DexTokenInfo>(),
                            directCandidates.Count > 0
                                ? $"Solana activity detector saw {signals.Count} new signature(s) on {definition.SourceLabel}. Parsed {directCandidates.Count} direct mint candidate(s)."
                                : $"Solana activity detector saw {signals.Count} new signature(s) on {definition.SourceLabel}. No direct mint was parsed, triggering accelerated refresh.",
                            SourceKind: "solana-activity",
                            SourceLabel: definition.SourceLabel),
                        cancellationToken);

                    List<DexTokenInfo> fresh;
                    if (directCandidates.Count > 0)
                    {
                        fresh = (await _dexClient.GetTokensByAddressesAsync("solana", directCandidates, cancellationToken))
                            .Where(static token => token.LiquidityUsd > 0m && !string.IsNullOrWhiteSpace(token.TokenAddress))
                            .Where(token => indexedTokenKeys.TryAdd(BuildTokenKey(token.ChainId, token.TokenAddress), 0))
                            .ToList();
                        ApplySourceState(fresh, sourceStates, "solana-direct", definition.SourceLabel);
                    }
                    else
                    {
                        var snapshot = await _dexClient.GetLatestTokensAsync(["solana"], MaxTokensPerChain, cancellationToken);
                        fresh = snapshot
                            .Where(static token => token.LiquidityUsd > 0m && !string.IsNullOrWhiteSpace(token.TokenAddress))
                            .Where(token => indexedTokenKeys.TryAdd(BuildTokenKey(token.ChainId, token.TokenAddress), 0))
                            .ToList();
                        ApplySourceState(fresh, sourceStates, "solana-indexed", definition.SourceLabel);
                    }

                    if (fresh.Count > 0)
                    {
                        await writer.WriteAsync(
                            new SniperSignalMessage(
                                SniperSignalMessageKind.FreshBatch,
                                fresh,
                                directCandidates.Count > 0
                                    ? $"Solana on-chain parsing on {definition.SourceLabel} surfaced {fresh.Count} fresh token(s) from direct mint candidates."
                                    : $"Solana on-chain activity on {definition.SourceLabel} surfaced {fresh.Count} fresh indexed token(s).",
                                SourceKind: directCandidates.Count > 0 ? "solana-direct" : "solana-indexed",
                                SourceLabel: definition.SourceLabel),
                            cancellationToken);
                    }
                    else
                    {
                        await writer.WriteAsync(
                            new SniperSignalMessage(
                                SniperSignalMessageKind.SourceStatus,
                                Array.Empty<DexTokenInfo>(),
                                directCandidates.Count > 0
                                    ? $"Solana direct parsing on {definition.SourceLabel} found mint candidates, but no fresh indexed token was materialized yet."
                                    : $"Solana activity on {definition.SourceLabel} was detected, but no new indexed token was materialized yet.",
                                SourceKind: "solana-activity",
                                SourceLabel: definition.SourceLabel),
                            cancellationToken);
                    }
                }

                await Task.Delay(SolanaPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"Solana {definition.SourceLabel} detector failed: {ex.Message}",
                        SolanaPollInterval,
                        SourceKind: "solana-activity",
                        SourceLabel: definition.SourceLabel),
                    cancellationToken);
                await Task.Delay(SolanaPollInterval, cancellationToken);
            }
        }
    }

    public static TimeSpan GetUiHeartbeatInterval()
    {
        return UiHeartbeatInterval;
    }

    private static string BuildTokenKey(string? chainId, string? tokenAddress)
    {
        return $"{chainId?.Trim().ToLowerInvariant()}::{tokenAddress?.Trim().ToLowerInvariant()}";
    }

    private static void ApplySourceState(
        IEnumerable<DexTokenInfo> tokens,
        ConcurrentDictionary<string, SignalSourceState> sourceStates,
        string sourceKind,
        string sourceLabel)
    {
        foreach (var token in tokens)
        {
            var tokenKey = BuildTokenKey(token.ChainId, token.TokenAddress);
            var updated = sourceStates.AddOrUpdate(
                tokenKey,
                _ => SignalSourceState.Create(sourceKind, sourceLabel),
                (_, existing) => existing.Register(sourceKind, sourceLabel));

            token.SignalSourceKind = updated.PrimaryKind;
            token.SignalSourceLabel = updated.BuildSummary();
            token.SignalSourceCount = updated.Count;
            token.SignalConfirmationLabel = updated.Count > 1
                ? $"Multi-source confirmed ({updated.Count})"
                : $"Single-source via {updated.PrimaryLabel}";
        }
    }

    private static bool ShouldRunOnChainMonitor(EvmFactoryMonitorDefinition definition)
    {
        // Public BSC RPC endpoints are too aggressively rate-limited for repeated eth_getLogs polling.
        // Keep BSC tracking on the indexed market-data layer unless a stronger private RPC is wired in.
        return !(definition.ChainId.Equals("bsc", StringComparison.OrdinalIgnoreCase) &&
                 definition.RpcUrl.Contains("bsc-dataseed.binance.org", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRpcThrottleOrTimeout(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("eth_getLogs", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rpc timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }

    private static void PruneExpiredPendingSignals(ConcurrentDictionary<string, PendingLaunchSignal> pendingLaunches)
    {
        var deadline = DateTime.UtcNow - PendingLaunchTtl;
        foreach (var item in pendingLaunches)
        {
            if (item.Value.FirstSeenUtc < deadline)
            {
                pendingLaunches.TryRemove(item.Key, out _);
            }
        }
    }

    private sealed record PendingLaunchSignal(
        EvmDexLaunchSignal Signal,
        DateTime FirstSeenUtc,
        int AttemptCount,
        string SourceLabel);

    private async Task RunPumpGraduationLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        ConcurrentDictionary<string, PendingLaunchSignal> pendingLaunches,
        CancellationToken cancellationToken)
    {
        using var monitor = new SolanaPumpGraduationMonitor(PumpGraduationRpcUrl);
        var pollInterval = TimeSpan.FromMilliseconds(1400);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var graduations = await monitor.PollAsync(cancellationToken);
                foreach (var grad in graduations)
                {
                    var tokenKey = BuildTokenKey("solana", grad.TokenMint);
                    if (pendingLaunches.ContainsKey(tokenKey)) continue;

                    var placeholder = monitor.ToPlaceholderToken(grad);
                    pendingLaunches[tokenKey] = new PendingLaunchSignal(
                        new EvmDexLaunchSignal("solana", "raydium-cpmm", grad.TokenMint, string.Empty, "SOL", false, 0),
                        DateTime.UtcNow,
                        AttemptCount: 0,
                        SourceLabel: "pump.fun graduation");

                    await writer.WriteAsync(
                        new SniperSignalMessage(
                            SniperSignalMessageKind.FreshBatch,
                            new[] { placeholder },
                            $"🎓 pump.fun GRADUATION: {grad.ShortMint} → Raydium pool ({grad.AgeLabel}). Tx: {grad.Signature[..8]}...",
                            SourceKind: "pump-graduation",
                            SourceLabel: "pump.fun graduation"),
                        cancellationToken);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    new SniperSignalMessage(
                        SniperSignalMessageKind.Fault,
                        Array.Empty<DexTokenInfo>(),
                        $"pump.fun graduation monitor failed: {ex.Message}",
                        pollInterval,
                        SourceKind: "pump-graduation",
                        SourceLabel: "pump.fun graduation"),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task RunMempoolMonitorLoopAsync(
        ChannelWriter<SniperSignalMessage> writer,
        MempoolMonitorDefinition definition,
        ConcurrentDictionary<string, PendingLaunchSignal> pendingLaunches,
        CancellationToken cancellationToken)
    {
        var monitor = new EvmMempoolMonitor(
            definition.ChainId,
            definition.RpcUrl,
            definition.RouterAddresses);

        var mempoolChannel = Channel.CreateBounded<MempoolLiquiditySignal>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

        _ = monitor.RunAsync(mempoolChannel.Writer, cancellationToken);

        await foreach (var signal in mempoolChannel.Reader.ReadAllAsync(cancellationToken))
        {
            var tokenKey = BuildTokenKey(signal.ChainId, signal.TokenAddress);

            if (pendingLaunches.ContainsKey(tokenKey)) continue;

            pendingLaunches[tokenKey] = new PendingLaunchSignal(
                new EvmDexLaunchSignal(signal.ChainId, "mempool", signal.TokenAddress, string.Empty, "ETH", false, 0),
                DateTime.UtcNow,
                AttemptCount: 0,
                SourceLabel: $"{signal.ChainId} mempool");

            await writer.WriteAsync(
                new SniperSignalMessage(
                    SniperSignalMessageKind.SourceStatus,
                    new[] { signal.ToPlaceholderToken() },
                    $"Mempool detected AddLiquidity for {signal.TokenAddress} on {signal.ChainId} (tx {signal.TxHash[..10]}..., {signal.NativeValueEth:F4} ETH, {signal.GasPriceGwei:F1} Gwei). Enrichment pending.",
                    SourceKind: "mempool",
                    SourceLabel: $"{signal.ChainId} mempool"),
                cancellationToken);
        }
    }

    private sealed record SignalSourceState(
        string PrimaryKind,
        string PrimaryLabel,
        HashSet<string> SeenKeys)
    {
        public int Count => SeenKeys.Count;

        public static SignalSourceState Create(string sourceKind, string sourceLabel)
        {
            return new SignalSourceState(sourceKind, sourceLabel, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"{sourceKind}::{sourceLabel}"
            });
        }

        public SignalSourceState Register(string sourceKind, string sourceLabel)
        {
            var updated = new HashSet<string>(SeenKeys, StringComparer.OrdinalIgnoreCase)
            {
                $"{sourceKind}::{sourceLabel}"
            };
            return this with { SeenKeys = updated };
        }

        public string BuildSummary()
        {
            return Count > 1
                ? $"{PrimaryLabel} + {Count - 1} more source(s)"
                : PrimaryLabel;
        }
    }
}

public enum SniperSignalMessageKind
{
    WarmSnapshot,
    SnapshotRefresh,
    FreshBatch,
    SourceStatus,
    Fault
}

public sealed record SniperSignalMessage(
    SniperSignalMessageKind Kind,
    IReadOnlyList<DexTokenInfo> Tokens,
    string Message,
    TimeSpan? RetryDelay = null,
    string SourceKind = "indexed",
    string SourceLabel = "Indexed snapshot");

public sealed record MempoolMonitorDefinition(
    string ChainId,
    string RpcUrl,
    IReadOnlyList<string> RouterAddresses);
