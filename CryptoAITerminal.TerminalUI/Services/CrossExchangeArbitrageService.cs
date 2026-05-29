using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Monitors real-time prices across Binance / Bybit / OKX spot markets,
/// detects cross-exchange spread opportunities and executes simultaneous arb trades.
/// </summary>
public sealed class CrossExchangeArbitrageService : IDisposable
{
    // ── Exchange gateways ─────────────────────────────────────────────────────

    private readonly Dictionary<string, IExchangeGateway> _gateways;
    private readonly List<IDisposable> _subs = [];
    private bool _monitoring;

    // ── Thread-safe price matrix  key = (exchange, symbol) ───────────────────
    // We replace the record atomically — record is immutable, reference assignment
    // on 64-bit .NET is atomic, so no lock needed for reads.

    private readonly ConcurrentDictionary<(string exch, string sym), ExchangePriceData> _prices = new();

    // ── Execution history ─────────────────────────────────────────────────────

    private readonly List<CrossExchangeArbExecution> _history = [];
    public IReadOnlyList<CrossExchangeArbExecution> History => _history;

    // Cooldown: prevent the same pair from being auto-traded more than once per window
    private readonly ConcurrentDictionary<string, DateTime> _lastExecTime = new();

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum NET spread (% of buy price) required to show as "profitable".
    /// Default 0.15% (covers 0.1% fee on each leg, plus 0.05% buffer).
    /// </summary>
    public decimal MinNetSpreadPct   { get; set; } = 0.15m;
    /// <summary>Taker fee per leg (%). Default 0.10%.</summary>
    public decimal TakerFeePct       { get; set; } = 0.10m;
    /// <summary>USD notional per arb execution. Default $500.</summary>
    public decimal TradeNotionalUsd  { get; set; } = 500m;
    /// <summary>When true, auto-executes profitable opportunities without confirmation.</summary>
    public bool    AutoExecute       { get; set; } = false;
    /// <summary>Minimum seconds between auto-executions for the same symbol pair.</summary>
    public int     AutoExecCooldownSec { get; set; } = 30;

    private decimal RoundTripFeePct => TakerFeePct * 2;

    internal static readonly string[] ExchangeNames = ["Binance", "Bybit", "OKX"];

    // ── Constructor ───────────────────────────────────────────────────────────

    public CrossExchangeArbitrageService(
        IExchangeGateway? binance,
        IExchangeGateway? bybit,
        IExchangeGateway? okx)
    {
        _gateways = new(StringComparer.OrdinalIgnoreCase);
        if (binance != null) _gateways["Binance"] = binance;
        if (bybit   != null) _gateways["Bybit"]   = bybit;
        if (okx     != null) _gateways["OKX"]     = okx;
    }

    // ── Monitoring ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects all configured gateways and subscribes to their market-data streams.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public void StartMonitoring()
    {
        if (_monitoring) return;
        _monitoring = true;

        foreach (var (name, gw) in _gateways)
        {
            // Connect in background — public ticker streams work without API credentials
            _ = Task.Run(async () =>
            {
                try { await gw.ConnectAsync(); } catch { /* already connected or no creds */ }
            });

            var exchName = name; // capture for lambda
            var sub = gw.MarketDataStream.Subscribe(data => OnPrice(exchName, data));
            _subs.Add(sub);
        }
    }

    public void StopMonitoring()
    {
        foreach (var sub in _subs) sub.Dispose();
        _subs.Clear();
        _monitoring = false;
    }

    private void OnPrice(string exchange, MarketData data)
    {
        // Atomic record replacement — thread-safe without locks
        _prices[(exchange, data.Symbol)] =
            new ExchangePriceData(data.BestBid, data.BestAsk, data.LastPrice, DateTime.UtcNow);
    }

    // ── Price matrix snapshot ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of all symbol prices across all exchanges,
    /// sorted by best net spread descending.
    /// </summary>
    public IReadOnlyList<CrossExchangePriceRow> GetPriceMatrix()
    {
        var symbols = _prices.Keys
            .Select(k => k.sym)
            .Distinct()
            .ToList();

        var rows = new List<CrossExchangePriceRow>(symbols.Count);

        foreach (var sym in symbols)
        {
            var row = new CrossExchangePriceRow { Symbol = sym };

            foreach (var exch in ExchangeNames)
            {
                if (_prices.TryGetValue((exch, sym), out var snap) && !snap.IsStale)
                    row.Prices[exch] = snap;
            }

            // Only show row if we have data from at least 2 exchanges
            if (row.Prices.Count >= 2)
            {
                row.BestOpportunity = DetectBestOpportunity(sym);
                rows.Add(row);
            }
        }

        return rows
            .OrderByDescending(r => r.BestOpportunity?.NetSpreadPct ?? decimal.MinValue)
            .ToList();
    }

