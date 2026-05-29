using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Per-asset snapshot computed by the rebalance engine.
/// All monetary values are in USD.
/// </summary>
public sealed record PortfolioAssetSnapshot(
    string  Symbol,
    decimal CexBalance,          // sum across all connected CEX gateways
    decimal DexBalance,          // from connected DEX wallet (0 if not applicable / not connected)
    decimal TotalBalance,        // CexBalance + DexBalance
    decimal PriceUsd,            // current market price
    decimal ValueUsd,            // TotalBalance × PriceUsd
    double  ActualPct,           // this asset's share of total portfolio value (0-100)
    double  TargetPct,           // user-defined target weight (0-100)
    double  DeviationPct,        // ActualPct − TargetPct  (positive = overweight)
    decimal RebalanceDeltaUsd,   // > 0 means BUY, < 0 means SELL
    decimal RebalanceDeltaUnits  // RebalanceDeltaUsd / PriceUsd
);

/// <summary>
/// Fetches balances from all exchange gateways + the DEX wallet, prices from Binance REST,
/// and computes portfolio weights + rebalancing deltas.
/// Thread-safe: every call to <see cref="FetchSnapshotAsync"/> is independent.
/// </summary>
public sealed class PortfolioRebalanceService : IDisposable
{
    private readonly IReadOnlyList<IExchangeGateway> _cexGateways;
    private readonly WalletWorkspaceViewModel        _walletVm;
    private readonly HttpClient                      _http;
    private bool _disposed;

    // Stablecoins are always priced at exactly $1 — skip REST call for them.
    private static readonly HashSet<string> StableCoins =
        new(StringComparer.OrdinalIgnoreCase)
        { "USDT", "USDC", "BUSD", "DAI", "TUSD", "FDUSD", "USDP", "GUSD" };

