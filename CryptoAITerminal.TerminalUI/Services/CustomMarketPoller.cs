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
/// Read-only and best-effort: any failure for one entry is swallowed.
/// </summary>
public sealed class CustomMarketPoller
{
    private sealed record Entry(CexMarketItemViewModel Vm, string Exchange, string Symbol, string DexChain, string DexAddress);

    private readonly List<Entry> _entries = [];
    private readonly Func<string, IExchangeGateway?> _resolveGateway;
    private readonly DexScreenerClient _dex;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _busy;

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
        try
        {
            foreach (var e in _entries.ToList())
            {
                try
                {
                    var md = e.Exchange.Equals("DEX", StringComparison.OrdinalIgnoreCase)
                        ? await PollDexAsync(e)
                        : await PollCexAsync(e);
                    if (md is not null)
                    {
                        var vm = e.Vm;
                        var data = md;
                        Dispatcher.UIThread.Post(() => vm.UpdateMarketData(data));
                    }
                }
                catch
                {
                    // skip this entry this tick
                }
            }
        }
        finally
        {
            _busy = false;
        }
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

    private async Task<MarketData?> PollDexAsync(Entry e)
    {
        var tokens = await _dex.SearchTokensAsync(e.DexAddress);
        var token = tokens.FirstOrDefault(t =>
                        string.Equals(t.TokenAddress, e.DexAddress, StringComparison.OrdinalIgnoreCase))
                    ?? tokens.FirstOrDefault();
        if (token is null || token.PriceUsd <= 0m) return null;

        return new MarketData
        {
            Symbol       = e.Vm.Symbol,
            BestBid      = token.PriceUsd,
            BestAsk      = token.PriceUsd,
            LastPrice    = token.PriceUsd,
            ChangePct24h = token.PriceChange24h,
            Volume24hUsd = token.Volume24h,
            Timestamp    = DateTime.UtcNow,
        };
    }

    public void Stop() => _timer.Stop();
}
