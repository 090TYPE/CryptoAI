using CryptoAITerminal.Core.Models;
using System;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Назначает каждому DEX-кандидату скор от 0 до 100, чтобы снайпер мог отсортировать очередь
/// по «качеству» сетапа, а не по хронологии. Все компоненты — чистые функции без I/O.
///
/// Веса (сумма = 100):
///   Momentum 5m       — 30 баллов (±50% линейная нормализация)
///   Liquidity USD      — 25 баллов (log-шкала 0 … $1M)
///   Volume/Liq ratio   — 20 баллов (оптимум 0.5–3.0, штраф за extreme)
///   Pool age           — 15 баллов (моложе = лучше; ≤1ч full, 1–6ч 10, 6–24ч 5, >24ч 0)
///   DEX quality        — 10 баллов (нормализация TokenInfo.DexQualityScore)
/// </summary>
public static class SniperRankingModel
{
    public const int MaxScore = 100;

    public static SniperRankScore Compute(DexTokenInfo token, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(token);

        var now = nowUtc ?? DateTime.UtcNow;

        var momentum = ScoreMomentum(token.PriceChange5m);
        var liquidity = ScoreLiquidity(token.LiquidityUsd);
        var volume = ScoreVolumeRatio(token.Volume24h, token.LiquidityUsd);
        var age = ScoreAge(token.ObservedFirstSeenUtc, now);
        var dex = ScoreDexQuality(token.DexQualityScore);

        var total = momentum + liquidity + volume + age + dex;
        var clamped = (int)Math.Clamp(Math.Round(total, MidpointRounding.AwayFromZero), 0d, MaxScore);

        return new SniperRankScore(
            clamped,
            momentum,
            liquidity,
            volume,
            age,
            dex);
    }

    internal static double ScoreMomentum(decimal change5mPercent)
    {
        // ±50% выводится в [0..30]; нулевое или отрицательное движение даёт мало баллов.
        var change = (double)change5mPercent;
        var clamped = Math.Clamp(change, -50d, 50d);
        var normalized = (clamped + 50d) / 100d; // 0 при -50%, 0.5 при 0%, 1 при +50%
        return 30d * normalized;
    }

    internal static double ScoreLiquidity(decimal liquidityUsd)
    {
        // log10(1) = 0, log10(1_000_000) = 6 → 25 баллов.
        var amount = Math.Max(1d, (double)liquidityUsd);
        var log = Math.Log10(amount);
        var normalized = Math.Clamp(log / 6d, 0d, 1d);
        return 25d * normalized;
    }

    internal static double ScoreVolumeRatio(decimal volume24h, decimal liquidityUsd)
    {
        if (liquidityUsd <= 0m) return 0d;
        var ratio = (double)volume24h / (double)liquidityUsd;

        // Оптимум 0.5–3.0 → полные 20 баллов. Ниже 0.05 или выше 30.0 — 0 баллов.
        if (ratio < 0.05d || ratio > 30d) return 0d;
        if (ratio >= 0.5d && ratio <= 3d) return 20d;

        if (ratio < 0.5d)
        {
            // 0.05 → 0, 0.5 → 20
            return 20d * ((ratio - 0.05d) / (0.5d - 0.05d));
        }

        // 3 → 20, 30 → 0
        return 20d * (1d - (ratio - 3d) / (30d - 3d));
    }

    internal static double ScoreAge(DateTime firstSeenUtc, DateTime nowUtc)
    {
        if (firstSeenUtc == DateTime.MinValue) return 7.5d; // неизвестен — выдаём middle, чтобы не штрафовать

        var age = nowUtc - firstSeenUtc;
        if (age.TotalMinutes < 0) return 15d;        // часы рассинхронизированы — даём максимум
        if (age.TotalHours <= 1d) return 15d;
        if (age.TotalHours <= 6d) return 10d;
        if (age.TotalHours <= 24d) return 5d;
        return 0d;
    }

    internal static double ScoreDexQuality(int dexQualityScore)
    {
        var clamped = Math.Clamp(dexQualityScore, 0, 100);
        return 10d * (clamped / 100d);
    }
}

public readonly record struct SniperRankScore(
    int    Total,
    double MomentumComponent,
    double LiquidityComponent,
    double VolumeComponent,
    double AgeComponent,
    double DexQualityComponent)
{
    public string Band => Total switch
    {
        >= 80 => "S",
        >= 65 => "A",
        >= 50 => "B",
        >= 35 => "C",
        _     => "D"
    };

    public string Label => $"{Total}/100 · {Band}";
}
