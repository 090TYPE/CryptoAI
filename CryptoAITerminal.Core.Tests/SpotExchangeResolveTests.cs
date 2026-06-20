using System.Collections.Generic;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class SpotExchangeResolveTests
{
    private sealed class StubGateway : IExchangeGateway
    {
        public string Name { get; init; } = "";
        public System.IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
        public System.Threading.Tasks.Task ConnectAsync() => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task DisconnectAsync() => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<Order> PlaceOrderAsync(Order order) => System.Threading.Tasks.Task.FromResult(order);
        public System.Threading.Tasks.Task CancelOrderAsync(string orderId) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<decimal> GetBalanceAsync(string asset) => System.Threading.Tasks.Task.FromResult(0m);
        public System.Threading.Tasks.Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => System.Threading.Tasks.Task.FromResult(new OrderBook());
    }

    [Fact]
    public void ResolveGateway_returns_mapped_gateway_case_insensitively()
    {
        var binance = new StubGateway { Name = "Binance" };
        var bybit   = new StubGateway { Name = "Bybit" };
        var map = new Dictionary<string, IExchangeGateway>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = binance,
            ["Bybit"]   = bybit,
        };
        Assert.Same(bybit, MainWindowViewModel.ResolveGateway(map, "bybit", binance));
    }

    [Fact]
    public void ResolveGateway_falls_back_when_key_missing_or_empty()
    {
        var binance = new StubGateway { Name = "Binance" };
        var map = new Dictionary<string, IExchangeGateway>(System.StringComparer.OrdinalIgnoreCase) { ["Binance"] = binance };
        Assert.Same(binance, MainWindowViewModel.ResolveGateway(map, "Kraken", binance));
        Assert.Same(binance, MainWindowViewModel.ResolveGateway(map, "", binance));
    }
}
