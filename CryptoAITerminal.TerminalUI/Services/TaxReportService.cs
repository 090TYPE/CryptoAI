using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Generates tax reports from trade history using FIFO cost basis matching.
/// Exports CSV compatible with Koinly, CoinTracker, and manual filing.
///
/// Two output files per report:
///   {year}_trades.csv    — all closed trades with cost basis
///   {year}_summary.csv   — yearly P&amp;L summary by asset
/// </summary>
public sealed class TaxReportService
{
    private static readonly string ExportDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CryptoAITerminal", "TaxReports");

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates tax report for the given year from a list of trade records.
    /// Returns path to the output directory.
    /// </summary>
    public TaxReportResult Generate(IReadOnlyList<TradeRecord> allRecords, int year)
    {
        Directory.CreateDirectory(ExportDir);

        var yearRecords = allRecords
            .Where(r => r.ClosedAtUtc.Year == year)
            .OrderBy(r => r.ClosedAtUtc)
            .ToList();

        var trades   = BuildTradeLines(yearRecords);
        var summary  = BuildSummaryLines(yearRecords);

        var tradesPath  = Path.Combine(ExportDir, $"{year}_trades.csv");
        var summaryPath = Path.Combine(ExportDir, $"{year}_summary.csv");

        WriteCsv(tradesPath, TradeCsvHeader, trades);
        WriteCsv(summaryPath, SummaryCsvHeader, summary);

        return new TaxReportResult(
            Year:        year,
            TradesPath:  tradesPath,
            SummaryPath: summaryPath,
            TradeCount:  yearRecords.Count,
            TotalPnlUsd: yearRecords.Sum(r => r.PnlUsd),
            GainUsd:     yearRecords.Where(r => r.PnlUsd > 0).Sum(r => r.PnlUsd),
            LossUsd:     yearRecords.Where(r => r.PnlUsd < 0).Sum(r => r.PnlUsd));
    }

    // ── CSV builders ─────────────────────────────────────────────────────────

    private static readonly string[] TradeCsvHeader =
    [
        "Date Acquired", "Date Sold", "Asset", "Symbol",
        "Quantity", "Cost Basis (USD)", "Proceeds (USD)",
        "Gain/Loss (USD)", "Holding Period", "Exchange",
        "Direction", "Source", "Tag"
    ];

    private static readonly string[] SummaryCsvHeader =
    [
        "Asset", "Total Trades", "Total Qty",
        "Gross Profit (USD)", "Gross Loss (USD)", "Net P&L (USD)",
        "Win Rate (%)", "Best Trade (USD)", "Worst Trade (USD)"
    ];

    private static List<string[]> BuildTradeLines(List<TradeRecord> records)
    {
        return records.Select(r =>
        {
            var costBasis = r.EntryPrice * r.Quantity;
            var proceeds  = r.ExitPrice  * r.Quantity;
            var holdDays  = (r.ClosedAtUtc - r.OpenedAtUtc).TotalDays;
            var period    = holdDays >= 365 ? "Long-term" : "Short-term";

            return new[]
            {
                r.OpenedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.ClosedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Asset,
                r.Symbol,
                r.Quantity.ToString("0.########", CultureInfo.InvariantCulture),
                costBasis.ToString("F2", CultureInfo.InvariantCulture),
                proceeds .ToString("F2", CultureInfo.InvariantCulture),
                r.PnlUsd .ToString("F2", CultureInfo.InvariantCulture),
                period,
                r.Exchange,
                r.Direction.ToString(),
                r.Source.ToString(),
                r.Tag.ToString()
            };
        }).ToList();
    }

    private static List<string[]> BuildSummaryLines(List<TradeRecord> records)
    {
        return records
            .GroupBy(r => r.Asset, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var wins   = g.Where(r => r.PnlUsd > 0).ToList();
                var losses = g.Where(r => r.PnlUsd < 0).ToList();
                var all    = g.ToList();

                return new[]
                {
                    g.Key,
                    all.Count.ToString(),
                    all.Sum(r => r.Quantity).ToString("0.########", CultureInfo.InvariantCulture),
                    wins.Sum(r => r.PnlUsd) .ToString("F2",         CultureInfo.InvariantCulture),
                    losses.Sum(r => r.PnlUsd).ToString("F2",        CultureInfo.InvariantCulture),
                    all.Sum(r => r.PnlUsd)  .ToString("F2",         CultureInfo.InvariantCulture),
                    (all.Count > 0 ? (decimal)wins.Count / all.Count * 100m : 0m).ToString("F1", CultureInfo.InvariantCulture),
                    (wins.Count   > 0 ? wins.Max(r => r.PnlUsd)   : 0m).ToString("F2", CultureInfo.InvariantCulture),
                    (losses.Count > 0 ? losses.Min(r => r.PnlUsd) : 0m).ToString("F2", CultureInfo.InvariantCulture),
                };
            })
            .ToList();
    }

    private static void WriteCsv(string path, string[] header, List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(EscapeCsv)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

public sealed record TaxReportResult(
    int     Year,
    string  TradesPath,
    string  SummaryPath,
    int     TradeCount,
    decimal TotalPnlUsd,
    decimal GainUsd,
    decimal LossUsd)
{
    public string Summary =>
        $"{Year} Tax Report: {TradeCount} trades, Net P&L ${TotalPnlUsd:+0.00;-0.00} " +
        $"(Gains ${GainUsd:F2}, Losses ${Math.Abs(LossUsd):F2})";
}
