using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

public sealed record WidgetCatalogEntry(string Key, string Title, int DefaultColSpan, int DefaultRowSpan);

public static class WidgetCatalog
{
    /// <summary>All widgets available on the dashboard. Phase 1 = first six; Phase 2 = nine market/portfolio widgets.</summary>
    public static readonly IReadOnlyList<WidgetCatalogEntry> All = new[]
    {
        new WidgetCatalogEntry("price-stats",   "OVERVIEW",                  12, 1),
        new WidgetCatalogEntry("tracked-coins", "WATCHLIST",                  4, 4),
        new WidgetCatalogEntry("price-chart",   "PRICE CHART",                8, 3),
        new WidgetCatalogEntry("liq-heatmap",   "LIQ MAP",                    4, 2),
        new WidgetCatalogEntry("order-book",    "ORDER BOOK",                 4, 2),
        new WidgetCatalogEntry("sentiment",     "MARKET SENTIMENT",          12, 2),
        // Phase 2 — market & portfolio widgets
        new WidgetCatalogEntry("news",          "NEWS FEED",                  6, 3),
        new WidgetCatalogEntry("positions",     "OPEN POSITIONS",             6, 3),
        new WidgetCatalogEntry("portfolio",     "PORTFOLIO",                  6, 2),
        new WidgetCatalogEntry("whales",        "WHALE TRACKER",              6, 3),
        new WidgetCatalogEntry("funding",       "FUNDING RATES",              4, 2),
        new WidgetCatalogEntry("analytics",     "P&L / ANALYTICS",            6, 3),
        new WidgetCatalogEntry("scanner",       "MARKET SCANNER",             6, 3),
        new WidgetCatalogEntry("gas",           "GAS MONITOR",                4, 2),
        new WidgetCatalogEntry("tape",          "LIVE TAPE",                  4, 3),
    };

    public static WidgetCatalogEntry? Find(string key)
    {
        foreach (var e in All) if (e.Key == key) return e;
        return null;
    }
}
