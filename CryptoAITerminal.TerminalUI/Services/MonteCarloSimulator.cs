using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

public readonly record struct MonteCarloResult(
    bool IsReady,
    int RequestedRuns,
    int CompletedRuns,
    decimal MeanReturnPercent,
    decimal MedianReturnPercent,
    decimal StdDevPercent,
    decimal Percentile5,
    decimal Percentile95,
    decimal BestReturnPercent,
    decimal WorstReturnPercent,
    decimal MeanDrawdownPercent,
    decimal ProfitableRunsPercent,
    IReadOnlyList<decimal> SortedReturns,
    string Message)
{
    public static MonteCarloResult Empty(string message) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<decimal>(), message);
}

public static class MonteCarloSimulator
{
    /// <summary>
    /// Прогоняет стратегию N раз на случайных подвыборках свечей (без замены, с сохранением хронологии).
    /// Возвращает распределение чистых доходностей: среднее, медиану, σ, 5/95 перцентили, % прибыльных прогонов.
    /// </summary>
    public static MonteCarloResult Run(
        Func<IStrategy> strategyFactory,
        IReadOnlyList<DexOhlcvPoint> candles,
        int runs,
        double subsampleFraction,
        decimal commissionPercent = 0.1m,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(strategyFactory);
        ArgumentNullException.ThrowIfNull(candles);

        if (candles.Count < 10)
            return MonteCarloResult.Empty("Monte Carlo требует минимум 10 свечей.");
        if (runs <= 0)
            return MonteCarloResult.Empty("Число прогонов должно быть положительным.");

        var fraction = Math.Clamp(subsampleFraction, 0.1, 1.0);
        var sampleSize = Math.Max(10, (int)(candles.Count * fraction));
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();

        var returns   = new List<decimal>(runs);
        var drawdowns = new List<decimal>(runs);

        for (var i = 0; i < runs; i++)
        {
            var sampled = SampleSubset(candles, sampleSize, rng);
            var result  = BacktestEngine.Run(strategyFactory(), sampled, commissionPercent);
            if (!result.IsReady) continue;
            returns.Add(result.NetReturnPercent);
            drawdowns.Add(result.MaxDrawdownPercent);
        }

        if (returns.Count == 0)
            return MonteCarloResult.Empty("Ни один прогон не дал сделок — стратегия не сработала на подвыборках.");

        var sorted     = returns.OrderBy(r => r).ToList();
        var mean       = sorted.Average();
        var median     = Percentile(sorted, 0.5);
        var stdDev     = ComputeStdDev(sorted, mean);
        var p5         = Percentile(sorted, 0.05);
        var p95        = Percentile(sorted, 0.95);
        var meanDd     = drawdowns.Average();
        var winPercent = (decimal)returns.Count(r => r > 0) / returns.Count * 100m;

        return new MonteCarloResult(
            true, runs, returns.Count,
            mean, median, stdDev, p5, p95,
            sorted[^1], sorted[0],
            meanDd, winPercent,
            sorted,
            $"{returns.Count}/{runs} прогонов завершено, подвыборка {sampleSize}/{candles.Count} свечей.");
    }

    private static IReadOnlyList<DexOhlcvPoint> SampleSubset(
        IReadOnlyList<DexOhlcvPoint> candles, int sampleSize, Random rng)
    {
        var indices = new List<int>(candles.Count);
        for (var i = 0; i < candles.Count; i++) indices.Add(i);

        for (var i = indices.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        return indices.Take(sampleSize).OrderBy(i => i).Select(i => candles[i]).ToList();
    }

    private static decimal ComputeStdDev(IReadOnlyList<decimal> values, decimal mean)
    {
        if (values.Count < 2) return 0m;
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return (decimal)Math.Sqrt((double)variance);
    }

    private static decimal Percentile(IReadOnlyList<decimal> sorted, double p)
    {
        if (sorted.Count == 0) return 0m;
        var index = Math.Clamp((int)Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }
}
