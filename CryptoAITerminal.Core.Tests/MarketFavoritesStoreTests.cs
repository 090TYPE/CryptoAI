using System;
using System.IO;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class MarketFavoritesStoreTests
{
    [Fact]
    public void Starred_markets_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"market-favorites-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MarketFavoritesStore(path);
            store.Save(new[]
            {
                MarketFavoritesStore.KeyOf("Binance", "PEPEUSDT"),
                MarketFavoritesStore.KeyOf("DEX·solana", "TRUMP"),
            });

            var reloaded = new MarketFavoritesStore(path).Load();

            Assert.Equal(2, reloaded.Count);
            Assert.Contains(MarketFavoritesStore.KeyOf("Binance", "PEPEUSDT"), reloaded);
            Assert.Contains(MarketFavoritesStore.KeyOf("DEX·solana", "TRUMP"), reloaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Unstarring_removes_the_row_from_the_saved_set()
    {
        var path = Path.Combine(Path.GetTempPath(), $"market-favorites-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MarketFavoritesStore(path);
            store.Save(new[]
            {
                MarketFavoritesStore.KeyOf("Binance", "PEPEUSDT"),
                MarketFavoritesStore.KeyOf("DEX·solana", "TRUMP"),
            });

            // The desk always writes the full set of stars that are still on screen.
            store.Save(new[] { MarketFavoritesStore.KeyOf("Binance", "PEPEUSDT") });

            var reloaded = new MarketFavoritesStore(path).Load();
            Assert.Single(reloaded);
            Assert.DoesNotContain(MarketFavoritesStore.KeyOf("DEX·solana", "TRUMP"), reloaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Same_ticker_on_two_venues_keeps_separate_stars()
    {
        Assert.NotEqual(
            MarketFavoritesStore.KeyOf("Binance", "PEPEUSDT"),
            MarketFavoritesStore.KeyOf("DEX·ethereum", "PEPEUSDT"));
    }

    [Fact]
    public void Key_ignores_casing_and_padding()
    {
        Assert.Equal(
            MarketFavoritesStore.KeyOf("binance", "pepeusdt"),
            MarketFavoritesStore.KeyOf(" Binance ", " PEPEUSDT "));
    }

    [Fact]
    public void Missing_or_corrupt_file_loads_as_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"market-favorites-{Guid.NewGuid():N}.json");
        Assert.Empty(new MarketFavoritesStore(missing).Load());

        var corrupt = Path.Combine(Path.GetTempPath(), $"market-favorites-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(corrupt, "{ this is not a json array");
            Assert.Empty(new MarketFavoritesStore(corrupt).Load());
        }
        finally
        {
            if (File.Exists(corrupt)) File.Delete(corrupt);
        }
    }

    [Fact]
    public void Duplicate_keys_are_written_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"market-favorites-{Guid.NewGuid():N}.json");
        try
        {
            new MarketFavoritesStore(path).Save(new[]
            {
                MarketFavoritesStore.KeyOf("Binance", "BTCUSDT"),
                MarketFavoritesStore.KeyOf("Binance", "BTCUSDT"),
            });

            Assert.Single(new MarketFavoritesStore(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
