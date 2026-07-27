using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Trading-desk redesign additions: per-venue price switcher (same token across
/// Binance/Bybit/OKX/KuCoin) and the polling that feeds it and the public trade tape.
/// Kept in a partial so the primary view model stays focused.
///
/// The chart toolbar that used to live here now belongs to
/// <see cref="TradingDesk.ChartPanelViewModel"/>, and the tape rows themselves to
/// <see cref="TradingDesk.TradeBlotterViewModel"/>; this file only still fills them.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly string[] TradingVenueOrder = ["Binance", "Bybit", "OKX", "KuCoin"];

    private DispatcherTimer? _venueQuoteTimer;
    private DispatcherTimer? _tapeTimer;
    private bool _isVenueRefreshRunning;
    private bool _isTapeRefreshRunning;
    private readonly Services.MarketTapeService _marketTape = new();

    private void InitializeTradingDesk()
    {
        _venueQuoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _venueQuoteTimer.Tick += (_, _) => _ = RefreshVenueQuotesAsync();
        _venueQuoteTimer.Start();

        _tapeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _tapeTimer.Tick += (_, _) => _ = RefreshTapeAsync();
        _tapeTimer.Start();

        _ = RefreshVenueQuotesAsync();
        _ = RefreshTapeAsync();
    }

    /// <summary>Called from the SelectedMarket setter when the active symbol changes.</summary>
    private void OnTradingSymbolChanged()
    {
        var activeSymbol = SelectedTradingSymbol;
        foreach (var market in Markets)
        {
            market.IsActive = string.Equals(market.Symbol, activeSymbol, StringComparison.OrdinalIgnoreCase);
        }

        // Reset venue prices so a stale delta from the previous symbol never lingers.
        foreach (var venue in CexRightRailVM.TradingVenues)
        {
            venue.Update(0m, 0m, hasData: false);
        }

        TradeBlotterVM.TapeTrades.Clear();
        TradeBlotterVM.RaiseTapeChanged();

        _ = RefreshVenueQuotesAsync();
        _ = RefreshTapeAsync();
    }

    /// <summary>
    /// Runs when the right rail's venue tab is clicked, before its rows are restyled. Point both
    /// spot and futures order routing / display at the chosen venue, so the header, order book and
    /// tape all re-source from it.
    /// </summary>
    private void OnRightRailVenueQuoteSelected(string exchange)
    {
        SelectedSpotExchange = exchange;
        SelectedFuturesExchange = exchange;
    }

    private async Task RefreshVenueQuotesAsync()
    {
        if (_isVenueRefreshRunning || _spotGatewaysMap is null)
        {
            return;
        }

        var symbol = SelectedTradingSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        _isVenueRefreshRunning = true;
        try
        {
            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            async Task<(string Exchange, decimal Mid)> QuoteAsync(string exchange)
            {
                if (!_spotGatewaysMap.TryGetValue(exchange, out var gateway))
                {
                    return (exchange, 0m);
                }

                try
                {
                    var book = await gateway.GetOrderBookAsync(symbol, depth: 5);
                    var bestBid = book.Bids.Count > 0 ? book.Bids.Max(l => l.Price) : 0m;
                    var bestAsk = book.Asks.Count > 0 ? book.Asks.Min(l => l.Price) : 0m;
                    var mid = bestBid > 0m && bestAsk > 0m ? (bestBid + bestAsk) / 2m
                        : bestBid > 0m ? bestBid
                        : bestAsk;
                    return (exchange, mid);
                }
                catch
                {
                    return (exchange, 0m);
                }
            }

            var results = await Task.WhenAll(TradingVenueOrder.Select(QuoteAsync));
            foreach (var (exchange, mid) in results)
            {
                prices[exchange] = mid;
            }

            var basePrice = prices.TryGetValue("Binance", out var bp) && bp > 0m
                ? bp
                : prices.Values.FirstOrDefault(p => p > 0m);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Guard against a symbol change mid-flight.
                if (!string.Equals(symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                foreach (var venue in CexRightRailVM.TradingVenues)
                {
                    var price = prices.TryGetValue(venue.Exchange, out var p) ? p : 0m;
                    venue.Update(price, basePrice, hasData: price > 0m);
                }
            });
        }
        finally
        {
            _isVenueRefreshRunning = false;
        }
    }

    private async Task RefreshTapeAsync()
    {
        if (_isTapeRefreshRunning)
        {
            return;
        }

        var symbol = SelectedTradingSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        _isTapeRefreshRunning = true;
        try
        {
            // Keyless public fills for the symbol (Binance /api/v3/trades) — works
            // for every venue selection and needs no private API credentials.
            IReadOnlyList<Services.TapeTrade> trades;
            try
            {
                trades = await _marketTape.GetRecentTradesAsync(symbol, limit: 30);
            }
            catch
            {
                return;
            }

            var ordered = trades
                .Where(t => t.Price > 0m)
                .OrderByDescending(t => t.TimeUtc)
                .Take(24)
                .ToList();

            if (ordered.Count == 0)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                TradeBlotterVM.TapeTrades.Clear();
                foreach (var trade in ordered)
                {
                    TradeBlotterVM.TapeTrades.Add(new TapeTradeViewModel(
                        string.Equals(trade.Side, "BUY", StringComparison.OrdinalIgnoreCase),
                        trade.Price,
                        trade.Quantity,
                        trade.TimeUtc.ToLocalTime()));
                }

                TradeBlotterVM.RaiseTapeChanged();
            });
        }
        finally
        {
            _isTapeRefreshRunning = false;
        }
    }

    /// <summary>Compact execution-guard badge for the ticket (mockup shows PASS/BLOCK).</summary>
    public string GuardPassLabel => OrderTicketVM.CanPlacePrimaryOrder ? "PASS" : "BLOCK";

    private void StopTradingDeskTimers()
    {
        _venueQuoteTimer?.Stop();
        _tapeTimer?.Stop();
        _marketTape.Dispose();
    }
}
