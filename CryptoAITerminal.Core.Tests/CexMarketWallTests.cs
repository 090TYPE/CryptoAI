using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class CexMarketWallTests
{
    private static OrderBook Book() => new()
    {
        Symbol = "BTCUSDT",
        Bids =
        [
            new OrderBookLevel { Price = 100m, Quantity = 20m }, // 2000 notional
            new OrderBookLevel { Price = 99m,  Quantity = 1m },  // 99 notional
        ],
        Asks =
        [
            new OrderBookLevel { Price = 101m, Quantity = 15m }, // 1515 notional
        ]
    };

    [Fact]
    public void Usd_mode_flags_levels_and_builds_walls()
    {
        var vm = new CexMarketItemViewModel("BTCUSDT")
        {
            WallMode = WallHighlightMode.Usd,
            WallUsdThreshold = 1000m
        };

        vm.UpdateOrderBook(Book());

        Assert.True(vm.BidLevels[0].IsLarge);
        Assert.False(vm.BidLevels[1].IsLarge);
        Assert.True(vm.AskLevels[0].IsLarge);

        Assert.Equal(2, vm.LargeWalls.Count);
        Assert.Contains(vm.LargeWalls, w => w.IsBid && w.Price == 100m);
        Assert.Contains(vm.LargeWalls, w => !w.IsBid && w.Price == 101m);
        Assert.Equal(1, vm.BidWallCount);
        Assert.Equal(1, vm.AskWallCount);
    }

    [Fact]
    public void Zero_threshold_flags_nothing()
    {
        var vm = new CexMarketItemViewModel("BTCUSDT")
        {
            WallMode = WallHighlightMode.Usd,
            WallUsdThreshold = 0m
        };

        vm.UpdateOrderBook(Book());

        Assert.All(vm.BidLevels, l => Assert.False(l.IsLarge));
        Assert.Empty(vm.LargeWalls);
    }

    [Fact]
    public void Changing_threshold_recomputes_current_book()
    {
        var vm = new CexMarketItemViewModel("BTCUSDT") { WallMode = WallHighlightMode.Usd, WallUsdThreshold = 1000m };
        vm.UpdateOrderBook(Book());
        Assert.Equal(2, vm.LargeWalls.Count);

        vm.WallUsdThreshold = 1600m; // now only the 2000-notional bid qualifies
        Assert.Single(vm.LargeWalls);
        Assert.True(vm.BidLevels[0].IsLarge);
        Assert.False(vm.AskLevels[0].IsLarge);
    }
}
