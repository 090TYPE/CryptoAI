using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class CexMarketItemViewModel : ReactiveObject
{
    private const double ChartWidth = 560;
    private const double ChartHeight = 220;
    private const int MaxHistoryPoints = 720;

    private readonly List<PriceSample> _priceHistory = [];
    private decimal _lastPrice;
    private decimal _bestBid;
    private decimal _bestAsk;
    private DateTime _lastUpdated;
    private string _selectedTimeframe = "1M";
    private bool _isFavorite;

    public CexMarketItemViewModel(string symbol)
    {
        Symbol = symbol;
    }

    public string Symbol { get; }
    public string BaseAssetSymbol => Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        ? Symbol[..^4]
        : Symbol;
    public string DisplaySymbol => Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        ? $"{Symbol[..^4]} / USDT"
        : Symbol;
    public string LogoText => BaseAssetSymbol.ToUpperInvariant() switch
    {
        "BTC" => "B",
        "ETH" => "E",
        "BNB" => "N",
        "SOL" => "S",
        "XRP" => "X",
        "ADA" => "A",
        "DOGE" => "D",
        "AVAX" => "V",
        "LINK" => "L",
        "TRX" => "T",
        "LTC" => "L",
        _ => BaseAssetSymbol[..1]
    };
    public string LogoBackground => BaseAssetSymbol.ToUpperInvariant() switch
    {
        "BTC" => "#F7931A",
        "ETH" => "#5B6478",
        "BNB" => "#F0B90B",
        "SOL" => "#101820",
        "XRP" => "#23292F",
        "ADA" => "#2A6BFF",
        _ => "#17373B"
    };
    public string LogoForeground => BaseAssetSymbol.ToUpperInvariant() switch
    {
        "BTC" => "#0B1118",
        "BNB" => "#0B1118",
        _ => "#F4F7FB"
    };

    public decimal LastPrice
    {
        get => _lastPrice;
        private set => this.RaiseAndSetIfChanged(ref _lastPrice, value);
    }

    public decimal BestBid
    {
        get => _bestBid;
        private set => this.RaiseAndSetIfChanged(ref _bestBid, value);
    }

    public decimal BestAsk
    {
        get => _bestAsk;
        private set => this.RaiseAndSetIfChanged(ref _bestAsk, value);
    }

    public decimal Spread => BestAsk > 0 && BestBid > 0 ? BestAsk - BestBid : 0;
    public decimal SpreadPercent => LastPrice > 0 ? Spread / LastPrice * 100m : 0m;

    public DateTime LastUpdated
    {
        get => _lastUpdated;
        private set => this.RaiseAndSetIfChanged(ref _lastUpdated, value);
    }

    public bool HasPriceHistory => GetVisibleHistory().Count > 1;

    public decimal ChangePercent
    {
        get
        {
            var visibleHistory = GetVisibleHistory();
            if (visibleHistory.Count == 0 || LastPrice <= 0)
            {
                return 0m;
            }

            var reference = visibleHistory[0].Price > 0 ? visibleHistory[0].Price : LastPrice;
            return reference > 0 ? (LastPrice - reference) / reference * 100m : 0m;
        }
    }

    public bool IsPositiveTrend
    {
        get
        {
            var visibleHistory = GetVisibleHistory();
            return visibleHistory.Count > 1 && visibleHistory[^1].Price >= visibleHistory[0].Price;
        }
    }

    public decimal SessionHigh
    {
        get
        {
            var visibleHistory = GetVisibleHistory();
            return visibleHistory.Count == 0 ? LastPrice : visibleHistory.Max(sample => sample.Price);
        }
    }

    public decimal SessionLow
    {
        get
        {
            var visibleHistory = GetVisibleHistory();
            return visibleHistory.Count == 0 ? LastPrice : visibleHistory.Min(sample => sample.Price);
        }
    }

    public decimal RangePercent => SessionLow > 0 ? (SessionHigh - SessionLow) / SessionLow * 100m : 0m;
    public int HistoryPointCount => GetVisibleHistory().Count;
    public decimal ActivityScore =>
        Math.Clamp(Math.Abs(ChangePercent) * 2.2m + HistoryPointCount / 8m - Math.Min(SpreadPercent * 18m, 18m), 0m, 99m);
    public string ChangePercentLabel => HasPriceHistory ? $"{ChangePercent:+0.##;-0.##;0}%" : "--";
    public string SpreadPercentLabel => Spread > 0 ? $"{SpreadPercent:0.###}%" : "--";
    public string RangeLabel => SessionHigh > 0 && SessionLow > 0 ? $"{SessionLow:N2} - {SessionHigh:N2}" : "--";
    public string TrendLabel => !HasPriceHistory ? "Warmup" : IsPositiveTrend ? "Bullish" : "Bearish";
    public string TrendBrush => !HasPriceHistory ? "#8FA3B8" : IsPositiveTrend ? "#21E6C1" : "#FF857B";
    public string UpdatedLabel => LastUpdated == default ? "--" : LastUpdated.ToString("HH:mm:ss");
    public string ActivityScoreLabel => $"{ActivityScore:0}";
    public string ChangeBrush => ChangePercent >= 0 ? "#21E6C1" : "#FF857B";

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFavorite, value);
            this.RaisePropertyChanged(nameof(FavoriteGlyph));
            this.RaisePropertyChanged(nameof(FavoriteBrush));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "STAR" : "ADD";
    public string FavoriteBrush => IsFavorite ? "#F4B860" : "#8FA3B8";

    public ObservableCollection<Point> ChartPoints { get; } = [];
    public ObservableCollection<OrderBookLevelViewModel> BidLevels { get; } = [];
    public ObservableCollection<OrderBookLevelViewModel> AskLevels { get; } = [];

    public string ChartPolylinePoints => string.Join(
        ' ',
        ChartPoints.Select(point =>
            $"{point.X.ToString("0.##", CultureInfo.InvariantCulture)},{point.Y.ToString("0.##", CultureInfo.InvariantCulture)}"));

    public void UpdateMarketData(MarketData data)
    {
        LastPrice = data.LastPrice;
        BestBid = data.BestBid;
        BestAsk = data.BestAsk;
        LastUpdated = data.Timestamp.ToLocalTime();

        if (data.LastPrice > 0)
        {
            _priceHistory.Add(new PriceSample(data.LastPrice, data.Timestamp.ToLocalTime()));
            if (_priceHistory.Count > MaxHistoryPoints)
            {
                _priceHistory.RemoveAt(0);
            }

            RebuildChart();
        }

        RaiseDerivedState();
    }

    public void ApplyTimeframe(string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return;
        }

        _selectedTimeframe = timeframe;
        RebuildChart();
        RaiseDerivedState();
    }

    public void UpdateOrderBook(OrderBook orderBook)
    {
        ReplaceLevels(
            BidLevels,
            orderBook.Bids
                .OrderByDescending(level => level.Price)
                .Take(8)
                .Select(level => new OrderBookLevelViewModel(level.Price, level.Quantity)));

        ReplaceLevels(
            AskLevels,
            orderBook.Asks
                .OrderBy(level => level.Price)
                .Take(8)
                .Select(level => new OrderBookLevelViewModel(level.Price, level.Quantity)));

        RaiseDerivedState();
    }

    private void RebuildChart()
    {
        ChartPoints.Clear();
        var visibleHistory = GetVisibleHistory();
        if (visibleHistory.Count == 0)
        {
            this.RaisePropertyChanged(nameof(HasPriceHistory));
            this.RaisePropertyChanged(nameof(IsPositiveTrend));
            this.RaisePropertyChanged(nameof(ChartPolylinePoints));
            return;
        }

        var min = visibleHistory.Min(sample => sample.Price);
        var max = visibleHistory.Max(sample => sample.Price);
        var rawRange = (double)(max - min);

        // Floor the visible range to a small fraction of price so negligible
        // tick-to-tick noise stays a near-flat line instead of being amplified
        // into a full-height square wave. When real movement exceeds the floor
        // the chart auto-scales exactly as before. The domain is centred on the
        // mid price so a flat market renders as a centred flat line.
        var midPrice = (double)(min + max) / 2d;
        var rangeFloor = Math.Max(midPrice * 0.0015d, 0.00000001d);
        var range = Math.Max(rawRange, rangeFloor);
        var lo = midPrice - (range / 2d);

        for (var index = 0; index < visibleHistory.Count; index++)
        {
            // X stays a sample index; CexPriceChart maps it across the width.
            // Y is normalised to [0..1] with 0 = top (high price), 1 = bottom.
            var normalized = ((double)visibleHistory[index].Price - lo) / range;
            var y = 1d - normalized;
            ChartPoints.Add(new Point(index, y));
        }

        this.RaisePropertyChanged(nameof(HasPriceHistory));
        this.RaisePropertyChanged(nameof(IsPositiveTrend));
        this.RaisePropertyChanged(nameof(ChartPolylinePoints));
    }

    private void RaiseDerivedState()
    {
        this.RaisePropertyChanged(nameof(Spread));
        this.RaisePropertyChanged(nameof(SpreadPercent));
        this.RaisePropertyChanged(nameof(SpreadPercentLabel));
        this.RaisePropertyChanged(nameof(ChangePercent));
        this.RaisePropertyChanged(nameof(ChangePercentLabel));
        this.RaisePropertyChanged(nameof(SessionHigh));
        this.RaisePropertyChanged(nameof(SessionLow));
        this.RaisePropertyChanged(nameof(RangePercent));
        this.RaisePropertyChanged(nameof(RangeLabel));
        this.RaisePropertyChanged(nameof(HistoryPointCount));
        this.RaisePropertyChanged(nameof(ActivityScore));
        this.RaisePropertyChanged(nameof(ActivityScoreLabel));
        this.RaisePropertyChanged(nameof(TrendLabel));
        this.RaisePropertyChanged(nameof(TrendBrush));
        this.RaisePropertyChanged(nameof(UpdatedLabel));
        this.RaisePropertyChanged(nameof(ChangeBrush));
    }

    private IReadOnlyList<PriceSample> GetVisibleHistory()
    {
        if (_priceHistory.Count == 0)
        {
            return [];
        }

        var latestTimestamp = _priceHistory[^1].TimestampLocal;
        var cutoff = latestTimestamp - GetLookback(_selectedTimeframe);
        var filtered = _priceHistory.Where(sample => sample.TimestampLocal >= cutoff).ToList();
        return filtered.Count > 1 ? filtered : _priceHistory.TakeLast(Math.Min(_priceHistory.Count, GetFallbackPointCount(_selectedTimeframe))).ToList();
    }

    private static TimeSpan GetLookback(string timeframe) => timeframe switch
    {
        "1M" => TimeSpan.FromMinutes(1),
        "5M" => TimeSpan.FromMinutes(5),
        "15M" => TimeSpan.FromMinutes(15),
        "1H" => TimeSpan.FromHours(1),
        _ => TimeSpan.FromMinutes(1)
    };

    private static int GetFallbackPointCount(string timeframe) => timeframe switch
    {
        "1M" => 30,
        "5M" => 90,
        "15M" => 180,
        "1H" => 360,
        _ => 30
    };

    private static void ReplaceLevels(
        ObservableCollection<OrderBookLevelViewModel> target,
        IEnumerable<OrderBookLevelViewModel> source)
    {
        target.Clear();
        foreach (var level in source)
        {
            target.Add(level);
        }
    }
}