    // Map network name → native asset symbol
    private static readonly Dictionary<string, string> NetworkNativeSymbol =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = "ETH",
            ["base"]     = "ETH",
            ["arbitrum"] = "ETH",
            ["bsc"]      = "BNB",
            ["solana"]   = "SOL",
            ["tron"]     = "TRX",
        };

    public PortfolioRebalanceService(
        IReadOnlyList<IExchangeGateway> cexGateways,
        WalletWorkspaceViewModel walletVm)
    {
        _cexGateways = cexGateways;
        _walletVm    = walletVm;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches current balances and prices for every allocation entry, then returns
    /// a list of snapshots with computed weights and rebalance deltas.
    /// Safe to call from background threads; posts nothing to the UI thread.
    /// </summary>
    public async Task<IReadOnlyList<PortfolioAssetSnapshot>> FetchSnapshotAsync(
        IReadOnlyList<PortfolioAllocation> allocations,
        CancellationToken ct = default)
    {
        if (allocations.Count == 0) return [];

        var symbols = allocations.Select(a => a.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Fan out: fetch prices and CEX balances in parallel
        var priceTask      = FetchPricesAsync(symbols, ct);
        var cexBalanceTask = FetchCexBalancesAsync(symbols, ct);
        await Task.WhenAll(priceTask, cexBalanceTask).ConfigureAwait(false);

        var prices      = priceTask.Result;
        var cexBalances = cexBalanceTask.Result;
        var dexBalances = GetDexNativeBalance(symbols);   // synchronous, reads VM properties

        // ── First pass: compute USD values ────────────────────────────────────
        var rows = allocations.Select(alloc =>
        {
            var cex   = alloc.IncludeCex ? cexBalances.GetValueOrDefault(alloc.Symbol, 0m) : 0m;
            var dex   = alloc.IncludeDex ? dexBalances.GetValueOrDefault(alloc.Symbol, 0m) : 0m;
            var price = prices.GetValueOrDefault(alloc.Symbol, 0m);
            return (alloc, cex, dex, price, value: (cex + dex) * price);
        }).ToList();

        var totalValue = rows.Sum(r => r.value);
        if (totalValue == 0m) totalValue = 1m;  // guard against empty portfolio

        // ── Second pass: compute weights and deltas ───────────────────────────
        return rows.Select(r =>
        {
            var (alloc, cex, dex, price, value) = r;
            var actual      = (double)(value / totalValue) * 100.0;
            var target      = alloc.TargetPct;
            var deviation   = actual - target;
            var targetUsd   = (decimal)(target / 100.0) * totalValue;
            var deltaUsd    = targetUsd - value;           // + buy, − sell
            var deltaUnits  = price > 0m ? deltaUsd / price : 0m;

            return new PortfolioAssetSnapshot(
                Symbol:              alloc.Symbol,
                CexBalance:          cex,
                DexBalance:          dex,
                TotalBalance:        cex + dex,
                PriceUsd:            price,
                ValueUsd:            value,
                ActualPct:           Math.Round(actual,    2),
                TargetPct:           Math.Round(target,    2),
                DeviationPct:        Math.Round(deviation, 2),
                RebalanceDeltaUsd:   Math.Round(deltaUsd,    2),
                RebalanceDeltaUnits: Math.Round(deltaUnits,  6));
        }).ToList();
    }

    // ── Price fetching (Binance batch endpoint) ───────────────────────────────

    private async Task<Dictionary<string, decimal>> FetchPricesAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        // Stablecoins → immediate $1.00
        foreach (var sym in symbols)
            if (StableCoins.Contains(sym)) prices[sym] = 1m;

        var toFetch = symbols.Where(s => !prices.ContainsKey(s)).ToList();
        if (toFetch.Count == 0) return prices;

        // Binance accepts: /api/v3/ticker/price?symbols=["BTCUSDT","ETHUSDT"]
        // Normalize "BTCUSDT" → "BTC" so we don't form "BTCUSDTUSDT"
        var quotedPairs = string.Join(",", toFetch.Select(s => $"\"{NormalizeToAsset(s)}USDT\""));
        var url = $"https://api.binance.com/api/v3/ticker/price?symbols={Uri.EscapeDataString($"[{quotedPairs}]")}";

        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var raw = item.TryGetProperty("symbol", out var sp) ? sp.GetString() ?? "" : "";
                // "BTCUSDT" → "BTC"
                var asset = raw.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                    ? raw[..^4] : raw;

                if (!string.IsNullOrEmpty(asset)
                    && item.TryGetProperty("price", out var pp)
                    && decimal.TryParse(pp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                {
                    prices[asset] = p;
                }
            }
        }
        catch
        {
            // Network error or unknown symbol — leave price as 0 (shown as "?" in UI)
        }

        return prices;
    }

    // ── CEX balance aggregation ───────────────────────────────────────────────

    private async Task<Dictionary<string, decimal>> FetchCexBalancesAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (_cexGateways.Count == 0) return result;

        // For each symbol query all gateways concurrently, sum the results
        var tasks = symbols.Select(async sym =>
        {
            decimal total = 0m;
            var gatewayTasks = _cexGateways.Select(async gw =>
            {
                try   { return await gw.GetBalanceAsync(sym).ConfigureAwait(false); }
                catch { return 0m; }  // gateway offline / no API key → contribute 0
            });
            var amounts = await Task.WhenAll(gatewayTasks).ConfigureAwait(false);
            total = amounts.Sum();
            return (sym, total);
        });

        var rows = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (sym, total) in rows)
            result[sym] = total;

        return result;
    }

    // ── DEX wallet native balance ─────────────────────────────────────────────

    /// <summary>
    /// Reads the currently connected DEX wallet's native balance.
    /// Returns a single-entry dict with the native symbol → balance,
    /// or empty if no wallet is connected.
    /// </summary>
    private Dictionary<string, decimal> GetDexNativeBalance(IReadOnlyList<string> symbols)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        if (!_walletVm.IsConnected || _walletVm.NativeBalance <= 0) return result;

        var network = _walletVm.SelectedNetwork ?? "";
        if (!NetworkNativeSymbol.TryGetValue(network, out var nativeSym)) return result;

        // Only populate if this native asset is actually in the allocation list
        if (symbols.Any(s => string.Equals(s, nativeSym, StringComparison.OrdinalIgnoreCase)))
            result[nativeSym] = _walletVm.NativeBalance;

        return result;
    }

    // БАГ-18: strip trailing "USDT" so "BTCUSDT" → "BTC" before appending "USDT"
    private static string NormalizeToAsset(string symbol) =>
        symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? symbol[..^4] : symbol;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed) { _disposed = true; _http.Dispose(); }
    }
}
