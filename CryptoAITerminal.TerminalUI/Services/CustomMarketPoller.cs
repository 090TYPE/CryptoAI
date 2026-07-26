using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Keeps non-Binance CEX coins and DEX tokens on the Markets board fresh via REST
/// polling (Binance defaults stay on the live socket). CEX prices come from each
/// gateway's order book; DEX prices come from DexScreener by contract address.
/// Read-only and best-effort: a failure on one entry only skips that entry, but a
/// rate-limit answer slows the whole poller down (see <see cref="ApplyBackoff"/>).
/// </summary>
public sealed class CustomMarketPoller
{
    private sealed record Entry(CexMarketItemViewModel Vm, string Exchange, string Symbol, string DexChain, string DexAddress);

    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MaxInterval  = TimeSpan.FromMinutes(5);

    private readonly List<Entry> _entries = [];
    private readonly Func<string, IExchangeGateway?> _resolveGateway;
    private readonly DexScreenerClient _dex;
    private readonly DispatcherTimer _timer = new() { Interval = BaseInterval };
    private bool _busy;
    private bool _throttled;

    /// <summary>True while the poller is backing off after a 429 — prices on the board are stale.</summary>
    public bool IsRateLimited => _throttled;

    public CustomMarketPoller(Func<string, IExchangeGateway?> resolveGateway, DexScreenerClient dex)
    {
        _resolveGateway = resolveGateway;
        _dex = dex;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    public void AddCex(CexMarketItemViewModel vm, string exchange, string symbol)
        => _entries.Add(new Entry(vm, exchange, symbol, "", ""));

    public void AddDex(CexMarketItemViewModel vm, string chain, string address)
        => _entries.Add(new Entry(vm, "DEX", "", chain, address));

    public void Remove(CexMarketItemViewModel vm)
        => _entries.RemoveAll(e => ReferenceEquals(e.Vm, vm));

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        var rateLimited = false;
        try
        {
            var entries = _entries.ToList();

            // DexScreener allows 300 requests per minute; one request per token every 6 s spends
            // the whole budget on 30 coins. tokens/v1 takes up to 30 addresses at once, so a tick
            // costs one request per chain instead of one per token.
            foreach (var group in entries.Where(IsDex).GroupBy(e => e.DexChain, StringComparer.OrdinalIgnoreCase))
            {
                try { await PollDexChainAsync(group.Key, group.ToList()); }
                catch (Exception ex) { rateLimited |= RestBackoff.IsRateLimit(ex); }
            }

            foreach (var e in entries.Where(entry => !IsDex(entry)))
            {
                try
                {
                    var md = await PollCexAsync(e);
                    if (md is not null) Push(e.Vm, md);
                }
                catch (Exception ex)
                {
                    rateLimited |= RestBackoff.IsRateLimit(ex);
                }
            }
        }
        finally
        {
            _busy = false;
            ApplyBackoff(rateLimited);
        }
    }

    private static bool IsDex(Entry e) => e.Exchange.Equals("DEX", StringComparison.OrdinalIgnoreCase);

    private static void Push(CexMarketItemViewModel vm, MarketData data)
        => Dispatcher.UIThread.Post(() => vm.UpdateMarketData(data));

    /// <summary>
    /// Polling at the same rate through a ban only prolongs it: on 429/418 the interval doubles
    /// up to five minutes and returns to the base rate after a clean pass.
    /// </summary>
    private void ApplyBackoff(bool rateLimited)
    {
        if (rateLimited)
        {
            var next = RestBackoff.Grow(_timer.Interval, MaxInterval);
            if (next != _timer.Interval || !_throttled)
                CrashLog.Write("WARN", $"CustomMarketPoller: rate limited, polling every {next.TotalSeconds:0}s");
            _timer.Interval = next;
            _throttled = true;
            return;
        }

        if (!_throttled) return;
        _throttled = false;
        _timer.Interval = BaseInterval;
        CrashLog.Write("INFO", "CustomMarketPoller: rate limit cleared, back to 6s polling");
    }

    private async Task<MarketData?> PollCexAsync(Entry e)
    {
        var gateway = _resolveGateway(e.Exchange);
        if (gateway is null) return null;

        var book = await gateway.GetOrderBookAsync(e.Symbol, 5);
        var bid = book.Bids.Count > 0 ? book.Bids[0].Price : 0m;
        var ask = book.Asks.Count > 0 ? book.Asks[0].Price : 0m;
        var last = bid > 0 && ask > 0 ? (bid + ask) / 2m : Math.Max(bid, ask);
        if (last <= 0m) return null;

        return new MarketData
        {
            Symbol    = e.Symbol,
            BestBid   = bid,
            BestAsk   = ask,
            LastPrice = last,
            Timestamp = DateTime.UtcNow,
        };
    }

    private async Task PollDexChainAsync(string chain, List<Entry> entries)
    {
        var tokens = await _dex.GetTokensByAddressesAsync(chain, entries.Select(e => e.DexAddress));
        if (tokens.Count == 0) return;

        foreach (var e in entries)
        {
            var token = tokens.FirstOrDefault(t =>
                string.Equals(t.TokenAddress, e.DexAddress, StringComparison.OrdinalIgnoreCase));
            if (token is null || token.PriceUsd <= 0m) continue;

            Push(e.Vm, new MarketData
            {
                Symbol       = e.Vm.Symbol,
                BestBid      = token.PriceUsd,
                BestAsk      = token.PriceUsd,
                LastPrice    = token.PriceUsd,
                ChangePct24h = token.PriceChange24h,
                Volume24hUsd = token.Volume24h,
                Timestamp    = DateTime.UtcNow,
            });
        }
    }

    public void Stop() => _timer.Stop();
}