public sealed class PriceSample
{
    public PriceSample(decimal price, DateTime timestampLocal)
    {
        Price = price;
        TimestampLocal = timestampLocal;
    }

    public decimal Price { get; }
    public DateTime TimestampLocal { get; }
}

public class OrderBookLevelViewModel : ReactiveObject
{
    private bool _isSelected;
    private bool _isLarge;

    public OrderBookLevelViewModel(decimal price, decimal quantity)
    {
        Price = price;
        Quantity = quantity;
    }

    public decimal Price { get; }
    public decimal Quantity { get; }
    public decimal Notional => Price * Quantity;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(PriceBrush));
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>True when this level is at/above the user's wall threshold.</summary>
    public bool IsLarge
    {
        get => _isLarge;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLarge, value);
            this.RaisePropertyChanged(nameof(RowBackground));
            this.RaisePropertyChanged(nameof(SizeFontWeight));
            this.RaisePropertyChanged(nameof(WallIconVisible));
        }
    }

    public string PriceBrush => IsSelected ? "#F4B860" : "#F4F7FB";

    // Selected tint wins; otherwise a warm wall tint when large.
    public string RowBackground => IsSelected ? "#243241" : IsLarge ? "#3A2E12" : "Transparent";
    public string SizeFontWeight => IsLarge ? "Bold" : "Normal";
    public bool WallIconVisible => IsLarge;
}
