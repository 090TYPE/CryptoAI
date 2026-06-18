using System;
using System.IO;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class BookWallSettingsStoreTests
{
    [Fact]
    public void Persists_mode_and_per_symbol_thresholds_across_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"book-walls-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BookWallSettingsStore(path);
            store.Mode = WallHighlightMode.Qty;
            store.Set("BTCUSDT", new BookWallSettings { UsdThreshold = 1_000_000m, QtyThreshold = 5m });

            var reloaded = new BookWallSettingsStore(path);
            Assert.Equal(WallHighlightMode.Qty, reloaded.Mode);
            var s = reloaded.Get("BTCUSDT");
            Assert.Equal(1_000_000m, s.UsdThreshold);
            Assert.Equal(5m, s.QtyThreshold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_symbol_returns_default_threshold()
    {
        var path = Path.Combine(Path.GetTempPath(), $"book-walls-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BookWallSettingsStore(path);
            var s = store.Get("NEWPAIR");
            Assert.Equal(250_000m, s.UsdThreshold);
            Assert.Equal(0m, s.QtyThreshold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
