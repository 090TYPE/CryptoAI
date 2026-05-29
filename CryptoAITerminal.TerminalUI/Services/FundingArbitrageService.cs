using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
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
/// Detects funding-rate arbitrage opportunities across Binance / Bybit / OKX
/// and executes delta-neutral positions (long spot + short perpetual).
/// </summary>
public sealed class FundingArbitrageService : IDisposable
{
    // ── Exchange gateways (null = exchange not configured) ────────────────────

    private readonly IExchangeGateway? _binanceSpot;
    private readonly IExchangeGateway? _binanceFutures;
    private readonly IExchangeGateway? _bybitSpot;
    private readonly IExchangeGateway? _bybitFutures;
    private readonly IExchangeGateway? _okxSpot;
    private readonly IExchangeGateway? _okxFutures;

    // БАГ-24: PooledConnectionLifetime обновляет DNS каждые 5 минут вместо бесконечного кэша
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonDocumentOptions _jOpts = new() { AllowTrailingCommas = true };

    // Symbols to monitor — match the terminal's default universe
    private static readonly string[] Symbols =
        ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "ADAUSDT", "DOGEUSDT", "AVAXUSDT"];

    // ── Configuration (writable from ViewModel) ───────────────────────────────

    /// <summary>Minimum funding rate (% per 8 h) to show as an opportunity. Default 0.05 %.</summary>
    public decimal ThresholdPct { get; set; } = 0.05m;
    /// <summary>USD notional per arb position (both legs). Default $500.</summary>
    public decimal NotionalUsd  { get; set; } = 500m;
    /// <summary>Leverage applied to the short-perp leg. Default 3×.</summary>
    public int     Leverage          { get; set; } = 3;
    /// <summary>When true, accumulated funding income is reinvested into the position.</summary>
    public bool    AutoReinvest      { get; set; } = false;
    /// <summary>Minimum accumulated funding (USD) before reinvesting. Default $10.</summary>
    public decimal ReinvestThreshold { get; set; } = 10m;

    // ── Position state ────────────────────────────────────────────────────────

    private readonly List<FundingArbPosition> _positions = [];
    public IReadOnlyList<FundingArbPosition> AllPositions     => _positions;
    public IReadOnlyList<FundingArbPosition> OpenPositions    =>
        _positions.Where(p => p.State == FundingArbPositionState.Open).ToList();

    // ── Constructor ───────────────────────────────────────────────────────────

    public FundingArbitrageService(
        IExchangeGateway? binanceSpot,
        IExchangeGateway? binanceFutures,
        IExchangeGateway? bybitSpot,
        IExchangeGateway? bybitFutures,
        IExchangeGateway? okxSpot,
        IExchangeGateway? okxFutures)
    {
        _binanceSpot    = binanceSpot;
        _binanceFutures = binanceFutures;
        _bybitSpot      = bybitSpot;
        _bybitFutures   = bybitFutures;
        _okxSpot        = okxSpot;
        _okxFutures     = okxFutures;
    }

    // ── Opportunity fetching (public REST – no auth) ──────────────────────────

    public async Task<IReadOnlyList<FundingArbitrageOpportunity>> FetchOpportunitiesAsync(
        CancellationToken ct = default)
    {
        var tasks = new[]
        {
            SafeFetch(FetchBinanceAsync, ct),
            SafeFetch(FetchBybitAsync,   ct),
            SafeFetch(FetchOkxAsync,     ct),
        };

        var results = await Task.WhenAll(tasks);

        return results
            .SelectMany(r => r)
            .Where(o => o.FundingRatePct > 0)           // positive rate = longs pay shorts
            .OrderByDescending(o => o.FundingRatePct)
            .ToList();
    }

    private static async Task<List<FundingArbitrageOpportunity>> SafeFetch(
        Func<CancellationToken, Task<List<FundingArbitrageOpportunity>>> fn,
        CancellationToken ct)
    {
        try   { return await fn(ct); }
        catch { return []; }
    }

    // GET https://fapi.binance.com/fapi/v1/premiumIndex  (no auth, returns all)
    private async Task<List<FundingArbitrageOpportunity>> FetchBinanceAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync(
            "https://fapi.binance.com/fapi/v1/premiumIndex", ct);

        using var doc = JsonDocument.Parse(json, _jOpts);
        var results = new List<FundingArbitrageOpportunity>(Symbols.Length);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var sym = el.GetProperty("symbol").GetString() ?? "";
            if (!Symbols.Contains(sym)) continue;

            if (!TryGetDecimalProp(el, "lastFundingRate", out var rate)) continue;
            TryGetDecimalProp(el, "markPrice", out var mark);
            var nftMs = el.TryGetProperty("nextFundingTime", out var nft) ? nft.GetInt64() : 0L;

            results.Add(MakeOpportunity("Binance", sym, rate, mark, nftMs));
        }

        return results;
    }

    // GET https://api.bybit.com/v5/market/tickers?category=linear  (no auth, returns all)
    private async Task<List<FundingArbitrageOpportunity>> FetchBybitAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync(
            "https://api.bybit.com/v5/market/tickers?category=linear", ct);

        using var doc = JsonDocument.Parse(json, _jOpts);
        var list    = doc.RootElement.GetProperty("result").GetProperty("list");
        var results = new List<FundingArbitrageOpportunity>(Symbols.Length);

        foreach (var el in list.EnumerateArray())
        {
            var sym = el.GetProperty("symbol").GetString() ?? "";
            if (!Symbols.Contains(sym)) continue;

            if (!TryGetDecimalStr(el, "fundingRate", out var rate)) continue;
            TryGetDecimalStr(el, "markPrice", out var mark);

            long nftMs = 0;
            if (el.TryGetProperty("nextFundingTime", out var nftProp))
                long.TryParse(nftProp.GetString(), out nftMs);

            results.Add(MakeOpportunity("Bybit", sym, rate, mark, nftMs));
        }

        return results;
    }

    // GET https://www.okx.com/api/v5/public/funding-rate?instId=BTC-USDT-SWAP  (per symbol)
    private async Task<List<FundingArbitrageOpportunity>> FetchOkxAsync(CancellationToken ct)
    {
        var results = new List<FundingArbitrageOpportunity>(Symbols.Length);

        foreach (var sym in Symbols)
        {
            if (ct.IsCancellationRequested) break;
            var okxSym = ToOkxSwap(sym);
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://www.okx.com/api/v5/public/funding-rate?instId={okxSym}", ct);

                using var doc = JsonDocument.Parse(json, _jOpts);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) continue;

                var el = data[0];
                if (!TryGetDecimalStr(el, "fundingRate", out var rate)) continue;

                long nftMs = 0;
                if (el.TryGetProperty("nextFundingTime", out var nftProp))
                    long.TryParse(nftProp.GetString(), out nftMs);

                results.Add(MakeOpportunity("OKX", sym, rate, 0m, nftMs));
            }
            catch { }

            await Task.Delay(80, ct);   // gentle rate-limit
        }

        return results;
    }

    // ── Position management ───────────────────────────────────────────────────

    /// <summary>
    /// Opens a delta-neutral arb: buys <see cref="NotionalUsd"/> of spot,
    /// opens a matching short perpetual on the same exchange.
    /// </summary>
    public async Task<(bool Ok, string Error)> OpenPositionAsync(
        FundingArbitrageOpportunity opp, CancellationToken ct = default)
    {
        var (spotGw, futGw) = GetGateways(opp.Exchange);
        if (spotGw is null || futGw is null)
            return (false, $"No API credentials configured for {opp.Exchange}");

        if (opp.MarkPrice <= 0)
            return (false, "Mark price unavailable — press Refresh and try again");

        var qty = Math.Round(NotionalUsd / opp.MarkPrice, 6);
        if (qty <= 0)
            return (false, "Notional is too small for this price");

        try
        {
            // 1. Set leverage on futures side
            await futGw.SetLeverageAsync(opp.Symbol, Leverage);

            // 2. Buy spot
            var spotOrder = await spotGw.PlaceOrderAsync(new Order
            {
                Symbol   = opp.Symbol,
                Side     = OrderSide.Buy,
                Type     = OrderType.Market,
                Quantity = qty,
            });

            // 3. Short perpetual
            var perpOrder = await futGw.PlaceOrderAsync(new Order
            {
                Symbol       = opp.Symbol,
                Side         = OrderSide.Sell,
                Type         = OrderType.Market,
                Quantity     = qty,
                PositionSide = FuturesPositionSide.Short,
                Leverage     = Leverage,
            });

            var entryPrice = opp.MarkPrice;   // approximation; exact fill via order.Price if available

            _positions.Add(new FundingArbPosition
            {
                Exchange            = opp.Exchange,
                Symbol              = opp.Symbol,
                SpotQty             = qty,
                PerpQty             = qty,
                SpotEntryPrice      = entryPrice,
                PerpEntryPrice      = entryPrice,
                NotionalUsd         = NotionalUsd,
                EntryFundingRatePct = opp.FundingRatePct,
                OpenedAt            = DateTime.UtcNow,
                CurrentSpotPrice    = entryPrice,
                CurrentPerpPrice    = entryPrice,
                State               = FundingArbPositionState.Open,
            });

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Closes an open arb position: market-closes the short perp,
    /// then market-sells the spot.
    /// </summary>
    public async Task<(bool Ok, string Error)> ClosePositionAsync(
        FundingArbPosition pos, CancellationToken ct = default)
    {
        if (pos.State != FundingArbPositionState.Open)
            return (false, "Position is not open");

        pos.State = FundingArbPositionState.Closing;

        var (spotGw, futGw) = GetGateways(pos.Exchange);
        var errors = new List<string>(2);

        if (futGw is not null)
        {
            try
            {
                await futGw.PlaceOrderAsync(new Order
                {
                    Symbol       = pos.Symbol,
                    Side         = OrderSide.Buy,
                    Type         = OrderType.Market,
                    Quantity     = pos.PerpQty,
                    ReduceOnly   = true,
                    PositionSide = FuturesPositionSide.Short,
                });
            }
            catch (Exception ex) { errors.Add($"Perp close: {ex.Message}"); }
        }

        if (spotGw is not null)
        {
            try
            {
                await spotGw.PlaceOrderAsync(new Order
                {
                    Symbol   = pos.Symbol,
                    Side     = OrderSide.Sell,
                    Type     = OrderType.Market,
                    Quantity = pos.SpotQty,
                });
            }
            catch (Exception ex) { errors.Add($"Spot sell: {ex.Message}"); }
        }

        // Только при успешном закрытии обеих ног помечаем позицию закрытой.
        // Если хотя бы один ордер провалился — возвращаем в Open, чтобы пользователь
        // видел её в UI и мог повторить или закрыть вручную (иначе реальная
        // позиция останется висеть на бирже, а в UI исчезнет).
        if (errors.Count == 0)
        {
            pos.State = FundingArbPositionState.Closed;
            return (true, "");
        }

        pos.State = FundingArbPositionState.Open;
        return (false, string.Join(" | ", errors));
    }

    /// <summary>
    /// Updates funding-income estimates and current prices for all open positions.
    /// When AutoReinvest is enabled and accumulated funding >= ReinvestThreshold,
    /// the funding income is added to both legs of the position.
    /// Call after every rate refresh.
    /// </summary>
    public void UpdateFundingEstimates(IReadOnlyList<FundingArbitrageOpportunity> latestRates)
    {
        var rateMap = latestRates.ToDictionary(r => (r.Exchange, r.Symbol));

        foreach (var pos in _positions.Where(p => p.State == FundingArbPositionState.Open))
        {
            // Update mark price
            if (rateMap.TryGetValue((pos.Exchange, pos.Symbol), out var opp) && opp.MarkPrice > 0)
            {
                pos.CurrentSpotPrice = opp.MarkPrice;
                pos.CurrentPerpPrice = opp.MarkPrice;
            }

            // Estimate: notional × rate_per_period × periods_elapsed
            var periodsElapsed = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours / 8m;
            pos.FundingCollectedUsd =
                pos.NotionalUsd * (pos.EntryFundingRatePct / 100m) * periodsElapsed;

            // Auto-reinvest: compound collected funding back into the position
            if (AutoReinvest
                && pos.FundingCollectedUsd >= ReinvestThreshold
                && pos.CurrentSpotPrice > 0)
            {
                _ = ReinvestAsync(pos);
            }
        }
    }

    private async Task ReinvestAsync(FundingArbPosition pos)
    {
        var (spotGw, futGw) = GetGateways(pos.Exchange);
        if (spotGw is null || futGw is null) return;

        var extraQty = Math.Round(pos.FundingCollectedUsd / pos.CurrentSpotPrice, 6);
        if (extraQty <= 0) return;

        try
        {
            await spotGw.PlaceOrderAsync(new Order
            {
                Symbol   = pos.Symbol,
                Side     = OrderSide.Buy,
                Type     = OrderType.Market,
                Quantity = extraQty,
            });
            await futGw.PlaceOrderAsync(new Order
            {
                Symbol       = pos.Symbol,
                Side         = OrderSide.Sell,
                Type         = OrderType.Market,
                Quantity     = extraQty,
                PositionSide = FuturesPositionSide.Short,
            });

            pos.SpotQty    += extraQty;
            pos.PerpQty    += extraQty;
            pos.NotionalUsd += pos.FundingCollectedUsd;
            pos.FundingCollectedUsd = 0m;
            pos.ReinvestCount++;
        }
        catch { /* best-effort — next tick will retry */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static FundingArbitrageOpportunity MakeOpportunity(
        string exchange, string symbol, decimal rate, decimal markPrice, long nextFundingMs) => new()
    {
        Exchange        = exchange,
        Symbol          = symbol,
        FundingRatePct  = Math.Round(rate * 100m, 6),
        AnnualizedPct   = Math.Round(rate * 3m * 365m * 100m, 2),
        NextFundingTime = nextFundingMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(nextFundingMs).UtcDateTime
            : DateTime.UtcNow.AddHours(8),
        MarkPrice = markPrice,
    };

    private (IExchangeGateway? spot, IExchangeGateway? fut) GetGateways(string exchange) =>
        exchange switch
        {
            "Binance" => (_binanceSpot, _binanceFutures),
            "Bybit"   => (_bybitSpot,   _bybitFutures),
            "OKX"     => (_okxSpot,     _okxFutures),
            _         => (null, null),
        };

    private static string ToOkxSwap(string s) =>
        s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? s[..^4] + "-USDT-SWAP" : s;

    private static bool TryGetDecimalProp(JsonElement el, string prop, out decimal value)
    {
        value = 0m;
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetDecimal(out value);
        var s = p.GetString() ?? "";
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDecimalStr(JsonElement el, string prop, out decimal value)
    {
        value = 0m;
        if (!el.TryGetProperty(prop, out var p)) return false;
        var s = p.GetString() ?? "";
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose() { /* HttpClient is static — not disposed here */ }
}
