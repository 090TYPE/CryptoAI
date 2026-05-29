using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Single gas snapshot across Ethereum, BSC and Solana.</summary>
public sealed record GasSnapshot
{
    /// <summary>Slow / cheap tier (gwei). 0 = data unavailable.</summary>
    public decimal EthSlow     { get; init; }
    public decimal EthStandard { get; init; }
    public decimal EthFast     { get; init; }

    public decimal BscSlow     { get; init; }
    public decimal BscStandard { get; init; }
    public decimal BscFast     { get; init; }

    /// <summary>Average TPS over the last 4 Solana performance samples.</summary>
    public double SolanaTps  { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Polls Ethereum gas (EIP-1559 fee history via public RPC / Etherscan oracle),
/// BSC gas (eth_gasPrice via public RPC / BSCScan oracle) and
/// Solana TPS (getRecentPerformanceSamples) every <see cref="PollInterval"/>.
/// Raises <see cref="SnapshotReceived"/> on a thread-pool thread — callers
/// must marshal to the UI thread if needed.
/// </summary>
public sealed class GasMonitorService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private CancellationTokenSource? _cts;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fired whenever a fresh snapshot is available (thread-pool thread).</summary>
    public event Action<GasSnapshot>? SnapshotReceived;

    // Optional Etherscan/BSCScan API keys (for higher rate limits via oracle endpoint)
    public string? EtherscanApiKey { get; set; }
    public string? BscscanApiKey  { get; set; }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── poll loop ─────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await FetchAndFireAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }

