using System.Numerics;
using System.Text.Json.Nodes;
using CryptoAITerminal.Core.Models;
using Nethereum.Web3;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Monitors a list of "whale" wallet addresses on EVM chains.
/// When a watched wallet executes a DEX swap, emits a CopyTradeSignal.
/// Supported: any EVM chain via standard eth_getLogs + Transfer event scanning.
/// </summary>
public sealed class WalletCopyTradeMonitor
{
    // Swap(address,uint256,uint256,uint256,uint256,address) — Uniswap V2 / forks
    private const string SwapEventTopic = "0xd78ad95fa46c994b6551d0da85fc275fe613ce37657fb8d5e3d130840159d822";
    // Transfer(address,address,uint256) — ERC-20 transfer used to detect incoming tokens
    private const string TransferEventTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;
    private readonly string _rpcUrl;
    private readonly string _chainId;
    private readonly IReadOnlyList<string> _watchedWallets;
    private readonly IReadOnlySet<string> _knownRouters;
    private BigInteger _lastBlock = BigInteger.Zero;

    public WalletCopyTradeMonitor(
        string chainId,
        string rpcUrl,
        IEnumerable<string> watchedWallets,
        IEnumerable<string> knownRouterAddresses)
    {
        _chainId = chainId;
        _rpcUrl = rpcUrl;
        _watchedWallets = watchedWallets
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        _knownRouters = new HashSet<string>(
            knownRouterAddresses.Select(r => r.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task RunAsync(
        System.Threading.Channels.ChannelWriter<CopyTradeSignal> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var latest = await GetLatestBlockAsync(cancellationToken);
                if (_lastBlock == BigInteger.Zero)
                {
                    _lastBlock = latest;
                }

                if (latest > _lastBlock)
                {
                    var signals = await ScanBlockRangeAsync(_lastBlock + 1, latest, cancellationToken);
                    foreach (var signal in signals)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        await writer.WriteAsync(signal, cancellationToken);
                    }
                    _lastBlock = latest;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        writer.TryComplete();
    }

    private async Task<IReadOnlyList<CopyTradeSignal>> ScanBlockRangeAsync(
        BigInteger fromBlock,
        BigInteger toBlock,
        CancellationToken ct)
    {
        // Scan for Swap events where the `to` field matches a watched wallet
        var paddedWallets = _watchedWallets
            .Select(w => "0x000000000000000000000000" + w.TrimStart('0', 'x'))
            .ToArray();

        var fromHex = "0x" + fromBlock.ToString("X");
        var toHex = "0x" + toBlock.ToString("X");

        var logsJson = await EthGetLogsAsync(SwapEventTopic, paddedWallets, fromHex, toHex, ct);
        var signals = new List<CopyTradeSignal>();

        foreach (var log in logsJson)
        {
            var contractAddress = log["address"]?.GetValue<string>() ?? string.Empty;
            var txHash = log["transactionHash"]?.GetValue<string>() ?? string.Empty;
            var data = log["data"]?.GetValue<string>() ?? string.Empty;
            var topics = log["topics"]?.AsArray();

            if (string.IsNullOrEmpty(txHash) || string.IsNullOrEmpty(data) || topics is null || topics.Count < 3)
                continue;

            // Topic[1] = sender (who called the router), Topic[2] = to (recipient)
            var recipient = topics[2]?.GetValue<string>() ?? string.Empty;
            if (recipient.Length < 42) continue;
            var recipientAddress = "0x" + recipient[^40..];

            if (!_watchedWallets.Contains(recipientAddress.ToLowerInvariant())) continue;

            // Parse amounts from data: amount0In, amount1In, amount0Out, amount1Out (each 32 bytes)
            if (data.Length < 2 + 8 * 64) continue;
            var dataHex = data[2..]; // strip 0x
            var amount0In  = HexToDecimal(dataHex[..64]);
            var amount1In  = HexToDecimal(dataHex[64..128]);
            var amount0Out = HexToDecimal(dataHex[128..192]);
            var amount1Out = HexToDecimal(dataHex[192..256]);

            // Determine buy vs sell: if amount0Out > 0 the wallet received token0
            var isBuy = amount0Out > 0 || amount1Out > 0;
            var spentAmount = amount0In > amount1In ? amount0In : amount1In;
            var receivedAmount = amount0Out > amount1Out ? amount0Out : amount1Out;

            signals.Add(new CopyTradeSignal(
                _chainId,
                recipientAddress,
                contractAddress,
                txHash,
                isBuy ? CopyTradeDirection.Buy : CopyTradeDirection.Sell,
                spentAmount,
                receivedAmount,
                DateTime.UtcNow));
        }

        return signals;
    }

    private async Task<JsonArray> EthGetLogsAsync(
        string topic0,
        string[] topic2Options,
        string fromBlock,
        string toBlock,
        CancellationToken ct)
    {
        // Build topics array: [topic0, null, [wallet1, wallet2, ...]]
        var topic2Json = "[" + string.Join(",", topic2Options.Select(t => $"\"{t}\"")) + "]";
        var payload = $$"""
            {"jsonrpc":"2.0","method":"eth_getLogs","params":[{
              "fromBlock":"{{fromBlock}}",
              "toBlock":"{{toBlock}}",
              "topics":["{{topic0}}", null, {{topic2Json}}]
            }],"id":1}
            """;

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_rpcUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(body);
        return doc?["result"]?.AsArray() ?? new JsonArray();
    }

    private async Task<BigInteger> GetLatestBlockAsync(CancellationToken ct)
    {
        var payload = """{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}""";
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_rpcUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(body);
        var hex = doc?["result"]?.GetValue<string>() ?? "0x0";
        return BigInteger.Parse("0" + hex.TrimStart('0', 'x'), System.Globalization.NumberStyles.HexNumber);
    }

    private static decimal HexToDecimal(string hex64)
    {
        if (string.IsNullOrWhiteSpace(hex64)) return 0m;
        var big = BigInteger.Parse("0" + hex64.TrimStart('0'), System.Globalization.NumberStyles.HexNumber);
        return (decimal)Web3.Convert.FromWei(big);
    }
}

public enum CopyTradeDirection { Buy, Sell }

/// <summary>
/// Emitted when a watched wallet executes a DEX swap.
/// </summary>
public sealed record CopyTradeSignal(
    string ChainId,
    string WalletAddress,
    string PairContractAddress,
    string TxHash,
    CopyTradeDirection Direction,
    decimal SpentAmountNative,
    decimal ReceivedAmountTokens,
    DateTime DetectedAtUtc)
{
    /// <summary>Minimum native spend (ETH/BNB) to consider this signal significant.</summary>
    public bool IsMeaningful(decimal minNativeSpend = 0.05m) =>
        Direction == CopyTradeDirection.Buy && SpentAmountNative >= minNativeSpend;
}

/// <summary>
/// Configuration for a single wallet to monitor for copy-trading.
/// </summary>
public sealed record WatchedWalletConfig(
    string Address,
    string Label,
    decimal MaxCopyFractionOfPosition = 0.5m,
    decimal MaxCopyNativeAmount = 0.5m,
    bool CopyBuysOnly = true);
