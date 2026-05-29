using System.Numerics;
using CryptoAITerminal.Core.Models;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace CryptoAITerminal.Gateway.DEX;

[Event("PairCreated")]
public sealed class PairCreatedEventDto : IEventDTO
{
    [Parameter("address", "token0", 1, true)]
    public string Token0 { get; set; } = string.Empty;

    [Parameter("address", "token1", 2, true)]
    public string Token1 { get; set; } = string.Empty;

    [Parameter("address", "pair", 3, false)]
    public string Pair { get; set; } = string.Empty;

    [Parameter("uint256", "", 4, false)]
    public BigInteger PairIndex { get; set; }
}

[Event("PoolCreated")]
public sealed class AerodromePoolCreatedEventDto : IEventDTO
{
    [Parameter("address", "token0", 1, true)]
    public string Token0 { get; set; } = string.Empty;

    [Parameter("address", "token1", 2, true)]
    public string Token1 { get; set; } = string.Empty;

    [Parameter("bool", "stable", 3, false)]
    public bool Stable { get; set; }

    [Parameter("address", "pool", 4, false)]
    public string Pool { get; set; } = string.Empty;

    [Parameter("uint256", "", 5, false)]
    public BigInteger PoolIndex { get; set; }
}

public sealed class EvmDexFactoryMonitor
{
    private readonly Web3 _web3;
    private readonly EvmFactoryMonitorDefinition _definition;
    private BigInteger? _lastProcessedBlock;

    public EvmDexFactoryMonitor(EvmFactoryMonitorDefinition definition)
    {
        _definition = definition;
        _web3 = new Web3(definition.RpcUrl);
    }

    public string ChainId => _definition.ChainId;
    public string DexId => _definition.DexId;

    public async Task<IReadOnlyList<EvmDexLaunchSignal>> PollAsync(CancellationToken cancellationToken = default)
    {
        var latestBlockNumber = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        if (_lastProcessedBlock is null)
        {
            _lastProcessedBlock = latestBlockNumber.Value;
            return [];
        }

        if (latestBlockNumber.Value <= _lastProcessedBlock.Value)
        {
            return [];
        }

        var nextBlock = _lastProcessedBlock.Value + 1;
        var maxBlockSpan = Math.Max(1, _definition.MaxBlockSpan);
        var cappedToBlock = BigInteger.Min(latestBlockNumber.Value, nextBlock + (maxBlockSpan - 1));
        var fromBlock = new BlockParameter(new HexBigInteger(nextBlock));
        var toBlock = new BlockParameter(new HexBigInteger(cappedToBlock));
        IReadOnlyList<EvmDexLaunchSignal> launches = _definition.EventKind switch
        {
            EvmFactoryEventKind.PairCreated => await PollPairCreatedAsync(fromBlock, toBlock),
            EvmFactoryEventKind.PoolCreated => await PollPoolCreatedAsync(fromBlock, toBlock),
            _ => []
        };

        _lastProcessedBlock = cappedToBlock;
        return launches;
    }

    private async Task<IReadOnlyList<EvmDexLaunchSignal>> PollPairCreatedAsync(BlockParameter fromBlock, BlockParameter toBlock)
    {
        var handler = _web3.Eth.GetEvent<PairCreatedEventDto>(_definition.FactoryAddress);
        var filter = handler.CreateFilterInput(fromBlock, toBlock);
        var changes = await handler.GetAllChangesAsync(filter);

        return changes
            .Select(change => BuildSignal(
                change.Event.Token0,
                change.Event.Token1,
                change.Event.Pair,
                false,
                change.Log.BlockNumber?.Value ?? BigInteger.Zero))
            .Where(static signal => signal is not null)
            .Select(static signal => signal!)
            .ToList();
    }

    private async Task<IReadOnlyList<EvmDexLaunchSignal>> PollPoolCreatedAsync(BlockParameter fromBlock, BlockParameter toBlock)
    {
        var handler = _web3.Eth.GetEvent<AerodromePoolCreatedEventDto>(_definition.FactoryAddress);
        var filter = handler.CreateFilterInput(fromBlock, toBlock);
        var changes = await handler.GetAllChangesAsync(filter);

        return changes
            .Select(change => BuildSignal(
                change.Event.Token0,
                change.Event.Token1,
                change.Event.Pool,
                change.Event.Stable,
                change.Log.BlockNumber?.Value ?? BigInteger.Zero))
            .Where(static signal => signal is not null)
            .Select(static signal => signal!)
            .ToList();
    }

    private EvmDexLaunchSignal? BuildSignal(
        string token0,
        string token1,
        string pairAddress,
        bool stablePool,
        BigInteger blockNumber)
    {
        if (string.IsNullOrWhiteSpace(token0) ||
            string.IsNullOrWhiteSpace(token1) ||
            string.IsNullOrWhiteSpace(pairAddress))
        {
            return null;
        }

        var token0IsWrapped = token0.Equals(_definition.WrappedNativeAddress, StringComparison.OrdinalIgnoreCase);
        var token1IsWrapped = token1.Equals(_definition.WrappedNativeAddress, StringComparison.OrdinalIgnoreCase);
        if (!token0IsWrapped && !token1IsWrapped)
        {
            return null;
        }

        var launchedToken = token0IsWrapped ? token1 : token0;
        return new EvmDexLaunchSignal(
            _definition.ChainId,
            _definition.DexId,
            launchedToken,
            pairAddress,
            _definition.NativeSymbol,
            stablePool,
            blockNumber);
    }
}

public sealed record EvmFactoryMonitorDefinition(
    string ChainId,
    string DexId,
    string NativeSymbol,
    string RpcUrl,
    string FactoryAddress,
    string WrappedNativeAddress,
    EvmFactoryEventKind EventKind,
    int MaxBlockSpan = 25);

public enum EvmFactoryEventKind
{
    PairCreated,
    PoolCreated
}

public sealed record EvmDexLaunchSignal(
    string ChainId,
    string DexId,
    string TokenAddress,
    string PairAddress,
    string QuoteSymbol,
    bool StablePool,
    BigInteger BlockNumber)
{
    public DexTokenInfo ToPlaceholderToken()
    {
        return new DexTokenInfo
        {
            ChainId = ChainId,
            DexId = DexId,
            PairAddress = PairAddress,
            TokenAddress = TokenAddress,
            QuoteSymbol = QuoteSymbol,
            Symbol = string.Empty,
            Name = string.Empty,
            Url = string.Empty,
            LastUpdatedUtc = DateTime.UtcNow
        };
    }
}
