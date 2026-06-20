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
        _               => new TextBlock { Text = "Unknown widget" },
    };
}
