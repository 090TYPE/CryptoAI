using System;
using System.Collections.Generic;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Covers the deterministic HTML report builder (no file IO).
/// </summary>
public class BacktestReportExporterTests
{
    private static BacktestReportExporter.ReportModel SampleModel() => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1H",
        StrategyName = "SMA(9/21)",
        Commission = 0.1m,
        GeneratedAt = new DateTime(2026, 6, 1, 12, 0, 0),
        DateRange = "01.03.26 — 01.06.26",
        TradeCount = 42,
        WinRatePercent = 57.5m,
        NetReturnPercent = 23.4m,
        MaxDrawdownPercent = 12.1m,
        SharpeRatio = 1.8m,
        BestTradePercent = 9.2m,
        WorstTradePercent = -4.3m,
        Equity = new List<BacktestReportExporter.EquityPoint>
        {
            new(new DateTime(2026, 3, 1), 100m),
            new(new DateTime(2026, 4, 1), 110m),
            new(new DateTime(2026, 5, 1), 123.4m),
        },
        BuyHold = new List<decimal> { 100m, 105m, 108m },
        Comparison = new List<BacktestReportExporter.ComparisonRow>
        {
            new("Swing SMA(9/21)", "42", "57.5%", "+23.4%", "12.1%", "1.80", "+9.2%", true),
            new("Trend SMA(50/200)", "8", "50%", "+5.1%", "20.0%", "0.40", "+12%", false),
        },
        MonteCarloSummary = "Monte Carlo 100× | mean +18%\n90% CI [-3% … +41%]"
    };

    [Fact]
    public void BuildHtml_ProducesWellFormedDocument()
    {
        var html = BacktestReportExporter.BuildHtml(SampleModel());

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("CRYPTO AI TERMINAL", html);
    }

    [Fact]
    public void BuildHtml_IncludesKeyMetricsAndChart()
    {
        var html = BacktestReportExporter.BuildHtml(SampleModel());

        Assert.Contains("BTCUSDT", html);
        Assert.Contains("+23.4%", html);          // net return
        Assert.Contains("1.8", html);             // sharpe
        Assert.Contains("<svg", html);            // equity chart
        Assert.Contains("<polyline", html);       // plotted curve
        Assert.Contains("Monte Carlo", html);     // MC section
        Assert.Contains("Swing SMA(9/21)", html); // comparison row
    }

    [Fact]
    public void BuildHtml_EscapesHtmlSpecialCharacters()
    {
        var model = SampleModel() with { StrategyName = "A<b> & \"c\"" };
        var html = BacktestReportExporter.BuildHtml(model);

        Assert.Contains("A&lt;b&gt; &amp; &quot;c&quot;", html);
        Assert.DoesNotContain("A<b> &", html);
    }

    [Fact]
    public void BuildHtml_WithTooFewEquityPoints_DegradesGracefully()
    {
        var model = SampleModel() with
        {
            Equity = new List<BacktestReportExporter.EquityPoint>
            {
                new(new DateTime(2026, 3, 1), 100m),
            },
            BuyHold = new List<decimal>()
        };

        var html = BacktestReportExporter.BuildHtml(model);

        Assert.Contains("Not enough data to plot", html);
        Assert.Contains("</html>", html);
    }
}
