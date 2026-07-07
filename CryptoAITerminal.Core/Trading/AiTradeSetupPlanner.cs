using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core.Trading;

public enum TradeRiskProfile { Conservative, Balanced, Aggressive }

public enum TradeHorizon { Scalp, Swing, Position }

public enum TradeBiasMode { Auto, Long, Short }

/// <summary>A concrete, chart-derived trade setup the AI assistant proposes and the
/// ticket can apply verbatim.</summary>
public sealed record TradeSetup(
    string Bias,          // "LONG" / "SHORT"
    decimal Entry,
    decimal TakeProfit,
    decimal StopLoss,
    decimal RiskReward,
    int Leverage,
    decimal SizePercent,
    int Confidence,       // 0..100
    string Rationale,
    int Confluence = 0);  // 0..100 multi-lookback trend agreement

/// <summary>
/// Deterministic trade-setup builder that reads recent candles and the user's stated
/// preference (risk profile, horizon, bias, free-text intent) and produces concrete
/// entry / take-profit / stop-loss / leverage / size values. Pure and testable — no
/// network, no randomness. An optional AI narrative can be layered on top by callers,
/// but the numbers here always work offline.
/// </summary>
public static class AiTradeSetupPlanner
{
    public static TradeSetup? Build(
        IReadOnlyList<DexOhlcvPoint> candles,
        decimal currentPrice,
        TradeBiasMode biasMode,
        TradeRiskProfile risk,
        TradeHorizon horizon,
        string? intent = null,
        decimal riskPercentPerTrade = 0m)
    {
        if (candles is null || candles.Count < 3)
        {
            return null;
        }

        var closes = candles.Select(c => c.Close).Where(c => c > 0m).ToList();
        if (closes.Count < 3)
        {
            return null;
        }

        var price = currentPrice > 0m ? currentPrice : closes[^1];
        if (price <= 0m)
        {
            return null;
        }

        // Trend: short vs long simple moving average of closes.
        var shortWin = Math.Min(8, closes.Count);
        var longWin = Math.Min(21, closes.Count);
        var shortMa = closes.Skip(closes.Count - shortWin).Average();
        var longMa = closes.Skip(closes.Count - longWin).Average();
        var trendUp = shortMa >= longMa;
        var trendStrength = longMa == 0m ? 0m : Math.Abs(shortMa - longMa) / longMa;

        // Volatility: average true-range proxy over recent candles.
        var ranges = candles.TakeLast(Math.Min(14, candles.Count))
            .Select(c => Math.Max(c.High - c.Low, 0m))
            .Where(r => r > 0m)
            .ToList();
        var atr = ranges.Count > 0 ? ranges.Average() : price * 0.01m;
        if (atr <= 0m)
        {
            atr = price * 0.01m;
        }

        // Bias: explicit override → intent keywords → trend.
        var bias = biasMode switch
        {
            TradeBiasMode.Long => true,
            TradeBiasMode.Short => false,
            _ => ResolveIntentBias(intent) ?? trendUp,
        };
        var isLong = bias;

        var stopMult = horizon switch
        {
            TradeHorizon.Scalp => 1.2m,
            TradeHorizon.Position => 2.8m,
            _ => 1.8m,
        };
        var rMultiple = risk switch
        {
            TradeRiskProfile.Conservative => 1.5m,
            TradeRiskProfile.Aggressive => 3.2m,
            _ => 2.2m,
        };
        var leverage = risk switch
        {
            TradeRiskProfile.Conservative => 3,
            TradeRiskProfile.Aggressive => 25,
            _ => 10,
        };
        var sizePercent = risk switch
        {
            TradeRiskProfile.Conservative => 15m,
            TradeRiskProfile.Aggressive => 60m,
            _ => 35m,
        };

        var stopDistance = atr * stopMult;
        var entry = price;
        var stop = isLong ? entry - stopDistance : entry + stopDistance;
        var target = isLong ? entry + stopDistance * rMultiple : entry - stopDistance * rMultiple;
        if (stop < 0m)
        {
            stop = entry * 0.5m;
        }

        // Risk-based sizing: if the user states a per-trade risk %, size so that being
        // stopped out costs ~that % of the account — independent of leverage/equity.
        if (riskPercentPerTrade > 0m && stopDistance > 0m)
        {
            sizePercent = Math.Clamp(riskPercentPerTrade * (entry / stopDistance), 1m, 100m);
        }

        // Confluence: agreement of fast/mid/slow moving averages with the chosen bias.
        var confluence = TrendConfluence(closes, isLong);

        // Confidence: trend strength, rewarded when the bias agrees with the trend.
        var aligned = isLong == trendUp;
        var baseConfidence = 52m + Math.Min(trendStrength * 900m, 34m);
        var confidence = (int)Math.Clamp(aligned ? baseConfidence + 6m : baseConfidence - 14m, 38m, 93m);

        var rationale = BuildRationale(isLong, aligned, trendUp, trendStrength, atr, entry, target, stop,
            rMultiple, risk, horizon, intent, price);

        return new TradeSetup(
            Bias: isLong ? "LONG" : "SHORT",
            Entry: Round(entry, price),
            TakeProfit: Round(target, price),
            StopLoss: Round(stop, price),
            RiskReward: Math.Round(rMultiple, 2),
            Leverage: leverage,
            SizePercent: sizePercent,
            Confidence: confidence,
            Rationale: rationale,
            Confluence: confluence);
    }

