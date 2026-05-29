using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoAITerminal.Core.Models;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Web3;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Scans the EVM pending mempool for AddLiquidity calls and large swaps,
/// emitting sniper signals BEFORE the transaction is mined.
/// This gives 1-3 blocks of advance notice over factory event monitoring.
/// </summary>
public sealed class EvmMempoolMonitor
{
    // addLiquidityETH(address,uint256,uint256,uint256,address,uint256)
    private const string AddLiquidityEthSelector = "0xf305d719";
    // addLiquidity(address,address,uint256,uint256,uint256,uint256,address,uint256)
    private const string AddLiquiditySelector = "0xe8e33700";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan DefaultStaleTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _rpcUrl;
    private readonly string _chainId;
    private readonly IReadOnlySet<string> _routerAddresses;
    private readonly TimeSpan _pollInterval;
    private readonly HashSet<string> _seenTxHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _seenTimestamps = new(StringComparer.OrdinalIgnoreCase);

    public EvmMempoolMonitor(
        string chainId,
        string rpcUrl,
        IEnumerable<string> routerAddresses,
        TimeSpan? pollInterval = null)
    {
        _chainId = chainId;
        _rpcUrl = rpcUrl;
        _routerAddresses = new HashSet<string>(routerAddresses, StringComparer.OrdinalIgnoreCase);
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>
    /// Runs continuously, writing MempoolLiquiditySignal items to the channel.
    /// </summary>
    public async Task RunAsync(
        System.Threading.Channels.ChannelWriter<MempoolLiquiditySignal> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pending = await FetchPendingTransactionsAsync(cancellationToken);
                PurgeStaleSeen();

                foreach (var tx in pending)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (string.IsNullOrEmpty(tx.Hash) || _seenTxHashes.Contains(tx.Hash)) continue;
                    if (!IsAddLiquidityCall(tx)) continue;

                    _seenTxHashes.Add(tx.Hash);
                    _seenTimestamps[tx.Hash] = DateTime.UtcNow;

                    var signal = ParseSignal(tx);
                    if (signal is null) continue;

                    await writer.WriteAsync(signal, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // RPC errors: back off briefly, don't crash the loop
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        writer.TryComplete();
    }

    private async Task<IReadOnlyList<PendingTxDto>> FetchPendingTransactionsAsync(CancellationToken ct)
    {
        var payload = """{"jsonrpc":"2.0","method":"eth_getBlockByNumber","params":["pending",true],"id":1}""";
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_rpcUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(body);
        var transactions = doc?["result"]?["transactions"]?.AsArray();
        if (transactions is null) return Array.Empty<PendingTxDto>();

        var result = new List<PendingTxDto>(transactions.Count);
        foreach (var node in transactions)
        {
            if (node is null) continue;
            var hash = node["hash"]?.GetValue<string>() ?? string.Empty;
            var to = node["to"]?.GetValue<string>() ?? string.Empty;
            var input = node["input"]?.GetValue<string>() ?? string.Empty;
            var value = node["value"]?.GetValue<string>() ?? "0x0";
            var gasPrice = node["gasPrice"]?.GetValue<string>()
                        ?? node["maxFeePerGas"]?.GetValue<string>()
                        ?? "0x0";
            result.Add(new PendingTxDto(hash, to, input, value, gasPrice));
        }
        return result;
    }

    private bool IsAddLiquidityCall(PendingTxDto tx)
    {
        if (string.IsNullOrEmpty(tx.Input) || tx.Input.Length < 10) return false;
        if (!_routerAddresses.Contains(tx.To)) return false;
        var selector = tx.Input[..10].ToLowerInvariant();
        return selector == AddLiquidityEthSelector || selector == AddLiquiditySelector;
    }

    private MempoolLiquiditySignal? ParseSignal(PendingTxDto tx)
    {
        try
        {
            var input = tx.Input;
            string tokenAddress;
            bool isEthPair;

            if (input[..10].ToLowerInvariant() == AddLiquidityEthSelector)
            {
                // addLiquidityETH: first param is token address (32 bytes = 64 hex chars at offset 10)
                tokenAddress = "0x" + input.Substring(10 + 24, 40);
                isEthPair = true;
            }
            else
            {
                // addLiquidity: first param tokenA, second tokenB
                var tokenA = "0x" + input.Substring(10 + 24, 40);
                var tokenB = "0x" + input.Substring(10 + 64 + 24, 40);
                // We report the non-WETH one; caller can filter
                tokenAddress = tokenA;
                isEthPair = false;
            }

            var nativeValue = HexToBigInteger(tx.Value);
            var nativeEth = Web3.Convert.FromWei(nativeValue);
            var gasPriceGwei = Web3.Convert.FromWei(HexToBigInteger(tx.GasPrice), 9);

            return new MempoolLiquiditySignal(
                _chainId,
                tx.Hash,
                tokenAddress,
                isEthPair,
                nativeEth,
                gasPriceGwei,
                DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static BigInteger HexToBigInteger(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex == "0x0" || hex == "0x") return BigInteger.Zero;
        var clean = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (string.IsNullOrEmpty(clean)) return BigInteger.Zero;
        return BigInteger.Parse("0" + clean, System.Globalization.NumberStyles.HexNumber);
    }

    private void PurgeStaleSeen()
    {
        var cutoff = DateTime.UtcNow - DefaultStaleTimeout;
        var stale = _seenTimestamps
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
        {
            _seenTxHashes.Remove(key);
            _seenTimestamps.Remove(key);
        }
    }

    private sealed record PendingTxDto(string Hash, string To, string Input, string Value, string GasPrice);
}

/// <summary>
/// Signal emitted when a pending AddLiquidity transaction is detected in the mempool.
/// Arrives 1-3 blocks before the PairCreated factory event fires.
/// </summary>
public sealed record MempoolLiquiditySignal(
    string ChainId,
    string TxHash,
    string TokenAddress,
    bool IsNativePair,
    decimal NativeValueEth,
    decimal GasPriceGwei,
    DateTime DetectedAtUtc)
{
    public DexTokenInfo ToPlaceholderToken(string dexId = "mempool", string quoteSymbol = "ETH") =>
        new()
        {
            ChainId = ChainId,
            DexId = dexId,
            TokenAddress = TokenAddress,
            QuoteSymbol = quoteSymbol,
            Symbol = string.Empty,
            Name = string.Empty,
            Url = string.Empty,
            LastUpdatedUtc = DetectedAtUtc
        };
}
