using System.Text;
using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Per-chain gas price (gwei) via public JSON-RPC (eth_gasPrice). Free — no key.
/// </summary>
public sealed class GasCollector : IDataCollector
{
    private static readonly (string Chain, string Rpc)[] Rpcs =
    {
        ("eth",      "https://ethereum-rpc.publicnode.com"),
        ("bsc",      "https://bsc-rpc.publicnode.com"),
        ("base",     "https://base-rpc.publicnode.com"),
        ("arbitrum", "https://arbitrum-one-rpc.publicnode.com"),
        ("polygon",  "https://polygon-bor-rpc.publicnode.com"),
    };

    private const string RpcBody = "{\"jsonrpc\":\"2.0\",\"method\":\"eth_gasPrice\",\"params\":[],\"id\":1}";

    private readonly HttpClient _http;
    private readonly GasRepository _gas;
    private readonly ILogger<GasCollector> _log;

    public GasCollector(GasRepository gas, ILogger<GasCollector> log)
    {
        _gas = gas;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Name => "gas";
    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var (chain, rpc) in Rpcs)
        {
            try
            {
                using var body = new StringContent(RpcBody, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(rpc, body, ct);
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var hex = doc.RootElement.GetProperty("result").GetString();
                if (string.IsNullOrWhiteSpace(hex)) continue;

                var wei = Convert.ToUInt64(hex.StartsWith("0x") ? hex[2..] : hex, 16);
                var gwei = wei / 1_000_000_000m;

                await _gas.InsertAsync(chain, fast: null, standard: gwei, slow: null, ct);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "gas {Chain} failed", chain); }
        }
        return count;
    }
}