            await FetchAndFireAsync(ct);
        }
    }

    private async Task FetchAndFireAsync(CancellationToken ct)
    {
        try
        {
            var ethTask = FetchEthGasAsync(ct);
            var bscTask = FetchBscGasAsync(ct);
            var solTask = FetchSolanaTpsAsync(ct);
            await Task.WhenAll(ethTask, bscTask, solTask);

            var (ethSlow, ethStd, ethFast) = ethTask.Result;
            var (bscSlow, bscStd, bscFast) = bscTask.Result;

            SnapshotReceived?.Invoke(new GasSnapshot
            {
                EthSlow     = ethSlow,
                EthStandard = ethStd,
                EthFast     = ethFast,
                BscSlow     = bscSlow,
                BscStandard = bscStd,
                BscFast     = bscFast,
                SolanaTps   = solTask.Result,
                Timestamp   = DateTime.UtcNow
            });
        }
        catch { /* best-effort — never crash the poll loop */ }
    }

    // ── Ethereum gas (EIP-1559 fee history via public RPC; oracle if key set) ─

    private async Task<(decimal slow, decimal std, decimal fast)> FetchEthGasAsync(CancellationToken ct)
    {
        // Use Etherscan oracle when an API key is configured
        if (!string.IsNullOrWhiteSpace(EtherscanApiKey))
        {
            try
            {
                var url  = $"https://api.etherscan.io/api?module=gastracker&action=gasoracle&apikey={EtherscanApiKey}";
                var json = await _http.GetStringAsync(url, ct);
                var node = JsonNode.Parse(json)?["result"];
                var slow = ParseDecimal(node?["SafeGasPrice"]?.GetValue<string>());
                var std  = ParseDecimal(node?["ProposeGasPrice"]?.GetValue<string>());
                var fast = ParseDecimal(node?["FastGasPrice"]?.GetValue<string>());
                if (slow > 0) return (slow, std, fast);
            }
            catch { }
        }

        // Fallback: public Ethereum RPC — eth_feeHistory (EIP-1559)
        try
        {
            const string rpc  = "https://ethereum.publicnode.com";
            const string body = """{"jsonrpc":"2.0","id":1,"method":"eth_feeHistory","params":["0x5","latest",[10,50,90]]}""";
            using var req = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(rpc, req, ct);
            var json   = await res.Content.ReadAsStringAsync(ct);
            var result = JsonNode.Parse(json)?["result"];

            var baseFees = result?["baseFeePerGas"]?.AsArray();
            var rewards  = result?["reward"]?.AsArray();
            if (baseFees is null || baseFees.Count == 0) return (0m, 0m, 0m);

            // Last entry is the pending block's expected base fee
            var baseGwei = HexWeiToGwei(baseFees[baseFees.Count - 1]?.GetValue<string>() ?? "0x0");

            decimal p10 = 1m, p50 = 1.5m, p90 = 3m;
            if (rewards is { Count: > 0 })
            {
                decimal sum10 = 0, sum50 = 0, sum90 = 0; int cnt = 0;
                foreach (var r in rewards)
                {
                    var a = r?.AsArray();
                    if (a?.Count >= 3)
                    {
                        sum10 += HexWeiToGwei(a[0]?.GetValue<string>() ?? "0x1");
                        sum50 += HexWeiToGwei(a[1]?.GetValue<string>() ?? "0x1");
                        sum90 += HexWeiToGwei(a[2]?.GetValue<string>() ?? "0x1");
                        cnt++;
                    }
                }
                if (cnt > 0) { p10 = sum10 / cnt; p50 = sum50 / cnt; p90 = sum90 / cnt; }
            }

            var slow = Math.Round(baseGwei + Math.Max(0.1m, p10), 1);
            var std  = Math.Round(baseGwei + Math.Max(0.5m, p50), 1);
            var fast = Math.Round(baseGwei + Math.Max(1.0m, p90), 1);
            return (slow, std, fast);
        }
        catch { return (0m, 0m, 0m); }
    }

    // ── BSC gas (eth_gasPrice via public BSC RPC; oracle if key set) ──────────

    private async Task<(decimal slow, decimal std, decimal fast)> FetchBscGasAsync(CancellationToken ct)
    {
        // Use BSCScan oracle when an API key is configured
        if (!string.IsNullOrWhiteSpace(BscscanApiKey))
        {
            try
            {
                var url  = $"https://api.bscscan.com/api?module=gastracker&action=gasoracle&apikey={BscscanApiKey}";
                var json = await _http.GetStringAsync(url, ct);
                var node = JsonNode.Parse(json)?["result"];
                var slow = ParseDecimal(node?["SafeGasPrice"]?.GetValue<string>());
                var std  = ParseDecimal(node?["ProposeGasPrice"]?.GetValue<string>());
                var fast = ParseDecimal(node?["FastGasPrice"]?.GetValue<string>());
                if (slow > 0) return (slow, std, fast);
            }
            catch { }
        }

        // Fallback: official Binance public BSC RPC — eth_gasPrice
        try
        {
            const string rpc  = "https://bsc-dataseed.binance.org/";
            const string body = """{"jsonrpc":"2.0","id":1,"method":"eth_gasPrice","params":[]}""";
            using var req = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(rpc, req, ct);
            var json       = await res.Content.ReadAsStringAsync(ct);
            var hexPrice   = JsonNode.Parse(json)?["result"]?.GetValue<string>() ?? "0x0";
            var priceGwei  = HexWeiToGwei(hexPrice);
            if (priceGwei <= 0) return (0m, 0m, 0m);

            // BSC uses a near-fixed gas price; build tiers around it
            // Use 3 decimal places so sub-gwei values (e.g. 0.05 gwei) are not rounded to 0
            var slow = Math.Round(priceGwei * 0.85m, 3);
            var std  = Math.Round(priceGwei, 3);
            var fast = Math.Round(priceGwei * 1.25m, 3);
            return (slow, std, fast);
        }
        catch { return (0m, 0m, 0m); }
    }

    // ── Solana TPS (getRecentPerformanceSamples) ──────────────────────────────

    private async Task<double> FetchSolanaTpsAsync(CancellationToken ct)
    {
        try
        {
            const string url  = "https://api.mainnet-beta.solana.com";
            const string body = """{"jsonrpc":"2.0","id":1,"method":"getRecentPerformanceSamples","params":[4]}""";

            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var res     = await _http.PostAsync(url, content, ct);
            var json = await res.Content.ReadAsStringAsync(ct);

            var samples = JsonNode.Parse(json)?["result"]?.AsArray();
            if (samples is null || samples.Count == 0) return 0;

            double total = 0;
            int    count = 0;
            foreach (var s in samples)
            {
                var numTx  = s?["numTransactions"]?.GetValue<long>()   ?? 0;
                var period = s?["samplePeriodSecs"]?.GetValue<double>() ?? 0;
                if (period > 0) { total += numTx / period; count++; }
            }
            return count > 0 ? Math.Round(total / count, 0) : 0;
        }
        catch { return 0; }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Converts a 0x-prefixed hex wei value to gwei as decimal.</summary>
    private static decimal HexWeiToGwei(string hex)
    {
        try
        {
            var h = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
            if (string.IsNullOrEmpty(h)) return 0m;
            var wei = Convert.ToUInt64(h, 16);
            return (decimal)wei / 1_000_000_000m;
        }
        catch { return 0m; }
    }

    private static decimal ParseDecimal(string? s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
