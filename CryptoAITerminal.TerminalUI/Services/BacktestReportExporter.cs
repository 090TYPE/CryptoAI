using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Renders a self-contained, shareable HTML backtest report (inline SVG equity
/// curve + metrics + strategy comparison + Monte Carlo). No external
/// dependencies — the file opens in any browser and prints cleanly to PDF.
/// </summary>
public static class BacktestReportExporter
{
    public sealed record EquityPoint(DateTime Time, decimal Value);

    public sealed record ComparisonRow(
        string Name, string Trades, string WinRate, string NetReturn,
        string MaxDD, string Sharpe, string BestTrade, bool IsSelected);

    public sealed record ReportModel
    {
        public string Symbol         { get; init; } = "";
        public string Timeframe      { get; init; } = "";
        public string StrategyName   { get; init; } = "";
        public decimal Commission    { get; init; }
        public DateTime GeneratedAt  { get; init; } = DateTime.Now;
        public string DateRange      { get; init; } = "";

        public int     TradeCount        { get; init; }
        public decimal WinRatePercent    { get; init; }
        public decimal NetReturnPercent  { get; init; }
        public decimal MaxDrawdownPercent{ get; init; }
        public decimal SharpeRatio       { get; init; }
        public decimal BestTradePercent  { get; init; }
        public decimal WorstTradePercent { get; init; }

        public IReadOnlyList<EquityPoint> Equity   { get; init; } = [];
        public IReadOnlyList<decimal>     BuyHold  { get; init; } = [];
        public IReadOnlyList<ComparisonRow> Comparison { get; init; } = [];
        public string MonteCarloSummary  { get; init; } = "";
    }

    private const double ChartW = 760d;
    private const double ChartH = 280d;

    public static string BuildHtml(ReportModel m)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.Append("""
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Crypto AI Terminal — Backtest Report</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin:0; background:#070F18; color:#D7E3EE;
         font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif; padding:32px; }
  .wrap { max-width:880px; margin:0 auto; }
  header { display:flex; align-items:center; justify-content:space-between;
           border-bottom:1px solid #1A3A50; padding-bottom:16px; margin-bottom:24px; }
  .brand { font-size:20px; font-weight:700; color:#1FE6C2; letter-spacing:.5px; }
  .sub { color:#6E8498; font-size:12px; }
  h1 { font-size:22px; margin:0 0 4px; }
  h2 { font-size:15px; color:#9FB2C4; margin:28px 0 12px; text-transform:uppercase; letter-spacing:.6px; }
  .kpis { display:grid; grid-template-columns:repeat(4,1fr); gap:12px; }
  .kpi { background:#0B1622; border:1px solid #1A3A50; border-radius:8px; padding:14px; }
  .kpi .label { color:#6E8498; font-size:11px; text-transform:uppercase; letter-spacing:.5px; }
  .kpi .value { font-size:22px; font-weight:700; margin-top:4px; }
  .pos { color:#3DDC84; } .neg { color:#FF5D73; } .neu { color:#D7E3EE; }
  table { width:100%; border-collapse:collapse; font-size:13px; }
  th,td { text-align:right; padding:8px 10px; border-bottom:1px solid #142838; }
  th:first-child, td:first-child { text-align:left; }
  th { color:#6E8498; font-weight:600; text-transform:uppercase; font-size:11px; }
  tr.sel td { background:#0E2A24; }
  .chartbox { background:#0B1622; border:1px solid #1A3A50; border-radius:8px; padding:12px; }
  .legend { font-size:12px; color:#9FB2C4; margin-top:8px; }
  .legend .dot { display:inline-block; width:10px; height:3px; vertical-align:middle; margin:0 6px 0 16px; }
  pre { background:#0B1622; border:1px solid #1A3A50; border-radius:8px; padding:14px;
        white-space:pre-wrap; font-size:13px; color:#C5D2DE; }
  footer { margin-top:32px; color:#48586A; font-size:11px; text-align:center;
           border-top:1px solid #142838; padding-top:14px; }
  @media print { body { background:#fff; color:#111; } .kpi,.chartbox,pre,table tr.sel td { background:#fff; } }
</style></head><body><div class="wrap">
""");

        // Header
        sb.Append("<header><div><div class=\"brand\">CRYPTO AI TERMINAL</div>")
          .Append("<div class=\"sub\">Backtest report</div></div><div class=\"sub\" style=\"text-align:right\">")
          .Append(Esc(m.GeneratedAt.ToString("yyyy-MM-dd HH:mm", inv))).Append("</div></header>");

        sb.Append("<h1>").Append(Esc(m.Symbol)).Append(" · ").Append(Esc(m.Timeframe))
          .Append("</h1><div class=\"sub\">").Append(Esc(m.StrategyName))
          .Append(" · commission ").Append(m.Commission.ToString("0.##", inv)).Append("% (round-trip ")
          .Append((m.Commission * 2).ToString("0.##", inv)).Append("%)");
        if (!string.IsNullOrWhiteSpace(m.DateRange))
            sb.Append(" · ").Append(Esc(m.DateRange));
        sb.Append("</div>");

        // KPIs
        sb.Append("<h2>Performance</h2><div class=\"kpis\">");
        Kpi(sb, "Net return", Pct(m.NetReturnPercent, inv), ColorClass(m.NetReturnPercent));
        Kpi(sb, "Win rate", m.WinRatePercent.ToString("0.#", inv) + "%", "neu");
        Kpi(sb, "Max drawdown", m.MaxDrawdownPercent.ToString("0.##", inv) + "%", "neg");
        Kpi(sb, "Sharpe (ann.)", m.SharpeRatio.ToString("0.##", inv), m.SharpeRatio >= 1 ? "pos" : "neu");
        Kpi(sb, "Trades", m.TradeCount.ToString(inv), "neu");
        Kpi(sb, "Best trade", Pct(m.BestTradePercent, inv), ColorClass(m.BestTradePercent));
        Kpi(sb, "Worst trade", Pct(m.WorstTradePercent, inv), ColorClass(m.WorstTradePercent));
        Kpi(sb, "Symbol", Esc(m.Symbol), "neu");
        sb.Append("</div>");

        // Equity chart
        sb.Append("<h2>Equity curve</h2><div class=\"chartbox\">");
        sb.Append(BuildSvg(m, inv));
        sb.Append("<div class=\"legend\"><span class=\"dot\" style=\"background:#1FE6C2\"></span>Strategy")
          .Append("<span class=\"dot\" style=\"background:#6E8498\"></span>Buy &amp; hold</div></div>");

        // Comparison
        if (m.Comparison.Count > 0)
        {
            sb.Append("<h2>Strategy comparison</h2><table><thead><tr>")
              .Append("<th>Strategy</th><th>Trades</th><th>Win</th><th>Net</th><th>Max DD</th><th>Sharpe</th><th>Best</th>")
              .Append("</tr></thead><tbody>");
            foreach (var r in m.Comparison)
            {
                sb.Append(r.IsSelected ? "<tr class=\"sel\">" : "<tr>");
                sb.Append("<td>").Append(Esc(r.Name)).Append(r.IsSelected ? " ★" : "").Append("</td>")
                  .Append("<td>").Append(Esc(r.Trades)).Append("</td>")
                  .Append("<td>").Append(Esc(r.WinRate)).Append("</td>")
                  .Append("<td>").Append(Esc(r.NetReturn)).Append("</td>")
                  .Append("<td>").Append(Esc(r.MaxDD)).Append("</td>")
                  .Append("<td>").Append(Esc(r.Sharpe)).Append("</td>")
                  .Append("<td>").Append(Esc(r.BestTrade)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        // Monte Carlo
        if (!string.IsNullOrWhiteSpace(m.MonteCarloSummary))
        {
            sb.Append("<h2>Monte Carlo</h2><pre>").Append(Esc(m.MonteCarloSummary)).Append("</pre>");
        }

        sb.Append("<footer>Generated by Crypto AI Terminal · Past performance does not guarantee future results. ")
          .Append("Backtests exclude slippage and may differ from live execution.</footer>");

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void Kpi(StringBuilder sb, string label, string value, string cls)
    {
        sb.Append("<div class=\"kpi\"><div class=\"label\">").Append(Esc(label))
          .Append("</div><div class=\"value ").Append(cls).Append("\">").Append(value).Append("</div></div>");
    }

    private static string BuildSvg(ReportModel m, CultureInfo inv)
    {
        if (m.Equity.Count < 2)
            return "<div class=\"sub\">Not enough data to plot.</div>";

        var stratVals = m.Equity.Select(p => (double)p.Value).ToList();
        var bhVals    = m.BuyHold.Select(v => (double)v).ToList();

        var all = new List<double>(stratVals);
        if (bhVals.Count >= 2) all.AddRange(bhVals);
        var min = all.Min();
        var max = all.Max();
        var pad = (max - min) * 0.05;
        min -= pad; max += pad;
        var range = Math.Max(max - min, 0.001);

        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append((int)ChartW).Append(' ').Append((int)ChartH)
          .Append("\" width=\"100%\" preserveAspectRatio=\"none\" xmlns=\"http://www.w3.org/2000/svg\">");

        // baseline at value=100 (start equity) if in range
        if (100.0 >= min && 100.0 <= max)
        {
            var y0 = Y(100.0, min, range);
            sb.Append("<line x1=\"0\" y1=\"").Append(F(y0)).Append("\" x2=\"").Append(F(ChartW))
              .Append("\" y2=\"").Append(F(y0)).Append("\" stroke=\"#23384A\" stroke-dasharray=\"4,4\" stroke-width=\"1\"/>");
        }

        if (bhVals.Count >= 2)
            sb.Append("<polyline fill=\"none\" stroke=\"#6E8498\" stroke-width=\"1.5\" stroke-dasharray=\"5,4\" points=\"")
              .Append(Points(bhVals, min, range, inv)).Append("\"/>");

        sb.Append("<polyline fill=\"none\" stroke=\"#1FE6C2\" stroke-width=\"2\" points=\"")
          .Append(Points(stratVals, min, range, inv)).Append("\"/>");

        // axis labels
        sb.Append("<text x=\"6\" y=\"14\" fill=\"#6E8498\" font-size=\"11\">")
          .Append(Pct((decimal)(max - pad), inv)).Append("</text>");
        sb.Append("<text x=\"6\" y=\"").Append((int)ChartH - 6).Append("\" fill=\"#6E8498\" font-size=\"11\">")
          .Append(Pct((decimal)(min + pad), inv)).Append("</text>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Points(List<double> values, double min, double range, CultureInfo inv)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            double x = i / (double)(values.Count - 1) * ChartW;
            double y = Y(values[i], min, range);
            if (i > 0) sb.Append(' ');
            sb.Append(x.ToString("F1", inv)).Append(',').Append(y.ToString("F1", inv));
        }
        return sb.ToString();
    }

    private static double Y(double value, double min, double range) =>
        Math.Clamp(ChartH - (value - min) / range * ChartH, 0, ChartH);

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static string Pct(decimal v, CultureInfo inv) => v.ToString("+0.##;-0.##;0", inv) + "%";

    private static string ColorClass(decimal v) => v > 0 ? "pos" : v < 0 ? "neg" : "neu";

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