    /// <summary>Finds the highest-net-spread arb opportunity for a single symbol.</summary>
    public CrossExchangeOpportunity? DetectBestOpportunity(string symbol)
    {
        CrossExchangeOpportunity? best = null;

        foreach (var buyExch in ExchangeNames)
        {
            if (!_prices.TryGetValue((buyExch, symbol), out var buySnap) || buySnap.IsStale) continue;

            foreach (var sellExch in ExchangeNames)
            {
                if (buyExch == sellExch) continue;
                if (!_prices.TryGetValue((sellExch, symbol), out var sellSnap) || sellSnap.IsStale) continue;

                var ask = buySnap.EffAsk;
                var bid = sellSnap.EffBid;
                if (ask <= 0 || bid <= 0) continue;

                var gross = (bid - ask) / ask * 100m;
                var net   = gross - RoundTripFeePct;

                if (best is null || net > best.NetSpreadPct)
                {
                    best = new CrossExchangeOpportunity
                    {
                        Symbol         = symbol,
                        BuyExchange    = buyExch,
                        BuyAsk         = ask,
                        SellExchange   = sellExch,
                        SellBid        = bid,
                        GrossSpreadPct = Math.Round(gross, 5),
                        NetSpreadPct   = Math.Round(net,   5),
                        DetectedAt     = DateTime.UtcNow,
                    };
                }
            }
        }

        return best;
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes both legs of an arb simultaneously.
    /// Checks balances first — returns error if insufficient funds on either side.
    /// </summary>
    public async Task<(bool Ok, string Error, decimal ProfitUsd)> ExecuteArbAsync(
        CrossExchangeOpportunity opp, CancellationToken ct = default)
    {
        if (!_gateways.TryGetValue(opp.BuyExchange,  out var buyGw)  ||
            !_gateways.TryGetValue(opp.SellExchange, out var sellGw))
            return (false, $"Gateway not configured for {opp.BuyExchange} or {opp.SellExchange}", 0m);

        var qty = Math.Round(TradeNotionalUsd / opp.BuyAsk, 6);
        if (qty <= 0) return (false, "Notional too small for this price", 0m);

        // ── Balance preflight ────────────────────────────────────────────────

        try
        {
            var usdtBalance = await buyGw.GetBalanceAsync("USDT");
            if (usdtBalance < TradeNotionalUsd)
                return (false,
                    $"Insufficient USDT on {opp.BuyExchange} " +
                    $"(have ${usdtBalance:N0}, need ${TradeNotionalUsd:N0})", 0m);

            var coin         = opp.Symbol.Replace("USDT", "", StringComparison.OrdinalIgnoreCase);
            var coinBalance  = await sellGw.GetBalanceAsync(coin);
            if (coinBalance < qty)
                return (false,
                    $"Insufficient {coin} on {opp.SellExchange} " +
                    $"(have {coinBalance:F6}, need {qty:F6}). " +
                    $"Transfer needed — arb alert only.", 0m);
        }
        catch (Exception ex)
        {
            return (false, $"Balance check failed: {ex.Message}", 0m);
        }

        // ── Simultaneous execution ───────────────────────────────────────────

        decimal buyFill = 0m, sellFill = 0m;
        string? buyErr  = null, sellErr = null;

        await Task.WhenAll(
            Task.Run(async () =>
            {
                try
                {
                    var o = await buyGw.PlaceOrderAsync(new Order
                    {
                        Symbol   = opp.Symbol,
                        Side     = OrderSide.Buy,
                        Type     = OrderType.Market,
                        Quantity = qty,
                    });
                    buyFill = o.Price > 0 ? o.Price : opp.BuyAsk;
                }
                catch (Exception ex) { buyErr = ex.Message; }
            }, ct),
            Task.Run(async () =>
            {
                try
                {
                    var o = await sellGw.PlaceOrderAsync(new Order
                    {
                        Symbol   = opp.Symbol,
                        Side     = OrderSide.Sell,
                        Type     = OrderType.Market,
                        Quantity = qty,
                    });
                    sellFill = o.Price > 0 ? o.Price : opp.SellBid;
                }
                catch (Exception ex) { sellErr = ex.Message; }
            }, ct)
        );

        var failed    = buyErr != null || sellErr != null;
        var errorMsg  = string.Join(" | ", new[] { buyErr, sellErr }.Where(e => e != null));
        var profitUsd = failed ? 0m
            : (sellFill - buyFill) * qty - TradeNotionalUsd * RoundTripFeePct / 100m;

        // Record execution
        _history.Add(new CrossExchangeArbExecution
        {
            Symbol              = opp.Symbol,
            BuyExchange         = opp.BuyExchange,
            BuyPrice            = buyFill > 0 ? buyFill : opp.BuyAsk,
            SellExchange        = opp.SellExchange,
            SellPrice           = sellFill > 0 ? sellFill : opp.SellBid,
            Quantity            = qty,
            NotionalUsd         = TradeNotionalUsd,
            GrossSpreadPct      = opp.GrossSpreadPct,
            EstimatedProfitUsd  = profitUsd,
            ExecutedAt          = DateTime.UtcNow,
            Status              = failed ? $"Failed: {errorMsg}" : "Executed",
        });

        // Update cooldown
        if (!failed)
            _lastExecTime[$"{opp.BuyExchange}:{opp.SellExchange}:{opp.Symbol}"] = DateTime.UtcNow;

        return failed ? (false, errorMsg, 0m) : (true, "", profitUsd);
    }

    /// <summary>
    /// Checks if auto-execute should fire for an opportunity.
    /// Returns true only when: AutoExecute=on, net spread ≥ threshold, cooldown elapsed.
    /// </summary>
    public bool ShouldAutoExecute(CrossExchangeOpportunity opp)
    {
        if (!AutoExecute) return false;
        if (opp.NetSpreadPct < MinNetSpreadPct) return false;

        var key = $"{opp.BuyExchange}:{opp.SellExchange}:{opp.Symbol}";
        if (_lastExecTime.TryGetValue(key, out var last) &&
            (DateTime.UtcNow - last).TotalSeconds < AutoExecCooldownSec)
            return false;

        return true;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => StopMonitoring();
}
