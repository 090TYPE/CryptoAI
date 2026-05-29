using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Computes Pearson correlation coefficients between assets based on their
/// price return history.  Feed daily/intraday close prices via AddSample,
/// then call Compute to get the full N×N matrix.
/// </summary>
public sealed class CorrelationMatrixService
{
    private const int MaxSamplesPerAsset = 365;

    // symbol → list of (timestamp, close)
    private readonly Dictionary<string, List<(DateTime ts, decimal close)>> _history =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Data ingestion ────────────────────────────────────────────────────────

    public void AddSample(string symbol, DateTime timestamp, decimal close)
    {
        if (!_history.TryGetValue(symbol, out var list))
        {
            list = new List<(DateTime, decimal)>();
            _history[symbol] = list;
        }

        list.Add((timestamp, close));

        // Keep sorted and capped
        list.Sort((a, b) => a.ts.CompareTo(b.ts));
        if (list.Count > MaxSamplesPerAsset)
            list.RemoveAt(0);
    }

    public void AddSamples(string symbol, IEnumerable<(DateTime ts, decimal close)> samples)
    {
        foreach (var (ts, close) in samples)
            AddSample(symbol, ts, close);
    }

    public IReadOnlyList<string> TrackedSymbols => _history.Keys.ToList();

    // ── Computation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes Pearson correlation for all symbol pairs.
    /// Uses daily log-returns aligned by nearest timestamp.
    /// </summary>
    public CorrelationMatrix Compute(int minOverlapSamples = 10)
    {
        var symbols = _history.Keys.OrderBy(s => s).ToList();
        var returns = symbols.ToDictionary(
            s => s,
            s => ComputeLogReturns(_history[s]),
            StringComparer.OrdinalIgnoreCase);

        var n     = symbols.Count;
        var cells = new List<CorrelationCell>(n * n);

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                {
                    cells.Add(new CorrelationCell(symbols[i], symbols[j], 1m, returns[symbols[i]].Count));
                    continue;
                }

                var r = PearsonCorrelation(
                    returns[symbols[i]],
                    returns[symbols[j]],
                    out var overlap);

                cells.Add(new CorrelationCell(symbols[i], symbols[j],
                    overlap >= minOverlapSamples ? r : 0m, overlap));
            }
        }

        return new CorrelationMatrix(symbols, cells);
    }

    // ── Math helpers ──────────────────────────────────────────────────────────

    private static List<decimal> ComputeLogReturns(List<(DateTime ts, decimal close)> prices)
    {
        var result = new List<decimal>(prices.Count);
        for (var i = 1; i < prices.Count; i++)
        {
            var prev = prices[i - 1].close;
            var curr = prices[i].close;
            if (prev > 0m && curr > 0m)
                result.Add((decimal)Math.Log((double)(curr / prev)));
        }
        return result;
    }

    private static decimal PearsonCorrelation(
        List<decimal> x, List<decimal> y, out int overlap)
    {
        overlap = Math.Min(x.Count, y.Count);
        if (overlap < 2) return 0m;

        var n  = overlap;
        var xs = x.TakeLast(n).ToList();
        var ys = y.TakeLast(n).ToList();

        var xMean = xs.Average();
        var yMean = ys.Average();

        var num  = 0m;
        var denX = 0m;
        var denY = 0m;

        for (var i = 0; i < n; i++)
        {
            var dx = xs[i] - xMean;
            var dy = ys[i] - yMean;
            num  += dx * dy;
            denX += dx * dx;
            denY += dy * dy;
        }

        var denom = (decimal)Math.Sqrt((double)(denX * denY));
        return denom == 0m ? 0m : Math.Round(num / denom, 4);
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed record CorrelationCell(
    string  SymbolA,
    string  SymbolB,
    decimal Coefficient,   // -1 to +1
    int     SampleOverlap)
{
    /// <summary>Color for heatmap: red = high positive, green = low/negative correlation.</summary>
    public string HeatmapBrush => Coefficient switch
    {
        >= 0.8m  => "#FF4444",   // very high — red
        >= 0.6m  => "#FF8844",   // high — orange
        >= 0.4m  => "#FFCC44",   // moderate — yellow
        >= 0.0m  => "#88CC88",   // low positive — light green
        >= -0.4m => "#44AA88",   // low negative — teal
        _        => "#2288AA"    // strong negative — blue
    };

    public string Label => $"{Coefficient:+0.00;-0.00}";
    public bool IsHighlyCorrelated => Math.Abs(Coefficient) >= 0.7m;
}

public sealed class CorrelationMatrix
{
    public IReadOnlyList<string>          Symbols { get; }
    public IReadOnlyList<CorrelationCell> Cells   { get; }

    public CorrelationMatrix(
        IReadOnlyList<string>          symbols,
        IReadOnlyList<CorrelationCell> cells)
    {
        Symbols = symbols;
        Cells   = cells;
    }

    public decimal GetCorrelation(string a, string b) =>
        Cells.FirstOrDefault(c =>
            string.Equals(c.SymbolA, a, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.SymbolB, b, StringComparison.OrdinalIgnoreCase))?.Coefficient ?? 0m;

    /// <summary>All pairs where |correlation| >= threshold.</summary>
    public IReadOnlyList<CorrelationCell> GetHighCorrelationPairs(decimal threshold = 0.7m) =>
        Cells
            .Where(c => c.SymbolA != c.SymbolB && Math.Abs(c.Coefficient) >= threshold)
            .OrderByDescending(c => Math.Abs(c.Coefficient))
            .ToList();

    /// <summary>Warning text for risk management dashboard.</summary>
    public string RiskWarning
    {
        get
        {
            var high = GetHighCorrelationPairs(0.8m);
            if (high.Count == 0) return string.Empty;
            var pairs = string.Join(", ", high.Take(3).Select(c => $"{c.SymbolA}/{c.SymbolB} ({c.Label})"));
            return $"High correlation detected: {pairs}. Consider reducing position sizes.";
        }
    }
}
