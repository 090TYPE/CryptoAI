using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core.Trading;

/// <summary>Aggregate result of replaying the assistant's rules over history.</summary>
public sealed record TradeBacktestResult(
    int Trades,
    int Wins,
    int Losses,
    decimal WinRate,      // 0..1
    decimal Expectancy,   // average R per trade
    string Summary);

/// <summary>
/// Walks a candle series and, at each step, builds a setup from the history so far with
/// <see cref="AiTradeSetupPlanner"/>, then simulates it forward candle-by-candle to see
/// whether the target or the stop is hit first. Gives the user a quick, honest read on
/// how the current preference (risk / horizon / bias) would have performed on this chart.
/// Pure and deterministic.
/// </summary>
public static class AiTradeSetupBacktester
{
    public static TradeBacktestResult Run(
        IReadOnlyList<DexOhlcvPoint> candles,
        TradeRiskProfile risk,
        TradeHorizon horizon,
        TradeBiasMode bias = TradeBiasMode.Auto,
        int maxHold = 24,
        int step = 3)
    {
        if (candles is null || candles.Count < 30)
        {
            return new TradeBacktestResult(0, 0, 0, 0m, 0m, "Not enough history to backtest (need ~30+ candles).");
        }

        step = Math.Max(1, step);
        var wins = 0;
        var losses = 0;
        decimal rSum = 0m;

        for (var i = 25; i < candles.Count - 2; i += step)
        {
            var prefix = candles.Take(i + 1).ToList();
            var entry = prefix[^1].Close;
            var setup = AiTradeSetupPlanner.Build(prefix, entry, bias, risk, horizon);
            if (setup is null)
            {
                continue;
            }

            var isLong = setup.Bias == "LONG";
            var outcome = Simulate(candles, i + 1, isLong, setup.TakeProfit, setup.StopLoss, maxHold);
            if (outcome == 0)
            {
                continue; // neither hit inside the window — no completed trade
            }

            if (outcome > 0)
            {
                wins++;
                rSum += setup.RiskReward;
            }
            else
            {
                losses++;
                rSum -= 1m;
            }
        }

        var trades = wins + losses;
        if (trades == 0)
        {
            return new TradeBacktestResult(0, 0, 0, 0m, 0m, "No setups completed on this chart in the tested window.");
        }

        var winRate = (decimal)wins / trades;
        var expectancy = rSum / trades;
        var summary = $"{trades} trades · {winRate:P0} win rate · expectancy {expectancy:+0.00;-0.00;0.00}R "
            + $"({risk}/{horizon}).";

        return new TradeBacktestResult(trades, wins, losses, decimal.Round(winRate, 4), decimal.Round(expectancy, 3), summary);
    }

    /// <summary>+1 target first, -1 stop first, 0 neither within the window.</summary>
    private static int Simulate(IReadOnlyList<DexOhlcvPoint> candles, int from, bool isLong, decimal target, decimal stop, int maxHold)
    {
        var end = Math.Min(candles.Count, from + maxHold);
        for (var j = from; j < end; j++)
        {
            var c = candles[j];
            if (isLong)
            {
                var stopHit = c.Low <= stop;
                var targetHit = c.High >= target;
                if (stopHit && targetHit) return -1; // conservative: assume stop first on same candle
                if (stopHit) return -1;
                if (targetHit) return 1;
            }
            else
            {
                var stopHit = c.High >= stop;
                var targetHit = c.Low <= target;
                if (stopHit && targetHit) return -1;
                if (stopHit) return -1;
                if (targetHit) return 1;
            }
        }

        return 0;
    }
}
