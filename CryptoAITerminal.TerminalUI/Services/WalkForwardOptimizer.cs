using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

// ── Strategy type discriminator ──────────────────────────────────────────────

public enum BacktestStrategyType
{
    MACrossover,
    RSI,
    BollingerBands,
    Breakout
}

// ── Parameter bag ─────────────────────────────────────────────────────────────

/// <summary>Named parameter set for one strategy variant.</summary>
public sealed record WfParamSet(
    string Label,
    IReadOnlyDictionary<string, decimal> Values);

// ── Per-variant aggregated result ─────────────────────────────────────────────

public sealed record WfResult(
    WfParamSet Params,
    // In-sample averages across all folds
    decimal IsSharpe,
    decimal IsReturn,
    decimal IsMaxDD,
    int     IsTrades,
    // Out-of-sample averages (only folds where this variant was chosen as best)
    decimal OosSharpe,
    decimal OosReturn,
    decimal OosMaxDD,
    int     OosTrades,
    /// OosSharpe / IsSharpe — how well IS performance predicts OOS.
    decimal Efficiency,
    /// Primary ranking score: OOS Sharpe × (1 – OOS MaxDD/100).
    decimal WfScore,
    /// How many fold IS-windows nominated this variant as best.
    int     TimesSelected
);

// ── Optimizer ─────────────────────────────────────────────────────────────────

