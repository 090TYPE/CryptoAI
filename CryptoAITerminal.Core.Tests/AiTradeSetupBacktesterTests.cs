using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Core.Trading;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class AiTradeSetupBacktesterTests
{
    private static List<DexOhlcvPoint> Series(IEnumerable<decimal> closes, decimal range = 2m)
    {
        var t = DateTime.UtcNow.AddDays(-10);
        return closes.Select(c => new DexOhlcvPoint
        {
            Timestamp = t = t.AddHours(1),
            Open = c,
            High = c + range,
            Low = c - range,
            Close = c,
            Volume = 1000m,
        }).ToList();
    }

    [Fact]
    public void Short_Series_Reports_Insufficient_History()
    {
        var r = AiTradeSetupBacktester.Run(Series(Enumerable.Range(0, 10).Select(i => (decimal)i)), TradeRiskProfile.Balanced, TradeHorizon.Swing);
        Assert.Equal(0, r.Trades);
    }

    [Fact]
    public void SteadyUptrend_Long_Has_Wins_And_Bounded_WinRate()
    {
        var candles = Series(Enumerable.Range(0, 120).Select(i => 100m + i)); // clean rising market
        var r = AiTradeSetupBacktester.Run(candles, TradeRiskProfile.Balanced, TradeHorizon.Swing, TradeBiasMode.Long);

        Assert.True(r.Trades > 0);
        Assert.InRange(r.WinRate, 0m, 1m);
        Assert.True(r.Wins > 0); // a persistent uptrend should hit some long targets
        Assert.Contains("win rate", r.Summary);
    }

    [Fact]
    public void WinRate_And_Counts_Are_Consistent()
    {
        var candles = Series(Enumerable.Range(0, 150).Select(i => 100m + (decimal)Math.Sin(i / 5.0) * 12m));
        var r = AiTradeSetupBacktester.Run(candles, TradeRiskProfile.Aggressive, TradeHorizon.Scalp);

        Assert.Equal(r.Wins + r.Losses, r.Trades);
        if (r.Trades > 0)
        {
            Assert.Equal(decimal.Round((decimal)r.Wins / r.Trades, 4), r.WinRate);
        }
    }
}
