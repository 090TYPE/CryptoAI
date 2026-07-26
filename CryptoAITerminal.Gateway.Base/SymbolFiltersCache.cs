using System;
using System.Collections.Concurrent;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.Base;

/// <summary>
/// Session-lifetime symbol → filters map. Deliberately has no TTL: exchanges change a tick size or
/// a lot step once in months and announce it, while the endpoints that publish them are the heavy
/// ones (Binance USD-M returns every symbol in one response), so re-fetching per order would cost
/// far more than the staleness is worth. A gateway lives for one session; restarting the app
/// re-reads everything.
///
/// Only successful lookups are stored — a failed one must be retried on the next order rather than
/// poisoning the symbol for the rest of the session.
/// </summary>
public sealed class SymbolFiltersCache
{
    private readonly ConcurrentDictionary<string, SymbolFilters> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    /// <summary>Cached filters for the symbol, or null when it has not been looked up yet.</summary>
    public SymbolFilters? Get(string symbol)
        => !string.IsNullOrWhiteSpace(symbol) && _entries.TryGetValue(symbol, out var filters) ? filters : null;

    /// <summary>Stores under an explicit key, for venues whose response is keyed by their own symbol form.</summary>
    public void Store(string symbol, SymbolFilters filters)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        _entries[symbol] = filters;
    }

    /// <summary>Stores under <see cref="SymbolFilters.Symbol"/>.</summary>
    public void Store(SymbolFilters filters) => Store(filters.Symbol, filters);

    public void Clear() => _entries.Clear();
}
