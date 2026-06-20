using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;
using CryptoAITerminal.TerminalUI.Views.Dashboard.Widgets;

namespace CryptoAITerminal.TerminalUI.Views.Dashboard;

/// <summary>Maps a DashboardWidget.Key to the concrete widget control.</summary>
public sealed class WidgetTemplateSelector : IDataTemplate
{
    public bool Match(object? data) => data is DashboardWidget;

    public Control? Build(object? data) => (data as DashboardWidget)?.Key switch
    {
        "price-stats"   => new PriceStatsWidget(),
        "tracked-coins" => new TrackedCoinsWidget(),
        "price-chart"   => new PriceChartWidget(),
        "liq-heatmap"   => new LiqHeatmapWidget(),
        "order-book"    => new OrderBookWidget(),
        "sentiment"     => new SentimentWidget(),
        // Phase 2 — market & portfolio widgets
        "news"          => new NewsWidget(),
        "positions"     => new PositionsWidget(),
        "portfolio"     => new PortfolioWidget(),
        "whales"        => new WhalesWidget(),
        "funding"       => new FundingWidget(),
        "analytics"     => new AnalyticsWidget(),
        "scanner"       => new ScannerWidget(),
        "gas"           => new GasWidget(),
        "tape"          => new TapeWidget(),
        _               => new TextBlock { Text = "Unknown widget" },
    };
}
