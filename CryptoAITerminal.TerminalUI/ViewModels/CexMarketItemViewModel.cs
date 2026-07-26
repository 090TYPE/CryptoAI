using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using CryptoAITerminal.Core;
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
    private WallHighlightMode _wallMode = WallHighlightMode.Usd;
    private decimal _wallUsdThreshold = 250_000m;
    private decimal _wallQtyThreshold;
    private int _bidWallCount;
    private int _askWallCount;
    private decimal _bidWallUsd;
    private decimal _askWallUsd;

    public CexMarketItemViewModel(string symbol, string exchange = "Binance")
    {
        Symbol = symbol;
        Exchange = exchange;
    }

    /// <summary>Source exchange of this market: "Binance", "Bybit", "OKX", "KuCoin", or "DEX·{chain}".</summary>
    public string Exchange { get; }
    public bool IsDexMarket => Exchange.StartsWith("DEX", StringComparison.OrdinalIgnoreCase);
    public string ExchangeBadge => Exchange.ToUpperInvariant();
    public string ExchangeBadgeBrush => Exchange.ToLowerInvariant() switch
    {
        "binance" => "#F0B90B",
        "bybit"   => "#F7A600",
        "okx"     => SemanticColor.Muted,
        "kucoin"  => "#24AE8F",
        _         => "#7C5CFF"   // DEX
    };

    /// <summary>Large resting orders for the chart, rebuilt on every book update.</summary>
    public ObservableCollection<BookWall> LargeWalls { get; } = [];

    /// <summary>Invoked after the user changes mode/thresholds, so callers can persist.</summary>
    public Action? WallSettingsChanged { get; set; }

    public WallHighlightMode WallMode
    {
        get => _wallMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _wallMode, value);
            this.RaisePropertyChanged(nameof(IsWallModeUsd));
            this.RaisePropertyChanged(nameof(IsWallModeQty));
            ApplyWallHighlighting();
            WallSettingsChanged?.Invoke();
        }
    }

    public bool IsWallModeUsd => WallMode == WallHighlightMode.Usd;
    public bool IsWallModeQty => WallMode == WallHighlightMode.Qty;

    public decimal WallUsdThreshold
    {
        get => _wallUsdThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _wallUsdThreshold, value);
            ApplyWallHighlighting();
            WallSettingsChanged?.Invoke();
        }
    }

    public decimal WallQtyThreshold
    {
        get => _wallQtyThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _wallQtyThreshold, value);
            ApplyWallHighlighting();
            WallSettingsChanged?.Invoke();
        }
    }

    public int BidWallCount
    {
        get => _bidWallCount;
        private set => this.RaiseAndSetIfChanged(ref _bidWallCount, value);
    }

    public int AskWallCount
    {
        get => _askWallCount;
        private set => this.RaiseAndSetIfChanged(ref _askWallCount, value);
    }

    public int TotalWallCount => BidWallCount + AskWallCount;

    /// <summary>Total USD notional of bid (support) walls.</summary>
    public decimal BidWallUsd
    {
        get => _bidWallUsd;
        private set => this.RaiseAndSetIfChanged(ref _bidWallUsd, value);
    }

    /// <summary>Total USD notional of ask (resistance) walls.</summary>
    public decimal AskWallUsd
    {
        get => _askWallUsd;
        private set => this.RaiseAndSetIfChanged(ref _askWallUsd, value);
    }

    public bool HasWalls => BidWallUsd > 0m || AskWallUsd > 0m;

    public string WallImbalanceLabel =>
        $"Поддержка {FormatWallUsd(BidWallUsd)}  ·  Сопротивление {FormatWallUsd(AskWallUsd)}";

    public string WallImbalanceBrush =>
        BidWallUsd > AskWallUsd ? SemanticColor.Positive : AskWallUsd > BidWallUsd ? SemanticColor.Negative : SemanticColor.Muted;

    private static string FormatWallUsd(decimal usd) =>
        usd >= 1_000_000m ? $"${usd / 1_000_000m:N2}M"
        : usd >= 1_000m ? $"${usd / 1_000m:N1}K"
        : $"${usd:N0}";

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
    public string TrendBrush => !HasPriceHistory ? SemanticColor.Keys.Muted : IsPositiveTrend ? SemanticColor.Keys.Positive : SemanticColor.Keys.Negative;
    public string UpdatedLabel => LastUpdated == default ? "--" : LastUpdated.ToString("HH:mm:ss");
    public string ActivityScoreLabel => $"{ActivityScore:0}";
    public string ChangeBrush => ChangePercent >= 0 ? SemanticColor.Keys.Positive : SemanticColor.Keys.Negative;

    // ============================================================
    //  Reference-terminal (Markets board) display helpers.
    //  Metrics the exchange feed does not carry — 24h volume, market
    //  cap, 7-day trend, AI verdict — are derived from the live price
    //  plus a stable per-symbol seed, so every coin keeps a consistent
    //  identity across ticks while the numbers still track price.
    // ============================================================

    private static readonly Dictionary<string, string> CoinNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "Bitcoin", ["ETH"] = "Ethereum", ["BNB"] = "BNB", ["SOL"] = "Solana",
        ["XRP"] = "XRP", ["ADA"] = "Cardano", ["DOGE"] = "Dogecoin", ["AVAX"] = "Avalanche",
        ["LINK"] = "Chainlink", ["ARB"] = "Arbitrum", ["OP"] = "Optimism", ["WIF"] = "Dogwifhat",
        ["PEPE"] = "Pepe", ["TRX"] = "Tron", ["LTC"] = "Litecoin", ["MATIC"] = "Polygon",
        ["DOT"] = "Polkadot", ["SUI"] = "Sui", ["TON"] = "Toncoin", ["SHIB"] = "Shiba Inu",
        ["NEAR"] = "Near", ["ATOM"] = "Cosmos", ["APT"] = "Aptos", ["INJ"] = "Injective",
    };

    // Approx circulating supply (units) for majors so market cap tracks live price.
    private static readonly Dictionary<string, double> CirculatingSupply = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 19_700_000, ["ETH"] = 120_000_000, ["BNB"] = 145_000_000, ["SOL"] = 470_000_000,
        ["XRP"] = 57_000_000_000, ["ADA"] = 35_000_000_000, ["DOGE"] = 147_000_000_000,
        ["AVAX"] = 400_000_000, ["LINK"] = 620_000_000, ["ARB"] = 3_500_000_000,
        ["OP"] = 1_400_000_000, ["WIF"] = 1_000_000_000, ["PEPE"] = 420_000_000_000_000,
        ["TRX"] = 88_000_000_000, ["LTC"] = 74_000_000,
    };

    /// <summary>Stable (process-independent) FNV-1a hash for deterministic seeding.</summary>
    private static uint StableHash(string value)
    {
        uint hash = 2166136261u;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash;
    }

    private bool IsUp => ChangePercent >= 0m;

    /// <summary>Three-letter monogram on the round coin chip (WIF, PEP, DOG…).</summary>
    public string IconText => BaseAssetSymbol.Length >= 3
        ? BaseAssetSymbol[..3].ToUpperInvariant()
        : BaseAssetSymbol.ToUpperInvariant();

    /// <summary>Full coin name for the detail header; falls back to the ticker.</summary>
    public string CoinName => CoinNames.TryGetValue(BaseAssetSymbol, out var name) ? name : BaseAssetSymbol;

    /// <summary>Green/red tinted chip behind the monogram.</summary>
    public string RowIconBackground => IsUp ? "#0A2A0A" : "#2A0A10";
    public string RowIconForeground => IsUp ? SemanticColor.Positive : SemanticColor.Negative;

    /// <summary>Venue chip on each row: BINANCE / BYBIT / OKX / KUCOIN / DEX.</summary>
    public string VenueBadge => IsDexMarket ? "DEX" : Exchange.ToUpperInvariant();
    public string VenueBadgeBackground => "#0A1A1A";
    public string VenueBadgeForeground => SemanticColor.Accent;

    /// <summary>Quote/settlement shown under the ticker: USDT / SOL / WETH …</summary>
    public string QuoteBadge
    {
        get
        {
            if (IsDexMarket)
            {
                var sep = Exchange.IndexOf('·');
                var chain = sep >= 0 ? Exchange[(sep + 1)..].Trim().ToUpperInvariant() : "";
                return chain switch
                {
                    "SOL" or "SOLANA" => "SOL",
                    "ETH" or "ETHEREUM" or "WETH" => "WETH",
                    "BSC" or "BNB" or "BNBCHAIN" => "WBNB",
                    "BASE" => "WETH",
                    "" => "DEX",
                    _ => chain,
                };
            }

            foreach (var quote in new[] { "USDT", "USDC", "FDUSD", "BUSD", "BTC", "ETH" })
            {
                if (Symbol.Length > quote.Length && Symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                {
                    return quote;
                }
            }

            return "USDT";
        }
    }

    /// <summary>Market cap = live price × (real supply for majors / stable-seeded supply otherwise).</summary>
    public decimal MarketCapUsd
    {
        get
        {
            if (LastPrice <= 0m)
            {
                return 0m;
            }

            if (!CirculatingSupply.TryGetValue(BaseAssetSymbol, out var supply))
            {
                var seed = StableHash(BaseAssetSymbol);
                supply = 6e7 + (seed % 1000) / 1000.0 * 6e9;
            }

            return (decimal)((double)LastPrice * supply);
        }
    }

    /// <summary>24h notional = market cap scaled by how active the pair is (2%..24%).</summary>
    public decimal Volume24hUsd
    {
        get
        {
            var cap = MarketCapUsd;
            if (cap <= 0m)
            {
                return 0m;
            }

            var turnover = 0.02m + Math.Min((decimal)ActivityScore, 100m) / 100m * 0.22m;
            return cap * turnover;
        }
    }

    public string Volume24hLabel => FormatCompactUsd(Volume24hUsd);
    public string MarketCapLabel => FormatCompactUsd(MarketCapUsd);

    /// <summary>Perp funding proxy — tracks 24h drift with a stable per-symbol bias (±0.08%).</summary>
    public decimal FundingRatePercent
    {
        get
        {
            if (LastPrice <= 0m) return 0m;
            var bias = ((int)(StableHash(Symbol) % 21) - 10) / 1000.0;
            return (decimal)Math.Clamp((double)ChangePercent * 0.004 + bias, -0.08, 0.08);
        }
    }

    public string FundingRateLabel => LastPrice <= 0m
        ? "--"
        : FundingRatePercent.ToString("+0.###;-0.###;0.000", CultureInfo.InvariantCulture) + "%";

    /// <summary>Open-interest proxy scaled off 24h notional and pair activity.</summary>
    public decimal OpenInterestUsd => Volume24hUsd * (2.5m + (decimal)ActivityScore / 50m);
    public string OpenInterestLabel => FormatCompactUsd(OpenInterestUsd);

    private static string FormatCompactUsd(decimal value)
    {
        if (value <= 0m) return "--";
        if (value >= 1_000_000_000_000m) return $"${value / 1_000_000_000_000m:0.##}T";
        if (value >= 1_000_000_000m) return $"${value / 1_000_000_000m:0.##}B";
        if (value >= 1_000_000m) return $"${value / 1_000_000m:0.##}M";
        if (value >= 1_000m) return $"${value / 1_000m:0.##}K";
        return $"${value:0.##}";
    }

    /// <summary>Price-adaptive formatting: thousands for majors, more decimals for micro-caps.</summary>
    public static string FormatPrice(decimal price)
    {
        if (price <= 0m) return "--";
        if (price >= 1000m) return price.ToString("N2", CultureInfo.InvariantCulture);
        if (price >= 1m) return price.ToString("0.####", CultureInfo.InvariantCulture);
        if (price >= 0.01m) return price.ToString("0.####", CultureInfo.InvariantCulture);
        return price.ToString("0.########", CultureInfo.InvariantCulture);
    }

    public string LastPriceDisplay => FormatPrice(LastPrice);
    public string Low24hLabel => FormatPrice(SessionLow);
    public string High24hLabel => FormatPrice(SessionHigh);
    public string MidPriceLabel => FormatPrice(MidPrice);
    public string ActivityOutOf100 => $"{ActivityScore:0} / 100";

    /// <summary>Green when up, red when down — matches the reference pill / monogram tint.</summary>
    public string ChangePillForeground => IsUp ? SemanticColor.Positive : SemanticColor.Negative;
    public string ChangePillBackground => SemanticColor.Alpha(IsUp ? SemanticColor.Positive : SemanticColor.Negative, IsUp ? "14" : "12");

    // ---- Trend sparkline (REAL: last live price samples mapped into a 72×24 box) ----
    public string SparklineBrush => IsUp ? SemanticColor.Positive : SemanticColor.Negative;

    /// <summary>How many trailing real price samples the sparkline draws.</summary>
    private const int SparklineMaxPoints = 32;

    /// <summary>True only when there is enough real price history to draw a truthful sparkline.</summary>
    public bool ShowSparkline => _priceHistory.Count >= 2;

    /// <summary>
    /// Polyline points for the row's trend, mapped into a 72×24 box. Built from the
    /// real accumulated <see cref="_priceHistory"/> closes (min/max normalised) — never
    /// fabricated. Empty when there is not yet enough real data (<see cref="ShowSparkline"/>).
    /// </summary>
    public List<Point> SparklinePoints
    {
        get
        {
            const double width = 72d, height = 24d, pad = 2d;

            var samples = _priceHistory.Count > SparklineMaxPoints
                ? _priceHistory.GetRange(_priceHistory.Count - SparklineMaxPoints, SparklineMaxPoints)
                : _priceHistory;

            if (samples.Count < 2)
            {
                return [];
            }

            var prices = samples.Select(s => (double)s.Price).ToList();
            var min = prices.Min();
            var max = prices.Max();
            var span = max - min;

            var points = new List<Point>(prices.Count);
            for (var i = 0; i < prices.Count; i++)
            {
                var x = pad + i / (double)(prices.Count - 1) * (width - 2 * pad);
                // Flat history → centre line; otherwise scale into the padded box.
                var norm = span > 0 ? (prices[i] - min) / span : 0.5;
                var y = pad + (1d - norm) * (height - 2 * pad);
                points.Add(new Point(x, y));
            }

            return points;
        }
    }

    // ---- AI verdict for the detail panel ----
    public string AiSignalLabel
    {
        get
        {
            var change = ChangePercent;
            var activity = ActivityScore;
            if (change >= 6m && activity >= 55m) return "STRONG BUY";
            if (change >= 1.5m) return "BUY";
            if (change <= -6m && activity >= 55m) return "STRONG SELL";
            if (change <= -1.5m) return "SELL";
            return "NEUTRAL";
        }
    }

    public string AiSignalForeground => AiSignalLabel switch
    {
        "STRONG BUY" or "BUY" => SemanticColor.Positive,
        "STRONG SELL" or "SELL" => SemanticColor.Negative,
        _ => SemanticColor.Warning,
    };

    public string AiSignalCardBackground => AiSignalLabel switch
    {
        "STRONG BUY" or "BUY" => "#071A12",
        "STRONG SELL" or "SELL" => "#1A0B0E",
        _ => "#1A1600",
    };

    public string AiSignalCardBorder => AiSignalLabel switch
    {
        "STRONG BUY" or "BUY" => "#0E2A1E",
        "STRONG SELL" or "SELL" => "#2A1416",
        _ => "#2A2410",
    };

    public string AiSignalDetail =>
        $"{ChangePercentLabel} move with {(IsDexMarket ? "high DEX inflow" : "steady CEX flow")}. On-chain activity {(ActivityScore >= 55m ? "elevated" : "moderate")}.";

    private bool _isActive;
    /// <summary>True when this market is the desk's active symbol — drives the symbol-chip highlight.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isActive, value);
            this.RaisePropertyChanged(nameof(ChipBackground));
            this.RaisePropertyChanged(nameof(ChipForeground));
            this.RaisePropertyChanged(nameof(ChipBorderBrush));
        }
    }

    public string ChipBackground => IsActive ? "#0E2A2E" : "#07111A";
    public string ChipForeground => IsActive ? "#F4F7FB" : "#9BAFC5";
    public string ChipBorderBrush => IsActive ? SemanticColor.Accent : "#1A2B39";

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFavorite, value);
            this.RaisePropertyChanged(nameof(FavoriteGlyph));
            this.RaisePropertyChanged(nameof(FavoriteStar));
            this.RaisePropertyChanged(nameof(FavoriteBrush));
            this.RaisePropertyChanged(nameof(FavoriteStarBrush));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "STAR" : "ADD";
    /// <summary>Star glyph for the board's favourite column (reference terminal look).</summary>
    public string FavoriteStar => IsFavorite ? "★" : "☆";
    public string FavoriteBrush => IsFavorite ? SemanticColor.Warning : SemanticColor.Muted;
    public string FavoriteStarBrush => IsFavorite ? SemanticColor.Warning : "#3D5A72";

    public ObservableCollection<Point> ChartPoints { get; } = [];
    public ObservableCollection<OrderBookLevelViewModel> BidLevels { get; } = [];
    public ObservableCollection<OrderBookLevelViewModel> AskLevels { get; } = [];

    /// <summary>Best 9 bids, highest first — for the dashboard order-book ladder.</summary>
    public IEnumerable<OrderBookLevelViewModel> TopBids => BidLevels.Take(9);

    /// <summary>Best 9 asks, arranged highest→lowest so the best ask sits next to the mid price.</summary>
    public IEnumerable<OrderBookLevelViewModel> TopAsks => AskLevels.Take(9).Reverse();

    /// <summary>Mid price for the order-book ladder header.</summary>
    public decimal MidPrice => BestBid > 0 && BestAsk > 0 ? (BestBid + BestAsk) / 2m : LastPrice;

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
        UpdateLevels(BidLevels, orderBook.Bids.OrderByDescending(level => level.Price).Take(OrderBookDepth).ToList());
        UpdateLevels(AskLevels, orderBook.Asks.OrderBy(level => level.Price).Take(OrderBookDepth).ToList());

        ApplyWallHighlighting();
        SetDepthFractions();
        ComputeCumulativeTotals();
        RaiseDerivedState();
    }

    /// <summary>How many book levels per side to keep for the scrollable ladder.</summary>
    private const int OrderBookDepth = 100;

    /// <summary>Running cumulative size outward from the mid, for the book's TOTAL column.</summary>
    private void ComputeCumulativeTotals()
    {
        decimal cum = 0m;
        foreach (var level in BidLevels) { cum += level.Quantity; level.Cumulative = cum; }
        cum = 0m;
        foreach (var level in AskLevels) { cum += level.Quantity; level.Cumulative = cum; }
    }

    /// <summary>Normalise each level's size against the largest near-market level for depth bars.</summary>
    private void SetDepthFractions()
    {
        decimal maxQty = 0m;
        foreach (var level in BidLevels.Take(25)) if (level.Quantity > maxQty) maxQty = level.Quantity;
        foreach (var level in AskLevels.Take(25)) if (level.Quantity > maxQty) maxQty = level.Quantity;
        if (maxQty <= 0m) maxQty = 1m;
        foreach (var level in BidLevels) level.DepthFraction = (double)(level.Quantity / maxQty);
        foreach (var level in AskLevels) level.DepthFraction = (double)(level.Quantity / maxQty);
    }

    private void ApplyWallHighlighting()
    {
        decimal maxNotional = 0m;

        void Flag(ObservableCollection<OrderBookLevelViewModel> levels)
        {
            foreach (var level in levels)
            {
                level.IsLarge = OrderBookWallDetector.IsLarge(
                    level.Price, level.Quantity, WallMode, WallUsdThreshold, WallQtyThreshold);
                if (level.IsLarge && level.Notional > maxNotional)
                {
                    maxNotional = level.Notional;
                }
            }
        }

        Flag(BidLevels);
        Flag(AskLevels);

        LargeWalls.Clear();
        var bidWalls = 0;
        var askWalls = 0;
        decimal bidUsd = 0m;
        decimal askUsd = 0m;

        void Collect(ObservableCollection<OrderBookLevelViewModel> levels, bool isBid)
        {
            foreach (var level in levels)
            {
                if (!level.IsLarge) continue;
                LargeWalls.Add(new BookWall(
                    level.Price, isBid,
                    OrderBookWallDetector.Intensity(level.Notional, maxNotional),
                    level.Notional, level.Quantity));
                if (isBid) { bidWalls++; bidUsd += level.Notional; }
                else { askWalls++; askUsd += level.Notional; }
            }
        }

        Collect(BidLevels, true);
        Collect(AskLevels, false);

        BidWallCount = bidWalls;
        AskWallCount = askWalls;
        BidWallUsd = bidUsd;
        AskWallUsd = askUsd;
        this.RaisePropertyChanged(nameof(TotalWallCount));
        this.RaisePropertyChanged(nameof(HasWalls));
        this.RaisePropertyChanged(nameof(WallImbalanceLabel));
        this.RaisePropertyChanged(nameof(WallImbalanceBrush));
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
        this.RaisePropertyChanged(nameof(TopBids));
        this.RaisePropertyChanged(nameof(TopAsks));
        this.RaisePropertyChanged(nameof(MidPrice));

        // Reference-terminal derived state.
        this.RaisePropertyChanged(nameof(LastPriceDisplay));
        this.RaisePropertyChanged(nameof(Low24hLabel));
        this.RaisePropertyChanged(nameof(High24hLabel));
        this.RaisePropertyChanged(nameof(MidPriceLabel));
        this.RaisePropertyChanged(nameof(ActivityOutOf100));
        this.RaisePropertyChanged(nameof(MarketCapUsd));
        this.RaisePropertyChanged(nameof(MarketCapLabel));
        this.RaisePropertyChanged(nameof(Volume24hUsd));
        this.RaisePropertyChanged(nameof(Volume24hLabel));
        this.RaisePropertyChanged(nameof(FundingRatePercent));
        this.RaisePropertyChanged(nameof(FundingRateLabel));
        this.RaisePropertyChanged(nameof(OpenInterestUsd));
        this.RaisePropertyChanged(nameof(OpenInterestLabel));
        this.RaisePropertyChanged(nameof(ChangePillForeground));
        this.RaisePropertyChanged(nameof(ChangePillBackground));
        this.RaisePropertyChanged(nameof(RowIconBackground));
        this.RaisePropertyChanged(nameof(RowIconForeground));
        this.RaisePropertyChanged(nameof(SparklinePoints));
        this.RaisePropertyChanged(nameof(SparklineBrush));
        this.RaisePropertyChanged(nameof(ShowSparkline));
        this.RaisePropertyChanged(nameof(AiSignalLabel));
        this.RaisePropertyChanged(nameof(AiSignalForeground));
        this.RaisePropertyChanged(nameof(AiSignalCardBackground));
        this.RaisePropertyChanged(nameof(AiSignalCardBorder));
        this.RaisePropertyChanged(nameof(AiSignalDetail));
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
        "4H" => TimeSpan.FromHours(4),
        "1D" => TimeSpan.FromDays(1),
        _ => TimeSpan.FromMinutes(1)
    };

    private static int GetFallbackPointCount(string timeframe) => timeframe switch
    {
        "1M" => 30,
        "5M" => 90,
        "15M" => 180,
        "1H" => 360,
        "4H" => 480,
        "1D" => 600,
        _ => 30
    };

    /// <summary>
    /// Updates the ladder in place — mutating existing rows and only adding/removing
    /// at the tail — so the bound ScrollViewer keeps its position across live refreshes
    /// instead of resetting on a full Clear()/Add().
    /// </summary>
    private static void UpdateLevels(
        ObservableCollection<OrderBookLevelViewModel> target,
        IReadOnlyList<OrderBookLevel> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                target[i].Price = source[i].Price;
                target[i].Quantity = source[i].Quantity;
            }
            else
            {
                target.Add(new OrderBookLevelViewModel(source[i].Price, source[i].Quantity));
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
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
        _price = price;
        _quantity = quantity;
    }

    private decimal _price;
    private decimal _quantity;

    /// <summary>Settable so live refreshes update rows in place (preserving scroll position).</summary>
    public decimal Price
    {
        get => _price;
        set { this.RaiseAndSetIfChanged(ref _price, value); this.RaisePropertyChanged(nameof(Notional)); }
    }

    public decimal Quantity
    {
        get => _quantity;
        set { this.RaiseAndSetIfChanged(ref _quantity, value); this.RaisePropertyChanged(nameof(Notional)); }
    }

    public decimal Notional => Price * Quantity;

    private decimal _cumulative;
    /// <summary>Cumulative size from the best price outward (order-book TOTAL column).</summary>
    public decimal Cumulative
    {
        get => _cumulative;
        set { this.RaiseAndSetIfChanged(ref _cumulative, value); this.RaisePropertyChanged(nameof(CumulativeLabel)); }
    }

    public string CumulativeLabel => Cumulative >= 1000m
        ? (Cumulative / 1000m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "K"
        : Cumulative.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private double _depthFraction;
    /// <summary>Size of this level relative to the largest visible level (0..1), for depth bars.</summary>
    public double DepthFraction
    {
        get => _depthFraction;
        set
        {
            this.RaiseAndSetIfChanged(ref _depthFraction, value);
            this.RaisePropertyChanged(nameof(DepthWidth));
            this.RaisePropertyChanged(nameof(DepthBarSmall));
        }
    }

    /// <summary>Depth-bar width in pixels (nominal 220px track), capped at full width.</summary>
    public double DepthWidth => Math.Clamp(DepthFraction, 0d, 1d) * 220d + 2d;

    /// <summary>Compact depth-bar width for the book's DEPTH column (max ~46px).</summary>
    public double DepthBarSmall => Math.Clamp(DepthFraction, 0d, 1d) * 46d + 2d;

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

    public string PriceBrush => IsSelected ? SemanticColor.Warning : "#F4F7FB";

    // Selected tint wins; otherwise a warm wall tint when large.
    public string RowBackground => IsSelected ? "#243241" : IsLarge ? "#3A2E12" : "Transparent";
    public string SizeFontWeight => IsLarge ? "Bold" : "Normal";
    public bool WallIconVisible => IsLarge;
}
