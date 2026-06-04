using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class CorrelationInsightServiceTests
{
    // Build a full symmetric matrix from a pair→coefficient map (diagonal = 1).
    private static CorrelationMatrix Matrix(string[] symbols, Dictionary<(string, string), decimal> coeffs, int overlap = 30)
    {
        var cells = new List<CorrelationCell>();
        foreach (var a in symbols)
            foreach (var b in symbols)
            {
                decimal r = string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 1m
                    : coeffs.TryGetValue((a, b), out var v) ? v
                    : coeffs.TryGetValue((b, a), out var v2) ? v2
                    : 0m;
                cells.Add(new CorrelationCell(a, b, r, string.Equals(a, b) ? overlap : overlap));
            }
        return new CorrelationMatrix(symbols, cells);
    }

    [Fact]
    public void Offline_LowCorrelations_AreDiversified()
    {
        var m = Matrix(["BTC", "ETH", "SOL"], new()
        {
            [("BTC", "ETH")] = 0.1m, [("BTC", "SOL")] = 0.05m, [("ETH", "SOL")] = 0.15m
        });
        var r = CorrelationInsightService.BuildOffline(m);
        Assert.Equal("DIVERSIFIED", r.Signal);
        Assert.True(r.IsFallback);
    }

    [Fact]
    public void Offline_HighCorrelations_AreConcentrated()
    {
        var m = Matrix(["BTC", "ETH", "SOL"], new()
        {
            [("BTC", "ETH")] = 0.9m, [("BTC", "SOL")] = 0.85m, [("ETH", "SOL")] = 0.88m
        });
        var r = CorrelationInsightService.BuildOffline(m);
        Assert.Equal("CONCENTRATED", r.Signal);
        Assert.Contains(r.Bullets, b => b.Contains("Tightest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Offline_HeldPairTightlyCoupled_EscalatesToConcentrated()
    {
        // Average is low, but the two HELD symbols are tightly coupled → escalate.
        var m = Matrix(["BTC", "ETH", "DOGE", "XMR"], new()
        {
            [("BTC", "ETH")] = 0.95m,   // held pair, tight
            [("BTC", "DOGE")] = 0.0m, [("BTC", "XMR")] = -0.1m,
            [("ETH", "DOGE")] = 0.05m, [("ETH", "XMR")] = 0.0m,
            [("DOGE", "XMR")] = 0.1m
        });
        var r = CorrelationInsightService.BuildOffline(m, heldSymbols: new[] { "BTC", "ETH" });
        Assert.Equal("CONCENTRATED", r.Signal);
        Assert.Contains(r.Bullets, b => b.Contains("your holdings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Offline_NoOverlap_ReturnsBalancedWithNote()
    {
        var cells = new List<CorrelationCell>
        {
            new("BTC", "BTC", 1m, 0),
            new("ETH", "ETH", 1m, 0),
            new("BTC", "ETH", 0m, 0),   // zero overlap → excluded
            new("ETH", "BTC", 0m, 0),
        };
        var m = new CorrelationMatrix(["BTC", "ETH"], cells);
        var r = CorrelationInsightService.BuildOffline(m);
        Assert.Equal("BALANCED", r.Signal);
        Assert.Contains("history", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_NoKey_FallsBackToOffline()
    {
        var svc = new CorrelationInsightService();
        svc.ConfigureAi("", null);
        Assert.False(svc.UsesLiveModel);

        var m = Matrix(["BTC", "ETH"], new() { [("BTC", "ETH")] = 0.92m });
        var r = await svc.AnalyzeAsync(m);
        Assert.Equal("CONCENTRATED", r.Signal);
        Assert.True(r.IsFallback);
    }
}