public static class WalkForwardOptimizer
{
    /// <summary>
    /// Runs a walk-forward optimisation across all parameter combinations for
    /// <paramref name="strategyType"/>.
    ///
    /// Algorithm:
    ///   1. Divide candles into <paramref name="folds"/> equal windows.
    ///   2. For each window: first <paramref name="inSamplePct"/> = IS, rest = OOS.
    ///   3. Sweep all param combos on IS → pick combo with best Sharpe.
    ///   4. Run that combo's params on OOS → capture OOS metrics.
    ///   5. Aggregate IS/OOS metrics across folds and rank by WfScore.
    ///
    /// Returns variants sorted by WfScore (best first).
    /// </summary>
    public static IReadOnlyList<WfResult> Optimize(
        BacktestStrategyType strategyType,
        IReadOnlyList<DexOhlcvPoint> candles,
        decimal commission   = 0.1m,
        int     folds        = 3,
        double  inSamplePct  = 0.70)
    {
        var space    = BuildParamSpace(strategyType);
        int foldSize = candles.Count / Math.Max(1, folds);

        // key → (paramSet, IS results list, OOS results list, timesSelected)
        var accum = new Dictionary<string, (WfParamSet p,
                                             List<BacktestResult> is_,
                                             List<BacktestResult> oos,
                                             int selected)>();

        // Pre-populate so every variant has an IS entry even if never selected
        foreach (var ps in space)
            accum[ps.Label] = (ps, new List<BacktestResult>(), new List<BacktestResult>(), 0);

        for (int fold = 0; fold < folds; fold++)
        {
            int start = fold * foldSize;
            int end   = fold == folds - 1 ? candles.Count : start + foldSize;
            var window = candles.Skip(start).Take(end - start).ToList();

            int splitAt = (int)(window.Count * inSamplePct);
            var isSlice  = window.Take(splitAt).ToList();
            var oosSlice = window.Skip(splitAt).ToList();

            if (isSlice.Count < 30 || oosSlice.Count < 5) continue;

            // ── IS sweep ──────────────────────────────────────────────────────
            WfParamSet? bestPs  = null;
            decimal     bestShp = decimal.MinValue;

            foreach (var ps in space)
            {
                var strat = Create(strategyType, ps);
                var res   = BacktestEngine.Run(strat, isSlice, commission);

                var (p, isList, oosList, sel) = accum[ps.Label];
                if (res.IsReady) isList.Add(res);
                accum[ps.Label] = (p, isList, oosList, sel);

                if (res.IsReady && res.SharpeRatio > bestShp)
                {
                    bestShp = res.SharpeRatio;
                    bestPs  = ps;
                }
            }

            // ── OOS validation with best IS params ────────────────────────────
            if (bestPs is not null)
            {
                var ooStrat = Create(strategyType, bestPs);
                var oosRes  = BacktestEngine.Run(ooStrat, oosSlice, commission);

                var (p, isList, oosList, sel) = accum[bestPs.Label];
                if (oosRes.IsReady) oosList.Add(oosRes);
                accum[bestPs.Label] = (p, isList, oosList, sel + 1);
            }
        }

        // ── Aggregate & rank ──────────────────────────────────────────────────
        var results = new List<WfResult>(space.Count);

        foreach (var (_, (ps, isList, oosList, timesSelected)) in accum)
        {
            var validIs  = isList .Where(r => r.IsReady).ToList();
            var validOos = oosList.Where(r => r.IsReady).ToList();

            if (validIs.Count == 0 && validOos.Count == 0) continue;

            decimal AvgD(List<BacktestResult> lst, Func<BacktestResult, decimal> sel) =>
                lst.Count == 0 ? 0m : lst.Average(sel);
            int SumI(List<BacktestResult> lst, Func<BacktestResult, int> sel) =>
                lst.Count == 0 ? 0  : lst.Sum(sel);

            var isSharpe  = AvgD(validIs,  r => r.SharpeRatio);
            var isReturn  = AvgD(validIs,  r => r.NetReturnPercent);
            var isDD      = AvgD(validIs,  r => r.MaxDrawdownPercent);
            var isTrades  = SumI(validIs,  r => r.TradeCount);

            var oosSharpe = AvgD(validOos, r => r.SharpeRatio);
            var oosReturn = AvgD(validOos, r => r.NetReturnPercent);
            var oosDD     = AvgD(validOos, r => r.MaxDrawdownPercent);
            var oosTrades = SumI(validOos, r => r.TradeCount);

            var efficiency = isSharpe != 0m ? oosSharpe / isSharpe : 0m;
            var wfScore    = oosSharpe * (1m - Math.Min(1m, oosDD / 100m));

            results.Add(new WfResult(
                ps,
                isSharpe,  isReturn,  isDD,  isTrades,
                oosSharpe, oosReturn, oosDD, oosTrades,
                efficiency, wfScore,  timesSelected));
        }

        results.Sort((a, b) => b.WfScore.CompareTo(a.WfScore));
        return results;
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    public static IStrategy Create(BacktestStrategyType type, WfParamSet ps)
    {
        var v = ps.Values;
        return type switch
        {
            BacktestStrategyType.MACrossover   => new SimpleMaStrategy((int)v["Fast"], (int)v["Slow"]),
            BacktestStrategyType.RSI           => new RsiStrategy((int)v["Period"], v["OB"], v["OS"]),
            BacktestStrategyType.BollingerBands=> new BollingerBandsStrategy((int)v["Period"], v["Mult"]),
            BacktestStrategyType.Breakout      => new BreakoutStrategy((int)v["Period"]),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    // ── Parameter spaces ──────────────────────────────────────────────────────

    public static IReadOnlyList<WfParamSet> BuildParamSpace(BacktestStrategyType type) =>
        type switch
        {
            BacktestStrategyType.MACrossover    => MaSpace(),
            BacktestStrategyType.RSI            => RsiSpace(),
            BacktestStrategyType.BollingerBands => BbSpace(),
            BacktestStrategyType.Breakout       => BreakoutSpace(),
            _                                   => []
        };

    private static List<WfParamSet> MaSpace()
    {
        int[]  fast = [5, 9, 14, 20];
        int[]  slow = [21, 34, 50, 100, 200];
        var    list = new List<WfParamSet>();
        foreach (var f in fast)
            foreach (var s in slow)
                if (s > f)
                    list.Add(Ps($"MA({f}/{s})", ("Fast", f), ("Slow", s)));
        return list;
    }

    private static List<WfParamSet> RsiSpace()
    {
        int[]     periods = [7, 9, 14, 21];
        decimal[] obs     = [65m, 70m, 75m];
        decimal[] oss     = [25m, 30m, 35m];
        var       list    = new List<WfParamSet>();
        foreach (var p in periods)
            foreach (var ob in obs)
                foreach (var os in oss)
                    list.Add(Ps($"RSI({p},OB={ob},OS={os})", ("Period", p), ("OB", ob), ("OS", os)));
        return list;
    }

    private static List<WfParamSet> BbSpace()
    {
        int[]     periods = [10, 20, 30];
        decimal[] mults   = [1.5m, 2.0m, 2.5m];
        var       list    = new List<WfParamSet>();
        foreach (var p in periods)
            foreach (var m in mults)
                list.Add(Ps($"BB({p},{m}σ)", ("Period", p), ("Mult", m)));
        return list;
    }

    private static List<WfParamSet> BreakoutSpace() =>
        new[] { 10, 15, 20, 30, 50 }
            .Select(p => Ps($"Breakout({p})", ("Period", p)))
            .ToList();

    // ── Fold segment layout (for Walk-Forward chart visualization) ───────────

    /// <summary>
    /// Returns the candle-index boundaries of each IS/OOS fold.
    /// Callers use these to draw colored bands on the equity chart.
    /// </summary>
    public static IReadOnlyList<WfFoldSegment> GetFoldSegments(
        int totalCandles,
        int folds        = 3,
        double inSamplePct = 0.70)
    {
        var result    = new List<WfFoldSegment>(folds);
        int foldSize  = totalCandles / Math.Max(1, folds);

        for (int fold = 0; fold < folds; fold++)
        {
            int start  = fold * foldSize;
            int end    = fold == folds - 1 ? totalCandles : start + foldSize;
            int split  = start + (int)((end - start) * inSamplePct);
            result.Add(new WfFoldSegment(fold + 1, start, split, end, totalCandles));
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WfParamSet Ps(string label, params (string k, decimal v)[] kvs) =>
        new(label, kvs.ToDictionary(x => x.k, x => x.v));
}

/// <summary>Describes one walk-forward fold with in-sample and out-of-sample segments.</summary>
public sealed record WfFoldSegment(
    int FoldNumber,
    int StartIndex,
    int SplitIndex,   // IS ends here, OOS begins
    int EndIndex,
    int TotalCandles)
{
    /// Normalised X position [0,1] for chart overlay
    public double XStart => (double)StartIndex / TotalCandles;
    public double XSplit => (double)SplitIndex / TotalCandles;
    public double XEnd   => (double)EndIndex   / TotalCandles;

    public double IsWidth  => XSplit - XStart;
    public double OosWidth => XEnd   - XSplit;
}
