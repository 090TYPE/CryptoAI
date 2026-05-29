using Avalonia.Media;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class DexTokenRowViewModel : ReactiveObject
{
    private readonly DexTrendingToken _token;
    private string _selectedTimeframe;

    public DexTrendingToken Token => _token;

    public string TokenAddress   => _token.TokenAddress;
    public string Symbol         => _token.Symbol;
    public string Name           => _token.Name;
    public string Chain          => _token.Chain;
    public string DexId          => _token.DexId;
    public string PairAddress    => _token.PairAddress;
    public string DexScreenerUrl => _token.DexScreenerUrl;

    // ── Computed display properties ───────────────────────────────────────────

    public string ChainIcon => _token.Chain.ToLowerInvariant() switch
    {
        "ethereum"  => "Ξ",
        "bsc"       => "⬡",
        "solana"    => "◎",
        "arbitrum"  => "🔵",
        "base"      => "🔷",
        _           => "●"
    };

    public string PriceLabel
    {
        get
        {
            var p = _token.PriceUsd;
            if (p == 0m) return "$0";
            if (p >= 1000m)   return $"${p:N0}";
            if (p >= 1m)      return $"${p:N4}";
            if (p >= 0.01m)   return $"${p:N5}";
            if (p >= 0.0001m) return $"${p:N6}";
            return $"${p:G4}";
        }
    }

    public string VolumeLabel
    {
        get
        {
            var vol = _selectedTimeframe switch
            {
                "5m"  => _token.VolumeM5,
                "15m" => _token.VolumeM15,
                "6h"  => _token.VolumeH24,  // best available approximation
                "24h" => _token.VolumeH24,
                _     => _token.VolumeH1
            };
            return FormatMoney(vol);
        }
    }

    public string LiquidityLabel => FormatMoney(_token.LiquidityUsd);

    public string MarketCapLabel => _token.MarketCap > 0m ? FormatMoney(_token.MarketCap) : "—";

    public string AgeLabel
    {
        get
        {
            if (_token.PairCreatedAtMs <= 0) return "?";
            var created = DateTimeOffset.FromUnixTimeMilliseconds(_token.PairCreatedAtMs).UtcDateTime;
            var age = DateTime.UtcNow - created;
            if (age.TotalMinutes < 60)  return $"{(int)age.TotalMinutes}m";
            if (age.TotalHours  < 24)   return $"{(int)age.TotalHours}h";
            return $"{(int)age.TotalDays}d";
        }
    }

    public double AgeHours
    {
        get
        {
            if (_token.PairCreatedAtMs <= 0) return 999;
            var created = DateTimeOffset.FromUnixTimeMilliseconds(_token.PairCreatedAtMs).UtcDateTime;
            return (DateTime.UtcNow - created).TotalHours;
        }
    }

    public string PriceChangeLabel
    {
        get
        {
            var pc = GetPriceChange();
            return pc >= 0 ? $"+{pc:F1}%" : $"{pc:F1}%";
        }
    }

    public IBrush PriceChangeBrush
    {
        get
        {
            var pc = GetPriceChange();
            if (pc > 0.1)  return new SolidColorBrush(Color.Parse("#21E6C1"));
            if (pc < -0.1) return new SolidColorBrush(Color.Parse("#FF4444"));
            return new SolidColorBrush(Color.Parse("#8FA3B8"));
        }
    }

    /// <summary>
    /// Heuristic security score 0-100 based on liquidity, pool age, volume, and price stability.
    /// Does NOT call GoPlus/RugCheck — intentionally fast and offline.
    /// </summary>
    public int SecurityScore => ComputeSecurityScore(
        _token.LiquidityUsd, AgeHours, _token.VolumeH1, _token.PriceChangeH1);

    /// <summary>Reusable static so DexTrendingViewModel can filter before instantiating row VMs.</summary>
    public static int ComputeSecurityScore(decimal liqUsd, double ageHours, decimal volH1, double priceChangeH1)
    {
        int score = 0;

        // ── Liquidity (0..35) ─────────────────────────────────────────────────
        if      (liqUsd >= 500_000m) score += 35;
        else if (liqUsd >= 100_000m) score += 28;
        else if (liqUsd >= 50_000m)  score += 20;
        else if (liqUsd >= 10_000m)  score += 12;
        else if (liqUsd >= 1_000m)   score += 5;

        // ── Pool age (0..30) ──────────────────────────────────────────────────
        if      (ageHours >= 168) score += 30;  // ≥ 1 week
        else if (ageHours >= 24)  score += 22;
        else if (ageHours >= 6)   score += 14;
        else if (ageHours >= 1)   score += 6;
        // < 1h → 0 (too new to trust)

        // ── Hourly volume activity (0..20) ────────────────────────────────────
        if      (volH1 >= 1_000_000m) score += 20;
        else if (volH1 >= 100_000m)   score += 15;
        else if (volH1 >= 10_000m)    score += 10;
        else if (volH1 >= 1_000m)     score += 5;

        // ── Price stability (0..15) ───────────────────────────────────────────
        // Extreme moves → higher rug/pump risk
        var absChange = Math.Abs(priceChangeH1);
        if      (absChange <= 5)  score += 15;
        else if (absChange <= 15) score += 10;
        else if (absChange <= 30) score += 5;
        // > 30% change → 0

        return Math.Min(100, score);
    }

    public string SecurityBadge
    {
        get
        {
            var s = SecurityScore;
            return s >= 70 ? $"🟢 {s}"
                 : s >= 40 ? $"🟡 {s}"
                 : $"🔴 {s}";
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public DexTokenRowViewModel(DexTrendingToken token, string selectedTimeframe)
    {
        _token = token;
        _selectedTimeframe = selectedTimeframe ?? "1h";
    }

    public void UpdateTimeframe(string tf)
    {
        _selectedTimeframe = tf ?? "1h";
        this.RaisePropertyChanged(nameof(VolumeLabel));
        this.RaisePropertyChanged(nameof(PriceChangeLabel));
        this.RaisePropertyChanged(nameof(PriceChangeBrush));
        // SecurityScore doesn't depend on timeframe, no need to raise it
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private double GetPriceChange() => _selectedTimeframe switch
    {
        "5m"  => _token.PriceChangeM5,
        "15m" => _token.PriceChangeM15,
        "6h"  => _token.PriceChangeH6,
        "24h" => _token.PriceChangeH24,
        _     => _token.PriceChangeH1
    };

    private static string FormatMoney(decimal v)
    {
        if (v == 0m) return "$0";
        if (v >= 1_000_000_000m) return $"${v / 1_000_000_000m:N1}B";
        if (v >= 1_000_000m)     return $"${v / 1_000_000m:N1}M";
        if (v >= 1_000m)         return $"${v / 1_000m:N0}K";
        return $"${v:N0}";
    }
}