    /// <summary>0..100 agreement of fast (5) / mid (13) / slow (34) SMAs with the bias.</summary>
    private static int TrendConfluence(IReadOnlyList<decimal> closes, bool isLong)
    {
        decimal Ma(int win)
        {
            var w = Math.Min(win, closes.Count);
            return closes.Skip(closes.Count - w).Average();
        }

        var fast = Ma(5);
        var mid = Ma(13);
        var slow = Ma(34);
        var upVotes = (fast >= mid ? 1 : 0) + (mid >= slow ? 1 : 0);
        var agree = isLong ? upVotes : 2 - upVotes;   // 0..2
        return 50 + agree * 25;                        // 50 / 75 / 100
    }

    private static bool? ResolveIntentBias(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return null;
        }

        var t = intent.ToLowerInvariant();
        var wantsShort = t.Contains("short") || t.Contains("sell") || t.Contains("шорт") || t.Contains("прода") || t.Contains("паден") || t.Contains("вниз");
        var wantsLong = t.Contains("long") || t.Contains("buy") || t.Contains("лонг") || t.Contains("покуп") || t.Contains("рост") || t.Contains("вверх");

        if (wantsShort && !wantsLong)
        {
            return false;
        }
        if (wantsLong && !wantsShort)
        {
            return true;
        }
        return null;
    }

    private static string BuildRationale(
        bool isLong, bool aligned, bool trendUp, decimal trendStrength, decimal atr,
        decimal entry, decimal target, decimal stop, decimal rMultiple,
        TradeRiskProfile risk, TradeHorizon horizon, string? intent, decimal price)
    {
        var trendWord = trendUp ? "up" : "down";
        var strengthPct = Math.Round(trendStrength * 100m, 2);
        var atrPct = price > 0m ? Math.Round(atr / price * 100m, 2) : 0m;
        var sideWord = isLong ? "LONG" : "SHORT";
        var alignWord = aligned ? "with the trend" : "counter-trend";

        var intentNote = string.IsNullOrWhiteSpace(intent)
            ? string.Empty
            : $" Tuned to your note: \"{intent.Trim()}\".";

        return $"Trend is {trendWord} (MA gap {strengthPct}%), volatility ≈ {atrPct}% ATR. "
            + $"{risk}/{horizon} → {sideWord} {alignWord}: enter near {Round(entry, price)}, "
            + $"stop {Round(stop, price)}, target {Round(target, price)} at {rMultiple:0.#}R.{intentNote}";
    }

    private static decimal Round(decimal value, decimal reference) => reference switch
    {
        >= 1000m => Math.Round(value, 2),
        >= 1m => Math.Round(value, 4),
        >= 0.0001m => Math.Round(value, 6),
        _ => Math.Round(value, 8),
    };
}
