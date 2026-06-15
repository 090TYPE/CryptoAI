using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

// ── Computed metrics ─────────────────────────────────────────────────────────

public readonly record struct PnlMetrics(
    int     TotalTrades,
    int     WinCount,
    int     LossCount,
    decimal TotalPnlUsd,
    decimal WinRate,          // 0-100 percent
    decimal AvgWinUsd,
    decimal AvgLossUsd,       // absolute value
    decimal MaxDrawdownUsd,
    decimal MaxDrawdownPct,
    decimal ProfitFactor      // gross profit / gross loss
);

public readonly record struct PnlEquityPoint(DateTime Date, decimal Equity);

public readonly record struct PeriodRow(
    string  Label,
    int     Trades,
    int     Wins,
    decimal PnlUsd,
    decimal WinRate);

public readonly record struct SourceRow(
    string  Label,
    int     Trades,
    int     Wins,
    decimal PnlUsd,
    decimal WinRate);

// ── Service ──────────────────────────────────────────────────────────────────

public sealed class PnlDashboardService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CryptoAITerminal", "pnl-history.json");

    private readonly List<TradeRecord> _records = new();
    private readonly HashSet<string>   _knownIds = new(StringComparer.Ordinal);

    // ── Persistence ──────────────────────────────────────────────────────────

    public void Load()
    {
        if (!File.Exists(StoragePath)) return;
        try
        {
            var loaded = AtomicJsonFile.Read<List<TradeRecord>>(StoragePath);
            if (loaded is null) return;
            foreach (var r in loaded)
                if (_knownIds.Add(r.Id))
                    _records.Add(r);
        }
        catch
        {
            AtomicJsonFile.BackupCorruptFile(StoragePath);
        }
    }

    public void Save() => AtomicJsonFile.Write(StoragePath, _records, JsonOpts);

    // ── Write ─────────────────────────────────────────────────────────────────

    /// Raised once per newly recorded (post-dedup) trade. Lets the risk guard feed its
    /// daily-loss budget from real closed trades without coupling this store to it.
    public Action<TradeRecord>? OnTradeRecorded { get; set; }

    public void RecordTrade(TradeRecord record)
    {
        if (!_knownIds.Add(record.Id)) return; // de-dup
        _records.Add(record);
        Save();
        OnTradeRecorded?.Invoke(record);
    }

    /// Converts existing PaperTradeRecordViewModels (sniper history) to unified records.
    /// Uses DisplayName + OpenedAtLocal as a stable dedup key.
    public void ImportSniperTrades(IEnumerable<PaperTradeRecordViewModel> sniper)
    {
        bool dirty = false;
        foreach (var s in sniper)
        {
            var id = $"sniper:{s.TokenAddress}:{s.OpenedAtLocal:o}";
            if (!_knownIds.Add(id)) continue;

            var r = new TradeRecord
            {
                Id          = id,
                Source      = TradeSource.Sniper,
                Direction   = TradeDirection.Long,   // sniper is always a buy
                Symbol      = string.IsNullOrWhiteSpace(s.DisplayName) ? s.TokenAddress : s.DisplayName,
                Exchange    = string.IsNullOrWhiteSpace(s.DexId) ? s.ChainId : s.DexId,
                OpenedAtUtc = s.OpenedAtLocal.ToUniversalTime(),
                ClosedAtUtc = s.ClosedAtLocal.ToUniversalTime(),
                EntryPrice  = s.EntryPriceUsd,
                ExitPrice   = s.ExitPriceUsd,
                Quantity    = s.EntryAmountBnb,
                PnlUsd      = s.NetPnlNative,       // may be 0 for paper
                PnlPercent  = s.PnlPercent,
                ExitReason  = s.ExitReason,
                Notes       = s.ExecutionMode
            };
            _records.Add(r);
            dirty = true;
        }
        if (dirty) Save();
    }

    // ── Query / filter ────────────────────────────────────────────────────────

    public IReadOnlyList<TradeRecord> GetAll() => _records.AsReadOnly();

    public IReadOnlyList<TradeRecord> Filter(
        string period = "All",
        string source = "All")
    {
        var now = DateTime.UtcNow;
        IEnumerable<TradeRecord> q = _records;

        q = period switch
        {
            "Today"  => q.Where(r => r.ClosedAtUtc.Date == now.Date),
            "Week"   => q.Where(r => (now - r.ClosedAtUtc).TotalDays <= 7),
            "Month"  => q.Where(r => r.ClosedAtUtc.Year == now.Year && r.ClosedAtUtc.Month == now.Month),
            "Year"   => q.Where(r => r.ClosedAtUtc.Year == now.Year),
            _        => q
        };

        q = source switch
        {
            "Bot"    => q.Where(r => r.Source == TradeSource.Bot),
            "Sniper" => q.Where(r => r.Source == TradeSource.Sniper),
            "Manual" => q.Where(r => r.Source == TradeSource.Manual),
            "DEX"    => q.Where(r => r.Source == TradeSource.Dex),
            _        => q
        };

        return q.OrderByDescending(r => r.ClosedAtUtc).ToList();
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    public PnlMetrics ComputeMetrics(IReadOnlyList<TradeRecord> trades)
    {
        if (trades.Count == 0)
            return new PnlMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var wins   = trades.Where(t => t.IsWin).ToList();
        var losses = trades.Where(t => !t.IsWin).ToList();

        decimal grossProfit = wins.Sum(t => t.PnlUsd);
        decimal grossLoss   = losses.Sum(t => Math.Abs(t.PnlUsd));
        decimal totalPnl    = trades.Sum(t => t.PnlUsd);
        decimal winRate     = trades.Count == 0 ? 0m : (decimal)wins.Count / trades.Count * 100m;
        decimal avgWin      = wins.Count   == 0 ? 0m : wins.Average(t => t.PnlUsd);
        decimal avgLoss     = losses.Count == 0 ? 0m : losses.Average(t => Math.Abs(t.PnlUsd));
        decimal pf          = grossLoss == 0 ? (grossProfit > 0 ? 999m : 0m) : grossProfit / grossLoss;

        // Maximum drawdown on equity curve
        var sorted = trades.OrderBy(t => t.ClosedAtUtc).ToList();
        decimal peak = 0m, maxDrawUsd = 0m, equity = 0m;
        foreach (var t in sorted)
        {
            equity += t.PnlUsd;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDrawUsd) maxDrawUsd = dd;
        }
        decimal maxDrawPct = peak == 0m ? 0m : maxDrawUsd / Math.Abs(peak) * 100m;

        return new PnlMetrics(
            trades.Count, wins.Count, losses.Count,
            totalPnl, winRate, avgWin, avgLoss,
            maxDrawUsd, maxDrawPct, pf);
    }

    // ── Equity curve ──────────────────────────────────────────────────────────

    public IReadOnlyList<PnlEquityPoint> ComputeEquityCurve(IReadOnlyList<TradeRecord> trades)
    {
        if (trades.Count == 0) return [];

        var sorted = trades.OrderBy(t => t.ClosedAtUtc).ToList();
        var points = new List<PnlEquityPoint>(sorted.Count + 1);
        decimal cumulative = 0m;

        // Start at zero
        points.Add(new PnlEquityPoint(sorted[0].ClosedAtUtc, 0m));

        foreach (var t in sorted)
        {
            cumulative += t.PnlUsd;
            points.Add(new PnlEquityPoint(t.ClosedAtUtc, cumulative));
        }
        return points;
    }

    // ── Period breakdown ──────────────────────────────────────────────────────

    public IReadOnlyList<PeriodRow> ComputeByDay(IReadOnlyList<TradeRecord> trades, int maxDays = 30)
    {
        return trades
            .GroupBy(t => t.ClosedAtUtc.Date)
            .OrderByDescending(g => g.Key)
            .Take(maxDays)
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(t => t.IsWin);
                return new PeriodRow(
                    g.Key.ToString("dd MMM", CultureInfo.InvariantCulture),
                    list.Count, wins,
                    list.Sum(t => t.PnlUsd),
                    list.Count == 0 ? 0m : (decimal)wins / list.Count * 100m);
            })
            .ToList();
    }

    public IReadOnlyList<SourceRow> ComputeBySource(IReadOnlyList<TradeRecord> trades)
    {
        return trades
            .GroupBy(t => t.Source)
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(t => t.IsWin);
                return new SourceRow(
                    g.Key.ToString(),
                    list.Count, wins,
                    list.Sum(t => t.PnlUsd),
                    list.Count == 0 ? 0m : (decimal)wins / list.Count * 100m);
            })
            .OrderByDescending(r => Math.Abs(r.PnlUsd))
            .ToList();
    }

    /// <summary>Breakdown by specific bot name (Grid Bot, DCA Bot, AI Bot, CEX Arb, etc.).</summary>
    public IReadOnlyList<SourceRow> ComputeByBot(IReadOnlyList<TradeRecord> trades)
    {
        return trades
            .GroupBy(t => t.BotName ?? t.SourceLabel)
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(t => t.IsWin);
                return new SourceRow(
                    g.Key,
                    list.Count, wins,
                    list.Sum(t => t.PnlUsd),
                    list.Count == 0 ? 0m : (decimal)wins / list.Count * 100m);
            })
            .OrderByDescending(r => r.PnlUsd)
            .ToList();
    }

    /// <summary>Breakdown by exchange name.</summary>
    public IReadOnlyList<SourceRow> ComputeByExchange(IReadOnlyList<TradeRecord> trades)
    {
        return trades
            .Where(t => !string.IsNullOrEmpty(t.Exchange))
            .GroupBy(t => t.Exchange, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(t => t.IsWin);
                return new SourceRow(
                    g.Key,
                    list.Count, wins,
                    list.Sum(t => t.PnlUsd),
                    list.Count == 0 ? 0m : (decimal)wins / list.Count * 100m);
            })
            .OrderByDescending(r => r.PnlUsd)
            .ToList();
    }

    /// <summary>Breakdown by user-applied journal tag.</summary>
    public IReadOnlyList<SourceRow> ComputeByTag(IReadOnlyList<TradeRecord> trades)
    {
        return trades
            .GroupBy(t => t.Tag)
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(t => t.IsWin);
                return new SourceRow(
                    g.Key == JournalTag.None ? "Untagged" : g.Key.ToString(),
                    list.Count, wins,
                    list.Sum(t => t.PnlUsd),
                    list.Count == 0 ? 0m : (decimal)wins / list.Count * 100m);
            })
            .OrderByDescending(r => r.Trades)
            .ToList();
    }

    /// <summary>Breakdown by base asset (e.g. BTC, ETH, SOL).</summary>
    public IReadOnlyList<SourceRow> ComputeByAsset(IReadOnlyList<TradeRecord> trades)
    {
        return trades
            .GroupBy(t => t.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(t => t.IsWin);
                return new SourceRow(
                    g.Key,
                    list.Count, wins,
                    list.Sum(t => t.PnlUsd),
                    list.Count == 0 ? 0m : (decimal)wins / list.Count * 100m);
            })
            .OrderByDescending(r => Math.Abs(r.PnlUsd))
            .ToList();
    }

    // ── Equity curve with drawdown fill ──────────────────────────────────────

    /// <summary>
    /// Returns two SVG path strings for the equity chart:
    /// mainPath  — the equity line ("M x,y L ...")
    /// ddFillPath — closed area of drawdown-from-peak zones (filled red/translucent)
    /// Also returns the y-coordinate of the zero baseline (0..H range).
    /// </summary>
    public (string mainPath, string ddFillPath, double zeroY, string holdPath)
        ComputeEquityCurvePaths(IReadOnlyList<PnlEquityPoint> points,
                                decimal holdFinalPnl = 0m)
    {
        if (points.Count < 2) return ("", "", 65, "");

        const double W = 560, H = 130, Pad = 6;

        var values  = points.Select(p => (double)p.Equity).ToArray();
        double minEq = values.Min();
        double maxEq = values.Max();
        // Expand range to include hold line if needed
        if ((double)holdFinalPnl > maxEq) maxEq = (double)holdFinalPnl;
        if ((double)holdFinalPnl < minEq) minEq = (double)holdFinalPnl;
        double range = Math.Max(maxEq - minEq, 0.01);

        double ToY(double eq) =>
            (H - Pad) - (eq - minEq) / range * (H - Pad * 2) + Pad;

        double zeroY = ToY(0);
        int n = points.Count;

        // ── Main equity line ─────────────────────────────────────────────────
        var main = new StringBuilder("M");
        for (int i = 0; i < n; i++)
        {
            double x = i * W / (n - 1);
            double y = ToY(values[i]);
            main.Append($" {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        // ── Drawdown fill (area between rolling peak and equity when in DD) ──
        // Approach: running peak; whenever equity < peak, fill that segment.
        var ddFill = new StringBuilder();
        double peak = values[0];
        bool inDD   = false;
        int  ddStart = 0;

        for (int i = 0; i < n; i++)
        {
            if (values[i] > peak) peak = values[i];
            bool nowDD = values[i] < peak - 0.001;

            if (nowDD && !inDD)
            {
                inDD    = true;
                ddStart = i > 0 ? i - 1 : i;
            }
            else if (!nowDD && inDD)
            {
                // Close the drawdown segment
                AppendDdSegment(ddFill, values, ddStart, i, n, W, ToY);
                inDD = false;
            }
        }
        if (inDD) AppendDdSegment(ddFill, values, ddStart, n - 1, n, W, ToY);

        // ── Hold comparison line (gray dotted — straight from 0 to holdFinalPnl) ─
        string holdPath = "";
        if (holdFinalPnl != 0m)
        {
            double holdY = ToY((double)holdFinalPnl);
            holdPath = $"M 0,{zeroY.ToString("F1", CultureInfo.InvariantCulture)}" +
                       $" L {W.ToString("F1", CultureInfo.InvariantCulture)},{holdY.ToString("F1", CultureInfo.InvariantCulture)}";
        }

        return (main.ToString(), ddFill.ToString(), zeroY, holdPath);
    }

    private static void AppendDdSegment(StringBuilder sb, double[] values,
        int startIdx, int endIdx, int n, double W,
        Func<double, double> toY)
    {
        // Top edge = rolling peak; bottom = equity line
        // Simplified: draw rectangle from equity line to peak line for each point
        double peak = values[0];
        for (int i = 0; i <= endIdx; i++) if (values[i] > peak) peak = values[i];

        // Build a closed polygon: follow equity line forward, then peak backwards
        var seg = new StringBuilder("M");
        for (int i = startIdx; i <= endIdx; i++)
        {
            double x = i * W / (n - 1);
            double y = toY(values[i]);
            if (i == startIdx) seg.Append($" {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
            else               seg.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        // Return along peak (above equity)
        for (int i = endIdx; i >= startIdx; i--)
        {
            double x    = i * W / (n - 1);
            // Rolling peak up to point i
            double pkHere = values[0];
            for (int j = 0; j <= i; j++) if (values[j] > pkHere) pkHere = values[j];
            double y = toY(pkHere);
            seg.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        seg.Append(" Z");
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(seg);
    }

    // ── CSV export ────────────────────────────────────────────────────────────

    public string BuildCsv(IReadOnlyList<TradeRecord> trades)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date (UTC),Symbol,Exchange,Source,Direction,Entry Price,Exit Price,Quantity,P&L USD,P&L %,Duration,Exit Reason,Tag,Notes");

        foreach (var t in trades.OrderBy(t => t.ClosedAtUtc))
        {
            sb.Append(CsvField(t.ClosedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))); sb.Append(',');
            sb.Append(CsvField(t.Symbol));   sb.Append(',');
            sb.Append(CsvField(t.Exchange)); sb.Append(',');
            sb.Append(CsvField(t.SourceLabel));  sb.Append(',');
            sb.Append(CsvField(t.Direction.ToString())); sb.Append(',');
            sb.Append(t.EntryPrice.ToString("F6", CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(t.ExitPrice.ToString("F6",  CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(t.Quantity.ToString("F6",   CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(t.PnlUsd.ToString("F4",     CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(t.PnlPercent.ToString("F2", CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(CsvField(t.DurationLabel));   sb.Append(',');
            sb.Append(CsvField(t.ExitReason));      sb.Append(',');
            sb.Append(CsvField(t.Tag == JournalTag.None ? "" : t.Tag.ToString())); sb.Append(',');
            sb.AppendLine(CsvField(t.Notes));
        }
        return sb.ToString();
    }

    private static string CsvField(string s)
    {
        if (!s.Contains(',') && !s.Contains('"') && !s.Contains('\n'))
            return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
