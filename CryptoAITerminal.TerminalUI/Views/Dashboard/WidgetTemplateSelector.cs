using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CryptoAITerminal.TerminalUI.ViewModels;
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
        // Server feed — shared AI digests + this user's events
        "ai-feed"       => new AiFeedWidget(),
        _               => new TextBlock { Text = "Unknown widget" },
    };

    /// <summary>
    /// The DataContext a widget body runs on. Widgets whose bindings stay inside a single
    /// sub-view-model get that sub-view-model, so their XAML can be compiled against it instead
    /// of against the whole MainWindowViewModel; the composite ones (Sentiment) and the ones that
    /// read MainWindowViewModel state directly (the price chart, whose timeframe strip is the
    /// trading desk's, not the feed's) still get the god object.
    /// Kept next to <see cref="Build"/> so a new widget cannot get a control without a context.
    /// </summary>
    public static object ResolveDataContext(string key, MainWindowViewModel main) => key switch
    {
        "liq-heatmap"   => main.LiquidationHeatmapVM,
        "sentiment"     => main.SentimentScreenVM,
        // Market feed: the quote strip, the book and the tracked list read nothing but the feed.
        "price-stats"   => main.MarketFeedVM,
        "order-book"    => main.MarketFeedVM,
        "tracked-coins" => main.MarketFeedVM,
        "news"          => main.NewsFeedVM,
        "positions"     => main.AllPositionsVM,
        "portfolio"     => main.WalletVM,
        "whales"        => main.WhaleTrackerVM,
        "funding"       => main.FundingRateVM,
        "analytics"     => main.PnlDashboardVM,
        "scanner"       => main.ScannerVM,
        "gas"           => main.GasMonitorVM,
        "tape"          => main.MarketTapeVM,
        "ai-feed"       => main.AiFeedVM,
        _               => main,
    };
}
