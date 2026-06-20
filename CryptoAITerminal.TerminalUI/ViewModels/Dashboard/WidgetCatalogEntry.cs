using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

public sealed record WidgetCatalogEntry(string Key, string Title, int DefaultColSpan, int DefaultRowSpan);

public static class WidgetCatalog
{
    /// <summary>All widgets available on the dashboard. Phase 1 = first six; Phase 2 = nine market/portfolio widgets.</summary>
    public static readonly IReadOnlyList<WidgetCatalogEntry> All = new[]
    {
        new WidgetCatalogEntry("price-stats",   "Цена / Bid / Ask / Spread", 12, 1),
        new WidgetCatalogEntry("tracked-coins", "Отслеживаемые монеты",       4, 4),
        new WidgetCatalogEntry("price-chart",   "График цены",                8, 3),
        new WidgetCatalogEntry("liq-heatmap",   "Карта ликвидаций",           4, 2),
        new WidgetCatalogEntry("order-book",    "Стакан (Bids/Asks)",         4, 2),
        new WidgetCatalogEntry("sentiment",     "Настроение рынка",          12, 2),
        // Phase 2 — market & portfolio widgets
        new WidgetCatalogEntry("news",          "Новости",                    6, 3),
        new WidgetCatalogEntry("positions",     "Открытые позиции",           6, 3),
        new WidgetCatalogEntry("portfolio",     "Портфель",                   6, 2),
        new WidgetCatalogEntry("whales",        "Whale Tracker",              6, 3),
        new WidgetCatalogEntry("funding",       "Funding Rate",               4, 2),
        new WidgetCatalogEntry("analytics",     "PnL / Аналитика",            6, 3),
        new WidgetCatalogEntry("scanner",       "Сканер рынка",               6, 3),
        new WidgetCatalogEntry("gas",           "Газ (Gas)",                  4, 2),
        new WidgetCatalogEntry("tape",          "Лента сделок",               4, 3),
    };

    public static WidgetCatalogEntry? Find(string key)
    {
        foreach (var e in All) if (e.Key == key) return e;
        return null;
    }
}
