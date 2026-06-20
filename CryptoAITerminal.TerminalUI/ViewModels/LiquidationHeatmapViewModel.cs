using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class LiquidationHeatmapViewModel : ReactiveObject, IDisposable
{
    // ── Render constants (canvas coordinate space 1200×460) ───────────────────
    // Canvas in XAML is 1200×460, NO Viewbox, direct 1:1 pixel mapping.
    // HorizontalAlignment="Center" + ClipToBounds on Border adapts to any screen width.
    private const double RW        = 1200.0; // canvas width
    private const double Cx        =  600.0; // center X (longs left, shorts right)
    private const double MaxHalf   =  590.0; // max half-bar width
    private const double BarH      =   16.0; // bar height (thicker = more visible)
    private const double RH        =  460.0; // canvas height
    private const double RangeF    =   0.20; // ±20 % price range
    private const double LabelStep =  0.025; // axis label every 2.5 %

    private readonly LiquidationDataService _svc;
    private readonly DispatcherTimer _refreshTimer;  // full reload every 5 min
    private readonly DispatcherTimer _priceTimer;    // lightweight price tick every 5 sec
    private CancellationTokenSource _cts = new();

    private string   _symbol          = "BTCUSDT";
    private bool     _isLoading;
    private string   _statusLabel     = "Idle";
    private Geometry _longBarsGeometry  = Geometry.Parse("");
    private Geometry _shortBarsGeometry = Geometry.Parse("");
    private Geometry _currentPriceGeometry = Geometry.Parse("");
    private string   _dataSourceLabel = "";
    private decimal  _currentPrice;
    private string   _topLongLabel    = "";
    private string   _topShortLabel   = "";
    private bool     _showLongs       = true;
    private bool     _showShorts      = true;
    private IReadOnlyList<LiquidationLevel> _levels      = [];
    private IReadOnlyList<PriceAxisLabel>   _priceLabels = [];

    // ── Proximity alert state ─────────────────────────────────────────────────
    private const decimal AlertProximityPct  = 1.5m;  // alert when price is within 1.5% of a major cluster
    private const decimal MajorClusterMinUsd = 50_000_000m; // $50M minimum cluster size
    private string  _proximityAlertMessage   = "";
    private bool    _isProximityAlertActive;
    private readonly HashSet<decimal> _firedAlerts = [];

    // ── Public properties ─────────────────────────────────────────────────────

    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value?.ToUpperInvariant() ?? "BTCUSDT");
    }
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }
    public string StatusLabel
    {
        get => _statusLabel;
        private set => this.RaiseAndSetIfChanged(ref _statusLabel, value);
    }

    /// <summary>Geometry for long liquidation bars (teal, left of centre).</summary>
    public Geometry LongBarsGeometry
    {
        get => _longBarsGeometry;
        private set => this.RaiseAndSetIfChanged(ref _longBarsGeometry, value);
    }
    /// <summary>Geometry for short liquidation bars (red, right of centre).</summary>
    public Geometry ShortBarsGeometry
    {
        get => _shortBarsGeometry;
        private set => this.RaiseAndSetIfChanged(ref _shortBarsGeometry, value);
    }
    /// <summary>Horizontal dashed line at the current price Y position.</summary>
    public Geometry CurrentPriceGeometry
    {
        get => _currentPriceGeometry;
        private set => this.RaiseAndSetIfChanged(ref _currentPriceGeometry, value);
    }

    public string DataSourceLabel
    {
        get => _dataSourceLabel;
        private set => this.RaiseAndSetIfChanged(ref _dataSourceLabel, value);
    }
    public decimal CurrentPrice
    {
        get => _currentPrice;
        private set => this.RaiseAndSetIfChanged(ref _currentPrice, value);
    }
    public string TopLongLabel
    {
        get => _topLongLabel;
        private set => this.RaiseAndSetIfChanged(ref _topLongLabel, value);
    }
    public string TopShortLabel
    {
        get => _topShortLabel;
        private set => this.RaiseAndSetIfChanged(ref _topShortLabel, value);
    }
    public bool ShowLongs
    {
        get => _showLongs;
        set { this.RaiseAndSetIfChanged(ref _showLongs, value); RenderHeatmap(); }
    }
    public bool ShowShorts
    {
        get => _showShorts;
        set { this.RaiseAndSetIfChanged(ref _showShorts, value); RenderHeatmap(); }
    }
    public IReadOnlyList<PriceAxisLabel> PriceLabels
    {
        get => _priceLabels;
        private set => this.RaiseAndSetIfChanged(ref _priceLabels, value);
    }

    /// <summary>Human-readable proximity alert message, empty when no alert.</summary>
    public string ProximityAlertMessage
    {
        get => _proximityAlertMessage;
        private set => this.RaiseAndSetIfChanged(ref _proximityAlertMessage, value);
    }

    public bool IsProximityAlertActive
    {
        get => _isProximityAlertActive;
        private set => this.RaiseAndSetIfChanged(ref _isProximityAlertActive, value);
    }

    /// <summary>
    /// Cluster overlay rows for the main chart: price levels with colour intensity
    /// proportional to the notional size of the cluster.
    /// </summary>
    public IReadOnlyList<LiqClusterOverlay> ClusterOverlay { get; private set; } = [];

    /// <summary>
    /// Raised (on UI thread) when price approaches a major liquidation cluster.
    /// Callers (MainWindowViewModel) wire this to ShowToast / AddLog.
    /// </summary>
    public event Action<string>? ProximityAlertTriggered;

    public ReactiveCommand<Unit, Unit>   RefreshCommand   { get; }
    public ReactiveCommand<string, Unit> SetSymbolCommand { get; }
    public ReactiveCommand<Unit, Unit>   AnalyzeWithAiCommand { get; }

    // ── AI liquidation insight (#13) ────────────────────────────────────────────
    private readonly MarketInsightAiService _insight = new();
    private bool _insightRunning, _hasInsight;
    private string _insightSummary = "", _insightSignal = "", _insightBullets = "", _insightSource = "";

    public bool InsightRunning { get => _insightRunning; private set => this.RaiseAndSetIfChanged(ref _insightRunning, value); }
    public bool HasInsight { get => _hasInsight; private set => this.RaiseAndSetIfChanged(ref _hasInsight, value); }
    public string InsightSummary { get => _insightSummary; private set => this.RaiseAndSetIfChanged(ref _insightSummary, value); }
    public string InsightSignal { get => _insightSignal; private set => this.RaiseAndSetIfChanged(ref _insightSignal, value); }
    public string InsightBullets { get => _insightBullets; private set => this.RaiseAndSetIfChanged(ref _insightBullets, value); }
    public string InsightSource { get => _insightSource; private set => this.RaiseAndSetIfChanged(ref _insightSource, value); }
    public string InsightSignalBrush => _insightSignal switch { "MAGNET_ABOVE" => "#3DDC84", "MAGNET_BELOW" => "#FF6B6B", _ => "#8FA3B8" };

    public void ConfigureAi(string apiKey, string model)
    {
        _insight.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _insight.Model = model;
    }

    private async System.Threading.Tasks.Task AnalyzeWithAiAsync()
    {
        if (InsightRunning) return;
        var levels = _levels;
        if (levels.Count == 0) { InsightSummary = "No liquidation levels loaded."; HasInsight = true; return; }

        var price = CurrentPrice;
        decimal shortAbove = levels.Where(l => l.Price > price).Sum(l => l.ShortLiqUsd); // shorts liquidate as price rises
        decimal longBelow  = levels.Where(l => l.Price < price).Sum(l => l.LongLiqUsd);  // longs liquidate as price falls

        var lines = new System.Collections.Generic.List<string>
        {
            $"Current price: {price:0.##}",
            $"Top long cluster: {TopLongLabel}",
            $"Top short cluster: {TopShortLabel}",
            $"Short liquidations above price: ${shortAbove:0}",
            $"Long liquidations below price: ${longBelow:0}",
        };

        InsightRunning = true;
        try
        {
            var offline = () =>
            {
                var total = shortAbove + longBelow;
                var skew = total > 0 ? (shortAbove - longBelow) / total : 0m;
                var signal = skew > 0.2m ? "MAGNET_ABOVE" : skew < -0.2m ? "MAGNET_BELOW" : "BALANCED";
                var summary = signal switch
                {
                    "MAGNET_ABOVE" => $"Heavier short liquidations above (${shortAbove:0}) — price may be pulled UP to hunt them.",
                    "MAGNET_BELOW" => $"Heavier long liquidations below (${longBelow:0}) — downside liquidity magnet.",
                    _ => "Liquidation clusters are balanced on both sides."
                };
                return new CryptoAITerminal.AIEngine.InsightResult(summary, signal, lines.ToArray(), "Heuristic (offline)", true);
            };
            var result = await _insight.InterpretAsync(
                "You are a liquidation-liquidity analyst. Large clusters of opposing-side liquidations act as price magnets (price tends to move toward resting liquidity).",
                lines, ["MAGNET_ABOVE", "MAGNET_BELOW", "BALANCED"], offline).ConfigureAwait(true);
            InsightSummary = result.Summary; InsightSignal = result.Signal;
            InsightBullets = result.Bullets.Length > 0 ? "• " + string.Join("\n• ", result.Bullets) : "";
            InsightSource = result.Source; HasInsight = true;
            this.RaisePropertyChanged(nameof(InsightSignalBrush));
        }
        catch (Exception ex) { InsightSummary = $"AI failed: {ex.Message}"; HasInsight = true; }
        finally { InsightRunning = false; }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public LiquidationHeatmapViewModel(LiquidationDataService svc)
    {
        _svc = svc;
        DataSourceLabel = svc.DataSourceLabel;
        AnalyzeWithAiCommand = ReactiveCommand.CreateFromTask(AnalyzeWithAiAsync, outputScheduler: App.UiScheduler);

        RefreshCommand = ReactiveCommand.CreateFromTask(
            _ => LoadAsync(), outputScheduler: App.UiScheduler);

        SetSymbolCommand = ReactiveCommand.CreateFromTask<string>(async sym =>
        {
            Symbol = sym;
            await LoadAsync();
        }, outputScheduler: App.UiScheduler);

        // Full reload (re-fetches from Binance/CoinGlass) every 5 minutes
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _refreshTimer.Tick += async (_, _) => await LoadAsync();
        _refreshTimer.Start();

        // Lightweight live-price tick every 5 seconds: just re-fetches current price,
        // rebuilds leverage model in-place, and redraws — no full HTTP roundtrip.
        _priceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _priceTimer.Tick += async (_, _) =>
        {
            if (_isLoading) return;          // full load in progress — skip
            if (_levels.Count == 0) return;  // not initialised yet
            var price = await _svc.FetchCurrentPriceAsync(_symbol);
            if (price <= 0) return;
            _levels       = LiquidationDataService.BuildLeverageModel(price);
            CurrentPrice  = price;
            RenderHeatmap();
            StatusLabel = $"Live  {DateTime.Now:HH:mm:ss}  ·  {FormatPrice((double)price)}";
        };
        _priceTimer.Start();

        _ = LoadAsync();
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        IsLoading   = true;
        StatusLabel = "Loading…";
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            _levels = await _svc.GetLevelsAsync(_symbol, ct);
            if (ct.IsCancellationRequested) return;

            // Always set price from freshly-loaded levels — keeps _currentPrice and _levels
            // consistent even if UpdateCurrentPrice had set a stale value earlier.
            if (_levels.Count > 0)
            {
                var mid = (_levels.Min(l => l.Price) + _levels.Max(l => l.Price)) / 2m;
                CurrentPrice = mid;
            }

            RenderHeatmap();
            UpdateStats();
            StatusLabel = $"Updated {DateTime.Now:HH:mm:ss}  ·  {_levels.Count} levels  ·  {FormatPrice((double)_currentPrice)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusLabel = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Called from MainWindowViewModel only when data.Symbol == this.Symbol.
    /// Rebuilds the leverage model at the new price and re-renders.
    /// </summary>
    public void UpdateCurrentPrice(decimal price)
    {
        if (price <= 0) return;
        if (_levels.Count > 0)
            _levels = LiquidationDataService.BuildLeverageModel(price);
        CurrentPrice = price;
        RenderHeatmap();
        CheckProximityAlerts();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void RenderHeatmap()
    {
        if (_levels.Count == 0 || _currentPrice <= 0) return;

        var minPrice = (double)_currentPrice * (1.0 - RangeF);
        var maxPrice = (double)_currentPrice * (1.0 + RangeF);
        var range    = maxPrice - minPrice;

        var inRange = _levels
            .Where(l => (double)l.Price >= minPrice && (double)l.Price <= maxPrice)
            .ToList();
        if (inRange.Count == 0) return;

        var maxL = (double)(inRange.Where(l => l.LongLiqUsd  > 0).Select(l => l.LongLiqUsd ).DefaultIfEmpty(1m).Max());
        var maxS = (double)(inRange.Where(l => l.ShortLiqUsd > 0).Select(l => l.ShortLiqUsd).DefaultIfEmpty(1m).Max());

        // Build StreamGeometry objects directly — no string parsing, no type conversion.
        var geoL = new StreamGeometry();
        var geoS = new StreamGeometry();

        using (var ctxL = geoL.Open())
        using (var ctxS = geoS.Open())
        {
            foreach (var lvl in inRange)
            {
                // Y: higher price → smaller Y (top = maxPrice, bottom = minPrice)
                var y  = (1.0 - ((double)lvl.Price - minPrice) / range) * RH;
                var y2 = y + BarH;

                if (_showLongs && lvl.LongLiqUsd > 0)
                {
                    var w = (double)lvl.LongLiqUsd / maxL * MaxHalf;
                    ctxL.BeginFigure(new Point(Cx,     y),  isFilled: true);
                    ctxL.LineTo(new Point(Cx - w, y));
                    ctxL.LineTo(new Point(Cx - w, y2));
                    ctxL.LineTo(new Point(Cx,     y2));
                    ctxL.EndFigure(true);
                }

                if (_showShorts && lvl.ShortLiqUsd > 0)
                {
                    var w = (double)lvl.ShortLiqUsd / maxS * MaxHalf;
                    ctxS.BeginFigure(new Point(Cx,     y),  isFilled: true);
                    ctxS.LineTo(new Point(Cx + w, y));
                    ctxS.LineTo(new Point(Cx + w, y2));
                    ctxS.LineTo(new Point(Cx,     y2));
                    ctxS.EndFigure(true);
                }
            }
        }

        LongBarsGeometry  = geoL;
        ShortBarsGeometry = geoS;

        // Current-price horizontal line
        var py = (1.0 - ((double)_currentPrice - minPrice) / range) * RH;
        py = Math.Clamp(py, 0, RH);
        var lineGeo = new StreamGeometry();
        using (var lc = lineGeo.Open())
        {
            lc.BeginFigure(new Point(0,  py), isFilled: false);
            lc.LineTo(new Point(RW, py));
            lc.EndFigure(false);
        }
        CurrentPriceGeometry = lineGeo;

        // ── Price axis labels ─────────────────────────────────────────────────
        var labels = new List<PriceAxisLabel>();
        for (var pct = -RangeF; pct <= RangeF + 0.001; pct += LabelStep)
        {
            var lp = (double)_currentPrice * (1.0 + pct);
            var yL = (1.0 - (lp - minPrice) / range) * RH;
            if (yL < 0 || yL > RH) continue;
            labels.Add(new PriceAxisLabel(FormatPrice(lp), yL, Math.Abs(pct) < 0.001));
        }
        PriceLabels = labels;
    }

    private void UpdateStats()
    {
        if (_levels.Count == 0) return;
        var topL = _levels.OrderByDescending(l => l.LongLiqUsd).First();
        var topS = _levels.OrderByDescending(l => l.ShortLiqUsd).First();
        TopLongLabel  = $"{FormatPrice((double)topL.Price)}  {FormatUsd(topL.LongLiqUsd)}";
        TopShortLabel = $"{FormatPrice((double)topS.Price)}  {FormatUsd(topS.ShortLiqUsd)}";

        RebuildClusterOverlay();
        CheckProximityAlerts();
    }

    // ── Cluster overlay for chart ─────────────────────────────────────────────

    private void RebuildClusterOverlay()
    {
        if (_levels.Count == 0 || _currentPrice <= 0) { ClusterOverlay = []; return; }

        // Show clusters within ±15% of current price, sorted by price descending
        var range = _currentPrice * 0.15m;
        var min   = _currentPrice - range;
        var max   = _currentPrice + range;

        var maxNotional = _levels.Max(l => l.LongLiqUsd + l.ShortLiqUsd);
        if (maxNotional <= 0) { ClusterOverlay = []; return; }

        var overlay = _levels
            .Where(l => l.Price >= min && l.Price <= max)
            .Where(l => l.LongLiqUsd + l.ShortLiqUsd >= MajorClusterMinUsd)
            .OrderByDescending(l => l.Price)
            .Select(l =>
            {
                var total     = l.LongLiqUsd + l.ShortLiqUsd;
                var intensity = Math.Clamp((double)(total / maxNotional), 0.1, 1.0);
                var side      = l.LongLiqUsd >= l.ShortLiqUsd ? "long" : "short";
                return new LiqClusterOverlay(l.Price, total, side, intensity);
            })
            .ToList();

        ClusterOverlay = overlay;
        this.RaisePropertyChanged(nameof(ClusterOverlay));
    }

    // ── Proximity alerts ──────────────────────────────────────────────────────

    private void CheckProximityAlerts()
    {
        if (_levels.Count == 0 || _currentPrice <= 0) return;

        var messages = new System.Collections.Generic.List<string>();
        bool anyAlert = false;

        foreach (var level in _levels)
        {
            var total = level.LongLiqUsd + level.ShortLiqUsd;
            if (total < MajorClusterMinUsd) continue;

            var distPct = Math.Abs((level.Price - _currentPrice) / _currentPrice * 100m);
            if (distPct > AlertProximityPct) continue;

            anyAlert = true;
            var dir    = level.Price < _currentPrice ? "below" : "above";
            var side   = level.LongLiqUsd >= level.ShortLiqUsd ? "long liquidations" : "short liquidations";
            var msg    = $"⚡ Price within {distPct:N1}% of {FormatUsd(total)} {side} cluster {dir} @ {FormatPrice((double)level.Price)}";
            messages.Add(msg);

            // Rising-edge only: only fire toast/log once per cluster price level
            var key = Math.Round(level.Price, 0);
            if (_firedAlerts.Add(key))
                ProximityAlertTriggered?.Invoke(msg);
        }

        // Reset fired alerts for levels no longer near price
        _firedAlerts.RemoveWhere(key =>
        {
            var price = key;
            var distPct = Math.Abs((price - _currentPrice) / _currentPrice * 100m);
            return distPct > AlertProximityPct * 2;
        });

        ProximityAlertMessage  = messages.Count > 0 ? messages[0] : "";
        IsProximityAlertActive = anyAlert;
    }

    /// <summary>Bar length for a liquidation level: proportional to USD, clamped to the usable width.</summary>
    public static double BarWidth(double usd, double maxUsd, double usableWidth)
        => maxUsd <= 0 ? 0.0 : System.Math.Clamp(usd / maxUsd, 0.0, 1.0) * usableWidth;

    // ── Formatters ────────────────────────────────────────────────────────────

    private static string FormatPrice(double p) =>
        p >= 10_000 ? $"${p:N0}"
        : p >= 1_000 ? $"${p:N0}"
        : p >= 1     ? $"${p:N2}"
        : $"${p:N4}";

    private static string FormatUsd(decimal v) =>
        v >= 1_000_000_000m ? $"${v / 1_000_000_000m:N1}B"
        : v >= 1_000_000m   ? $"${v / 1_000_000m:N1}M"
        : v >= 1_000m       ? $"${v / 1_000m:N0}K"
        : $"${v:N0}";

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _refreshTimer.Stop();
        _priceTimer.Stop();
        _cts.Cancel();
        _svc.Dispose();
    }
}

public record PriceAxisLabel(string Text, double Y, bool IsCurrentPrice);

/// <summary>A liquidation cluster line suitable for chart overlay rendering.</summary>
/// <param name="Price">Price level of the cluster.</param>
/// <param name="NotionalUsd">Total USD at risk at this level.</param>
/// <param name="Side">"long" or "short".</param>
/// <param name="Intensity">0–1 relative size vs. the largest cluster in range.</param>
public record LiqClusterOverlay(decimal Price, decimal NotionalUsd, string Side, double Intensity)
{
    public string NotionalLabel => NotionalUsd >= 1_000_000_000m ? $"${NotionalUsd / 1_000_000_000m:N1}B"
        : NotionalUsd >= 1_000_000m ? $"${NotionalUsd / 1_000_000m:N1}M"
        : $"${NotionalUsd / 1_000m:N0}K";

    public string Color => Side == "long" ? "#21E6C1" : "#FF6B6B";
    public string PriceLabel => NotionalUsd >= 1_000_000m
        ? $"{Price:N0}  {NotionalLabel}"
        : $"{Price:N0}";
}
