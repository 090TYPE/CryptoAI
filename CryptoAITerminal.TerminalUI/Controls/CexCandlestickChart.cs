using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Controls;

public class CexCandlestickChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DexOhlcvPoint>?> CandlesProperty =
        AvaloniaProperty.Register<CexCandlestickChart, IReadOnlyList<DexOhlcvPoint>?>(nameof(Candles));

    public static readonly StyledProperty<string?> ToolModeProperty =
        AvaloniaProperty.Register<CexCandlestickChart, string?>(nameof(ToolMode), defaultValue: "Cursor");

    public static readonly StyledProperty<int> ClearDrawingsVersionProperty =
        AvaloniaProperty.Register<CexCandlestickChart, int>(nameof(ClearDrawingsVersion));

    public static readonly StyledProperty<int> ResetViewVersionProperty =
        AvaloniaProperty.Register<CexCandlestickChart, int>(nameof(ResetViewVersion));

    public static readonly StyledProperty<string?> PersistenceKeyProperty =
        AvaloniaProperty.Register<CexCandlestickChart, string?>(nameof(PersistenceKey));

    public static readonly StyledProperty<bool> ShowVwapProperty =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowVwap), defaultValue: true);

    public static readonly StyledProperty<bool> ShowVolumeProfileProperty =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowVolumeProfile), defaultValue: true);

    public static readonly StyledProperty<IReadOnlyList<BookWall>?> WallsProperty =
        AvaloniaProperty.Register<CexCandlestickChart, IReadOnlyList<BookWall>?>(nameof(Walls));

    /// <summary>"Candles" | "HA" (Heikin-Ashi) | "Line" | "Area".</summary>
    public static readonly StyledProperty<string?> ChartTypeProperty =
        AvaloniaProperty.Register<CexCandlestickChart, string?>(nameof(ChartType), defaultValue: "Candles");

    public static readonly StyledProperty<bool> ShowMa20Property =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowMa20), defaultValue: true);

    public static readonly StyledProperty<bool> ShowMa50Property =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowMa50), defaultValue: true);

    public static readonly StyledProperty<bool> ShowBollingerProperty =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowBollinger));

    public static readonly StyledProperty<bool> ShowRsiProperty =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowRsi));

    public static readonly StyledProperty<bool> ShowWallsProperty =
        AvaloniaProperty.Register<CexCandlestickChart, bool>(nameof(ShowWalls), defaultValue: true);

    public static readonly StyledProperty<int> ZoomInVersionProperty =
        AvaloniaProperty.Register<CexCandlestickChart, int>(nameof(ZoomInVersion));

    public static readonly StyledProperty<int> ZoomOutVersionProperty =
        AvaloniaProperty.Register<CexCandlestickChart, int>(nameof(ZoomOutVersion));

    private const int MinimumVisibleCandles = 20;
    private const int DefaultRecentVisibleCandles = 180;
    private static readonly Dictionary<string, List<ChartDrawing>> PersistedDrawings = new(StringComparer.OrdinalIgnoreCase);

    // Every colour on this chart is a compile-time constant, so the brushes and pens are built
    // once here instead of inside the render loop — the candle loop alone ran a Color.Parse and
    // two allocations per candle, on every repaint. Palette tokens obey the same rule: the
    // application resources are read from these initialisers only, never from Render.
    private static readonly Color AccentColor = ChartPalette.Get("Accent", "#21E6C1");
    private static readonly Color PositiveColor = ChartPalette.Get("Positive", "#3DDC84");
    private static readonly Color NegativeColor = ChartPalette.Get("Negative", "#FF6B6B");
    private static readonly Color WarningColor = ChartPalette.Get("Warning", "#F4B860");

    private static readonly SolidColorBrush BackdropBrush = ChartPalette.Brush("Surface1", "#07101A");
    private static readonly SolidColorBrush ChartFillBrush = ChartPalette.Brush("SurfaceInner2", "#0A131E");
    private static readonly SolidColorBrush PriceFillBrush = ChartPalette.Brush("Surface2", "#0A1422");
    private static readonly SolidColorBrush TimeFillBrush = ChartPalette.Brush("Surface2", "#0A1422");
    // Растущая свеча здесь — бренд-акцент, а не Positive: тем же цветом рисуются линия в
    // режимах Line/Area, её заливка и заглушка загрузки.
    private static readonly SolidColorBrush BullBrush = new(AccentColor);
    private static readonly SolidColorBrush BearBrush = new(NegativeColor);
    private static readonly SolidColorBrush AreaFillBrush = new(ChartPalette.Fade(AccentColor, 60));
    private static readonly SolidColorBrush AxisTextBrush = new(Color.Parse("#DCE8F5"));
    private static readonly SolidColorBrush LastPriceLabelBrush = new(Color.Parse("#19C9AF"));
    private static readonly SolidColorBrush MarkerBackgroundBrush = new(Color.Parse("#16212C"));
    private static readonly SolidColorBrush HoverBoxBrush = ChartPalette.Brush("Surface3", "#0D1828");
    private static readonly SolidColorBrush RsiPanelBrush = ChartPalette.Brush("SurfaceInner2", "#0A131E");
    private static readonly SolidColorBrush RsiLabelBrush = new(Color.Parse("#6E8196"));
    private static readonly SolidColorBrush RsiLowBrush = new(PositiveColor);
    private static readonly SolidColorBrush RsiMidBrush = new(WarningColor);
    private static readonly SolidColorBrush VwapLabelBrush = new(Color.Parse("#FBBF24"));
    private static readonly SolidColorBrush VpNormalBrush = new(Color.Parse("#1A3A5C"));
    private static readonly SolidColorBrush VpPocBrush = new(Color.Parse("#7C3AED"));
    private static readonly SolidColorBrush VpValueBrush = new(Color.Parse("#1A4A6C"));
    private static readonly SolidColorBrush PocLabelBrush = new(Color.Parse("#A855F7"));
    private static readonly SolidColorBrush VpAxisLabelBrush = new(Color.Parse("#60A5FA"));
    private static readonly Color WallBidColor = Color.Parse("#42F5B1");
    private static readonly Color WallAskColor = NegativeColor;
    private static readonly SolidColorBrush WallBidLabelBrush = new(WallBidColor);
    private static readonly SolidColorBrush WallAskLabelBrush = new(WallAskColor);

    private static readonly Pen ChartBorderPen = new(new SolidColorBrush(Color.Parse("#223243")), 1);
    private static readonly Pen PriceBorderPen = new(new SolidColorBrush(Color.Parse("#2A4053")), 1);
    private static readonly Pen GridPen = new(ChartPalette.Brush("PanelStroke", "#152233"), 1);
    private static readonly Pen BullPen = new(BullBrush, 1);
    private static readonly Pen BearPen = new(BearBrush, 1);
    private static readonly Pen LinePen = new(BullBrush, 1.6);
    private static readonly Pen Ma20Pen = new(new SolidColorBrush(WarningColor), 1.4);
    private static readonly Pen Ma50Pen = new(new SolidColorBrush(Color.Parse("#8FB9DE")), 1.4);
    private static readonly Pen BollingerBandPen = new(new SolidColorBrush(Color.Parse("#7C5CFF")), 1.0) { DashStyle = new DashStyle([4, 3], 0) };
    private static readonly Pen BollingerMidPen = new(new SolidColorBrush(Color.FromArgb(140, 124, 92, 255)), 0.9);
    private static readonly Pen RsiPanelPen = new(new SolidColorBrush(Color.Parse("#1A2B39")), 1);
    private static readonly Pen RsiGuide70Pen = new(new SolidColorBrush(ChartPalette.Fade(NegativeColor, 80)), 0.8) { DashStyle = new DashStyle([3, 3], 0) };
    private static readonly Pen RsiGuide30Pen = new(new SolidColorBrush(ChartPalette.Fade(PositiveColor, 80)), 0.8) { DashStyle = new DashStyle([3, 3], 0) };
    private static readonly Pen RsiHighPen = new(BearBrush, 1.3);
    private static readonly Pen RsiLowPen = new(RsiLowBrush, 1.3);
    private static readonly Pen RsiMidPen = new(RsiMidBrush, 1.3);
    private static readonly Pen LastPriceLinePen = new(new SolidColorBrush(Color.Parse("#365A66")), 1);
    private static readonly Pen CrosshairPen = new(new SolidColorBrush(Color.Parse("#4E677E")), 1);
    private static readonly Pen HoverBoxPen = new(new SolidColorBrush(Color.Parse("#274055")), 1);
    private static readonly Pen TrendPreviewPen = new(new SolidColorBrush(Color.Parse("#E7C65C")), 2);
    private static readonly Pen RectanglePreviewPen = new(new SolidColorBrush(PositiveColor), 2);
    private static readonly Pen ChannelPreviewPen = new(new SolidColorBrush(Color.Parse("#F59E0B")), 2);
    private static readonly Pen VwapPen = new(VwapLabelBrush, 1.6);
    private static readonly Pen VwapSigma1Pen = new(new SolidColorBrush(Color.Parse("#60A5FA")), 1.0) { DashStyle = new DashStyle([5, 3], 0) };
    private static readonly Pen VwapSigma2Pen = new(new SolidColorBrush(Color.Parse("#3B82F6")), 0.8) { DashStyle = new DashStyle([3, 4], 0) };
    private static readonly Pen PocPen = new(PocLabelBrush, 1.0) { DashStyle = new DashStyle([6, 3], 0) };
    private static readonly Pen VahLinePen = new(new SolidColorBrush(Color.Parse("#3060A0")), 0.7) { DashStyle = new DashStyle([4, 4], 0) };

    private readonly List<ChartDrawing> _drawings = [];
    private INotifyCollectionChanged? _subscribedCollection;
    private INotifyCollectionChanged? _subscribedWalls;
    private bool _attached;
    private IReadOnlyList<DexOhlcvPoint> _allCandles = Array.Empty<DexOhlcvPoint>();
    private IReadOnlyList<DexOhlcvPoint> _visibleCandles = Array.Empty<DexOhlcvPoint>();
    // Cache keys so Render can skip the per-frame filter+sort+copy of the full candle
    // list when membership hasn't changed. DexOhlcvPoint is a class, so in-place value
    // updates on the forming candle are already visible through the cached references —
    // only a collection swap or a Count change needs a rebuild.
    private object? _lastCandlesRef;
    private int _lastCandlesCount = -1;
    // Full-history MA / Bollinger / RSI / Heikin-Ashi arrays, kept across frames (see EnsureFullSeries).
    private FullSeries? _fullSeries;
    private SeriesKey _fullSeriesKey;
    // Pointer position the last repaint was issued for — a move that changes nothing on screen
    // must not schedule another full chart render.
    private Point _lastRenderedPointer;
    private Rect _chartBounds;
    private Rect _priceBounds;
    private Rect _timeBounds;
    private Rect _rsiBounds;
    private decimal _minVisiblePrice;
    private decimal _maxVisiblePrice;
    private bool _hasChartLayout;
    private bool _isPanning;
    private bool _pointerInsideChart;
    private Point _pointerPosition;
    private Point _lastPanPoint;
    private int _visibleStartIndex;
    private int _visibleCount;
    private ChartAnchor? _pendingTrendAnchor;
    private RectangleDraft? _pendingRectangle;
    private ChannelDraft? _pendingChannel;
    private string? _loadedPersistenceKey;
    private bool _hasPendingViewRestore;
    private int _pendingVisibleCount;
    private int _pendingRightOffset;

    static CexCandlestickChart()
    {
        AffectsRender<CexCandlestickChart>(CandlesProperty, ToolModeProperty, ClearDrawingsVersionProperty, ResetViewVersionProperty, PersistenceKeyProperty, ShowVwapProperty, ShowVolumeProfileProperty, WallsProperty, ChartTypeProperty, ShowMa20Property, ShowMa50Property, ShowBollingerProperty, ShowRsiProperty, ShowWallsProperty);
        FocusableProperty.OverrideDefaultValue<CexCandlestickChart>(true);
    }

    public IReadOnlyList<DexOhlcvPoint>? Candles
    {
        get => GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    public string? ToolMode
    {
        get => GetValue(ToolModeProperty);
        set => SetValue(ToolModeProperty, value);
    }

    public int ClearDrawingsVersion
    {
        get => GetValue(ClearDrawingsVersionProperty);
        set => SetValue(ClearDrawingsVersionProperty, value);
    }

    public int ResetViewVersion
    {
        get => GetValue(ResetViewVersionProperty);
        set => SetValue(ResetViewVersionProperty, value);
    }

    public string? PersistenceKey
    {
        get => GetValue(PersistenceKeyProperty);
        set => SetValue(PersistenceKeyProperty, value);
    }

    public bool ShowVwap
    {
        get => GetValue(ShowVwapProperty);
        set => SetValue(ShowVwapProperty, value);
    }

    public bool ShowVolumeProfile
    {
        get => GetValue(ShowVolumeProfileProperty);
        set => SetValue(ShowVolumeProfileProperty, value);
    }

    public IReadOnlyList<BookWall>? Walls
    {
        get => GetValue(WallsProperty);
        set => SetValue(WallsProperty, value);
    }

    public string? ChartType
    {
        get => GetValue(ChartTypeProperty);
        set => SetValue(ChartTypeProperty, value);
    }

    public bool ShowMa20
    {
        get => GetValue(ShowMa20Property);
        set => SetValue(ShowMa20Property, value);
    }

    public bool ShowMa50
    {
        get => GetValue(ShowMa50Property);
        set => SetValue(ShowMa50Property, value);
    }

    public bool ShowBollinger
    {
        get => GetValue(ShowBollingerProperty);
        set => SetValue(ShowBollingerProperty, value);
    }

    public bool ShowRsi
    {
        get => GetValue(ShowRsiProperty);
        set => SetValue(ShowRsiProperty, value);
    }

    public bool ShowWalls
    {
        get => GetValue(ShowWallsProperty);
        set => SetValue(ShowWallsProperty, value);
    }

    public int ZoomInVersion
    {
        get => GetValue(ZoomInVersionProperty);
        set => SetValue(ZoomInVersionProperty, value);
    }

    public int ZoomOutVersion
    {
        get => GetValue(ZoomOutVersionProperty);
        set => SetValue(ZoomOutVersionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CandlesProperty)
        {
            SubscribeToCandles();
            RefreshAllCandles();
            ResetViewWindowIfNeeded();
            InvalidateVisual();
            return;
        }

        if (change.Property == WallsProperty)
        {
            SubscribeToWalls();
            InvalidateVisual();
            return;
        }

        if (change.Property == ClearDrawingsVersionProperty)
        {
            _drawings.Clear();
            CancelPendingToolState();
            PersistDrawings();
            InvalidateVisual();
            return;
        }

        if (change.Property == ResetViewVersionProperty)
        {
            ResetViewWindow();
            InvalidateVisual();
            return;
        }

        if (change.Property == ZoomInVersionProperty)
        {
            ApplyZoom(0.8d);
            return;
        }

        if (change.Property == ZoomOutVersionProperty)
        {
            ApplyZoom(1.25d);
            return;
        }

        if (change.Property == PersistenceKeyProperty)
        {
            LoadPersistedDrawings();
            InvalidateVisual();
            return;
        }

        if (change.Property == ToolModeProperty)
        {
            CancelPendingToolState();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(8, 8, Math.Max(0, Bounds.Width - 16), Math.Max(0, Bounds.Height - 16));
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var priceAxisWidth = 98d;
        var timeAxisHeight = 34d;
        var rsiHeight = ShowRsi ? 78d : 0d;
        var plotWidth = Math.Max(0, bounds.Width - priceAxisWidth);
        var plotHeight = Math.Max(0, bounds.Height - timeAxisHeight);
        _chartBounds = new Rect(bounds.Left, bounds.Top, plotWidth, Math.Max(0, plotHeight - rsiHeight));
        _rsiBounds = ShowRsi
            ? new Rect(bounds.Left, _chartBounds.Bottom, plotWidth, rsiHeight)
            : default;
        _priceBounds = new Rect(_chartBounds.Right, _chartBounds.Top, priceAxisWidth, _chartBounds.Height);
        _timeBounds = new Rect(bounds.Left, bounds.Top + plotHeight, plotWidth, timeAxisHeight);
        _hasChartLayout = _chartBounds.Width > 0 && _chartBounds.Height > 0;

        DrawBackdrop(context, bounds, _chartBounds, _priceBounds, _timeBounds);
        DrawGrid(context, _chartBounds);

        EnsureCandlesRefreshedForRender();
        if (_allCandles.Count == 0)
        {
            DrawEmptyState(context, _chartBounds);
            return;
        }

        EnsureViewWindow();
        _visibleCandles = _allCandles.Skip(_visibleStartIndex).Take(_visibleCount).ToList();
        if (_visibleCandles.Count == 0)
        {
            DrawEmptyState(context, _chartBounds);
            return;
        }

        // Indicators/series computed up front so the price range can grow to
        // include any overlay (MA, Bollinger, Heikin-Ashi) that extends past the
        // raw candle range and would otherwise clip.
        var series = BuildSeries();

        _minVisiblePrice = _visibleCandles.Min(candle => candle.Low);
        _maxVisiblePrice = _visibleCandles.Max(candle => candle.High);
        series.ExpandRange(ref _minVisiblePrice, ref _maxVisiblePrice);

        var padding = (_maxVisiblePrice - _minVisiblePrice) * 0.06m;
        if (padding <= 0)
        {
            padding = Math.Max(_maxVisiblePrice * 0.002m, 0.00000001m);
        }

        _minVisiblePrice -= padding;
        _maxVisiblePrice += padding;

        DrawPriceAxis(context);
        DrawTimeAxis(context);
        DrawPriceSeries(context, series);
        if (ShowVolumeProfile) DrawVolumeProfile(context);
        if (ShowVwap) DrawVwap(context);
        DrawIndicatorOverlays(context, series);
        DrawDrawings(context);
        if (ShowWalls) DrawWalls(context);
        DrawLastPriceMarker(context, _visibleCandles[^1].Close, MapY(_visibleCandles[^1].Close));
        if (ShowRsi) DrawRsiPanel(context, series);
        DrawCrosshair(context);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!_hasChartLayout)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_chartBounds.Contains(position) || _allCandles.Count <= MinimumVisibleCandles)
        {
            return;
        }

        EnsureViewWindow();
        var currentCount = Math.Max(MinimumVisibleCandles, _visibleCount);
        var newCount = e.Delta.Y > 0
            ? (int)Math.Round(currentCount * 0.85d)
            : (int)Math.Round(currentCount * 1.15d);

        newCount = Math.Clamp(newCount, MinimumVisibleCandles, _allCandles.Count);
        if (newCount == _visibleCount)
        {
            return;
        }

        var anchorFraction = Math.Clamp((position.X - _chartBounds.Left) / Math.Max(1d, _chartBounds.Width), 0d, 1d);
        var anchorIndex = _visibleStartIndex + (anchorFraction * Math.Max(0, _visibleCount - 1));
        _visibleStartIndex = ClampVisibleStart((int)Math.Round(anchorIndex - (anchorFraction * Math.Max(0, newCount - 1))), newCount);
        _visibleCount = newCount;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!_hasChartLayout)
        {
            return;
        }

        var position = e.GetPosition(this);
        _pointerPosition = position;
        _pointerInsideChart = _chartBounds.Contains(position);
        if (!_pointerInsideChart)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            CancelPendingToolState();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var mode = ToolMode ?? "Cursor";
        if (TryHandleToolClick(mode, position))
        {
            e.Handled = true;
            return;
        }

        _isPanning = true;
        _lastPanPoint = position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_hasChartLayout)
        {
            return;
        }

        var position = e.GetPosition(this);
        var wasInsideChart = _pointerInsideChart;
        _pointerPosition = position;
        // Show the crosshair whenever the pointer is anywhere over the drawn chart
        // (plot + price/time axes), not just the narrow plot rectangle. DrawCrosshair
        // clamps the lines back into the plot, so the readout always follows the mouse.
        _pointerInsideChart = position.X >= _chartBounds.Left
            && position.X <= _priceBounds.Right
            && position.Y >= _chartBounds.Top
            && position.Y <= _timeBounds.Bottom;

        var panned = false;
        if (_isPanning && _allCandles.Count > _visibleCount)
        {
            var slotWidth = _chartBounds.Width / Math.Max(1, _visibleCount);
            if (slotWidth > 0)
            {
                var deltaX = position.X - _lastPanPoint.X;
                var deltaCandles = (int)Math.Round(deltaX / slotWidth);
                if (deltaCandles != 0)
                {
                    _visibleStartIndex = ClampVisibleStart(_visibleStartIndex - deltaCandles, _visibleCount);
                    _lastPanPoint = position;
                    panned = true;
                }
            }
        }

        // Outside the chart nothing follows the pointer, and a sub-pixel move redraws the
        // crosshair in the same place — either way a full repaint would be wasted.
        var crosshairMoved = _pointerInsideChart
            && (Math.Abs(position.X - _lastRenderedPointer.X) >= 0.5d
                || Math.Abs(position.Y - _lastRenderedPointer.Y) >= 0.5d);
        if (!panned && !crosshairMoved && wasInsideChart == _pointerInsideChart)
        {
            return;
        }

        _lastRenderedPointer = position;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInsideChart = false;
        if (!_isPanning)
        {
            InvalidateVisual();
        }
    }

    private bool TryHandleToolClick(string mode, Point position)
    {
        if (!TryCreateAnchor(position, out var anchor))
        {
            return false;
        }

        if (string.Equals(mode, "Trend", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingTrendAnchor is null)
            {
                _pendingTrendAnchor = anchor;
            }
            else
            {
                _drawings.Add(ChartDrawing.CreateTrend(_pendingTrendAnchor.Value, anchor));
                PersistDrawings();
                _pendingTrendAnchor = null;
            }

            InvalidateVisual();
            return true;
        }

        if (string.Equals(mode, "Horizontal", StringComparison.OrdinalIgnoreCase))
        {
            _drawings.Add(ChartDrawing.CreateHorizontal(anchor.Price));
            PersistDrawings();
            InvalidateVisual();
            return true;
        }

        if (string.Equals(mode, "Rectangle", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingRectangle is null)
            {
                _pendingRectangle = new RectangleDraft(anchor);
            }
            else
            {
                _drawings.Add(ChartDrawing.CreateRectangle(_pendingRectangle.Value.Start, anchor));
                PersistDrawings();
                _pendingRectangle = null;
            }

            InvalidateVisual();
            return true;
        }

        if (string.Equals(mode, "Channel", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingChannel is null)
            {
                _pendingChannel = new ChannelDraft(anchor, null);
            }
            else if (_pendingChannel.Value.SecondAnchor is null)
            {
                _pendingChannel = new ChannelDraft(_pendingChannel.Value.Start, anchor);
            }
            else
            {
                _drawings.Add(ChartDrawing.CreateChannel(_pendingChannel.Value.Start, _pendingChannel.Value.SecondAnchor.Value, anchor));
                PersistDrawings();
                _pendingChannel = null;
            }

            InvalidateVisual();
            return true;
        }

        if (string.Equals(mode, "Erase", StringComparison.OrdinalIgnoreCase))
        {
            if (TryRemoveDrawing(position))
            {
                PersistDrawings();
                InvalidateVisual();
            }

            return true;
        }

        return false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        SubscribeToCandles();
        SubscribeToWalls();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        SubscribeToCandles();
        SubscribeToWalls();
    }

    /// <summary>
    /// Subscribes only while the control is in the tree: the candle and wall collections belong
    /// to a long-lived view model, so a handler left behind by a removed chart keeps the control
    /// alive and burns a repaint on every tick.
    /// </summary>
    private void SubscribeToCandles()
    {
        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnCandlesCollectionChanged;
            _subscribedCollection = null;
        }

        if (_attached && Candles is INotifyCollectionChanged notifyCollectionChanged)
        {
            _subscribedCollection = notifyCollectionChanged;
            _subscribedCollection.CollectionChanged += OnCandlesCollectionChanged;
        }
    }

    private void SubscribeToWalls()
    {
        if (_subscribedWalls is not null)
        {
            _subscribedWalls.CollectionChanged -= OnWallsCollectionChanged;
            _subscribedWalls = null;
        }

        if (_attached && Walls is INotifyCollectionChanged notify)
        {
            _subscribedWalls = notify;
            _subscribedWalls.CollectionChanged += OnWallsCollectionChanged;
        }
    }

    private void OnWallsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private void RefreshAllCandles()
    {
        _allCandles = Candles?
            .Where(candle => candle.High > 0 && candle.Low > 0 && candle.Open > 0 && candle.Close > 0)
            .OrderBy(candle => candle.Timestamp)
            .ToList() ?? (IReadOnlyList<DexOhlcvPoint>)Array.Empty<DexOhlcvPoint>();

        // Remember what we rebuilt from so Render can skip redundant rebuilds.
        _lastCandlesRef = Candles;
        _lastCandlesCount = Candles?.Count ?? 0;
    }

    /// <summary>Rebuilds the sorted/filtered candle list only when the source collection
    /// reference or item count changed since the last build. Called every frame from
    /// <see cref="Render"/> — the guard turns a per-frame Where+OrderBy+ToList over the
    /// whole history into a couple of cheap comparisons on the common repaint paths
    /// (crosshair move, pan, zoom, hover).</summary>
    private void EnsureCandlesRefreshedForRender()
    {
        if (ReferenceEquals(Candles, _lastCandlesRef) && (Candles?.Count ?? 0) == _lastCandlesCount)
        {
            return;
        }

        RefreshAllCandles();
    }

    private void ResetViewWindowIfNeeded()
    {
        if (_visibleCount <= 0)
        {
            ResetViewWindow();
            return;
        }

        EnsureViewWindow();
    }

    /// <summary>Zoom the visible window around its right edge. factor &lt; 1 zooms in.</summary>
    private void ApplyZoom(double factor)
    {
        RefreshAllCandles();
        if (_allCandles.Count <= MinimumVisibleCandles)
        {
            return;
        }

        EnsureViewWindow();
        var rightOffset = Math.Max(0, _allCandles.Count - (_visibleStartIndex + _visibleCount));
        var newCount = (int)Math.Round(_visibleCount * factor);
        newCount = Math.Clamp(newCount, MinimumVisibleCandles, _allCandles.Count);
        _visibleCount = newCount;
        _visibleStartIndex = ClampVisibleStart(_allCandles.Count - newCount - rightOffset, newCount);
        InvalidateVisual();
    }

    private void ResetViewWindow()
    {
        RefreshAllCandles();
        if (_allCandles.Count == 0)
        {
            _visibleStartIndex = 0;
            _visibleCount = 0;
            return;
        }

        _visibleCount = Math.Min(_allCandles.Count, Math.Max(MinimumVisibleCandles, DefaultRecentVisibleCandles));
        _visibleStartIndex = ClampVisibleStart(_allCandles.Count - _visibleCount, _visibleCount);
    }

    private void EnsureViewWindow()
    {
        var total = _allCandles.Count;
        if (total == 0)
        {
            return;
        }

        if (_visibleCount <= 0 || _visibleCount > total)
        {
            _visibleCount = total;
        }

        if (_visibleCount < MinimumVisibleCandles && total >= MinimumVisibleCandles)
        {
            _visibleCount = MinimumVisibleCandles;
        }

        _visibleStartIndex = ClampVisibleStart(_visibleStartIndex, _visibleCount);
    }

    private int ClampVisibleStart(int requestedStart, int visibleCount)
    {
        var maxStart = Math.Max(0, _allCandles.Count - visibleCount);
        return Math.Clamp(requestedStart, 0, maxStart);
    }

    private void DrawBackdrop(DrawingContext context, Rect bounds, Rect chartBounds, Rect priceBounds, Rect timeBounds)
    {
        context.DrawRectangle(BackdropBrush, null, bounds);
        context.DrawRectangle(ChartFillBrush, ChartBorderPen, chartBounds);
        context.DrawRectangle(PriceFillBrush, PriceBorderPen, priceBounds);
        context.DrawRectangle(TimeFillBrush, ChartBorderPen, timeBounds);
    }

    private void DrawGrid(DrawingContext context, Rect chartBounds)
    {
        for (var row = 1; row < 10; row++)
        {
            var y = chartBounds.Top + (chartBounds.Height / 10d * row);
            context.DrawLine(GridPen, new Point(chartBounds.Left, y), new Point(chartBounds.Right, y));
        }

        for (var column = 1; column < 10; column++)
        {
            var x = chartBounds.Left + (chartBounds.Width / 10d * column);
            context.DrawLine(GridPen, new Point(x, chartBounds.Top), new Point(x, chartBounds.Bottom));
        }
    }

    private void DrawPriceSeries(DrawingContext context, SeriesData series)
    {
        var type = (ChartType ?? "Candles").Trim();
        if (string.Equals(type, "Line", StringComparison.OrdinalIgnoreCase))
        {
            DrawLineArea(context, series.Close, fill: false);
            return;
        }

        if (string.Equals(type, "Area", StringComparison.OrdinalIgnoreCase))
        {
            DrawLineArea(context, series.Close, fill: true);
            return;
        }

        DrawCandleSeries(context, series, useHa: string.Equals(type, "HA", StringComparison.OrdinalIgnoreCase));
    }

    private void DrawCandleSeries(DrawingContext context, SeriesData series, bool useHa)
    {
        var slotWidth = _chartBounds.Width / Math.Max(1, _visibleCandles.Count);
        var candleBodyWidth = Math.Clamp(slotWidth * 0.72d, 3d, 24d);

        for (var visibleIndex = 0; visibleIndex < _visibleCandles.Count; visibleIndex++)
        {
            var candle = _visibleCandles[visibleIndex];
            decimal open, high, low, close;
            if (useHa && series.HasHa)
            {
                open = (decimal)series.HaOpen[visibleIndex];
                high = (decimal)series.HaHigh[visibleIndex];
                low = (decimal)series.HaLow[visibleIndex];
                close = (decimal)series.HaClose[visibleIndex];
            }
            else
            {
                open = candle.Open;
                high = candle.High;
                low = candle.Low;
                close = candle.Close;
            }

            var globalIndex = _visibleStartIndex + visibleIndex;
            var centerX = MapX(globalIndex);
            var openY = MapY(open);
            var closeY = MapY(close);
            var highY = MapY(high);
            var lowY = MapY(low);
            var bullish = close >= open;
            var brush = bullish ? BullBrush : BearBrush;
            var pen = bullish ? BullPen : BearPen;

            context.DrawLine(pen, new Point(centerX, highY), new Point(centerX, lowY));

            var bodyTop = Math.Min(openY, closeY);
            var bodyBottom = Math.Max(openY, closeY);
            var bodyHeight = Math.Max(2d, bodyBottom - bodyTop);
            var bodyRect = new Rect(centerX - (candleBodyWidth / 2d), bodyTop, candleBodyWidth, bodyHeight);
            context.DrawRectangle(brush, pen, bodyRect);
        }
    }

    private void DrawLineArea(DrawingContext context, double[] closes, bool fill)
    {
        if (closes.Length < 2)
        {
            return;
        }

        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            var started = false;
            for (var i = 0; i < closes.Length; i++)
            {
                var p = new Point(MapX(_visibleStartIndex + i), MapY((decimal)closes[i]));
                if (!started) { gc.BeginFigure(p, false); started = true; }
                else gc.LineTo(p);
            }

            gc.EndFigure(false);
        }

        if (fill)
        {
            var fillGeo = new StreamGeometry();
            using (var gc = fillGeo.Open())
            {
                gc.BeginFigure(new Point(MapX(_visibleStartIndex), _chartBounds.Bottom), true);
                for (var i = 0; i < closes.Length; i++)
                {
                    gc.LineTo(new Point(MapX(_visibleStartIndex + i), MapY((decimal)closes[i])));
                }

                gc.LineTo(new Point(MapX(_visibleStartIndex + closes.Length - 1), _chartBounds.Bottom));
                gc.EndFigure(true);
            }

            context.DrawGeometry(AreaFillBrush, null, fillGeo);
        }

        context.DrawGeometry(null, LinePen, geo);
    }

    private void DrawIndicatorOverlays(DrawingContext context, SeriesData series)
    {
        if (ShowMa20 && series.HasMa20)
        {
            DrawSeriesLine(context, series.Ma20, Ma20Pen);
        }

        if (ShowMa50 && series.HasMa50)
        {
            DrawSeriesLine(context, series.Ma50, Ma50Pen);
        }

        if (ShowBollinger && series.HasBb)
        {
            DrawSeriesLine(context, series.BbUpper, BollingerBandPen);
            DrawSeriesLine(context, series.BbLower, BollingerBandPen);
            DrawSeriesLine(context, series.BbMid, BollingerMidPen);
        }
    }

    private void DrawSeriesLine(DrawingContext context, double[] values, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            var started = false;
            for (var i = 0; i < values.Length; i++)
            {
                if (double.IsNaN(values[i]))
                {
                    started = false;
                    continue;
                }

                var p = new Point(MapX(_visibleStartIndex + i), MapY((decimal)values[i]));
                if (!started) { gc.BeginFigure(p, false); started = true; }
                else gc.LineTo(p);
            }

            gc.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geo);
    }

    private void DrawRsiPanel(DrawingContext context, SeriesData series)
    {
        if (_rsiBounds.Width <= 0 || _rsiBounds.Height <= 0)
        {
            return;
        }

        context.DrawRectangle(RsiPanelBrush, RsiPanelPen, _rsiBounds);

        double RsiY(double value) => _rsiBounds.Bottom - (Math.Clamp(value, 0d, 100d) / 100d * _rsiBounds.Height);

        context.DrawLine(RsiGuide70Pen, new Point(_rsiBounds.Left, RsiY(70)), new Point(_rsiBounds.Right, RsiY(70)));
        context.DrawLine(RsiGuide30Pen, new Point(_rsiBounds.Left, RsiY(30)), new Point(_rsiBounds.Right, RsiY(30)));

        var label = new FormattedText("RSI 14", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, RsiLabelBrush);
        context.DrawText(label, new Point(_rsiBounds.Left + 6, _rsiBounds.Top + 4));

        if (!series.HasRsi)
        {
            return;
        }

        var lastRsi = 50d;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            var started = false;
            for (var i = 0; i < series.Rsi.Length; i++)
            {
                if (double.IsNaN(series.Rsi[i]))
                {
                    started = false;
                    continue;
                }

                lastRsi = series.Rsi[i];
                var p = new Point(MapX(_visibleStartIndex + i), RsiY(series.Rsi[i]));
                if (!started) { gc.BeginFigure(p, false); started = true; }
                else gc.LineTo(p);
            }

            gc.EndFigure(false);
        }

        var rsiBrush = lastRsi >= 70 ? BearBrush : lastRsi <= 30 ? RsiLowBrush : RsiMidBrush;
        var rsiPen = lastRsi >= 70 ? RsiHighPen : lastRsi <= 30 ? RsiLowPen : RsiMidPen;
        context.DrawGeometry(null, rsiPen, geo);

        var valueLabel = new FormattedText(lastRsi.ToString("0.0", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold), 11, rsiBrush);
        context.DrawText(valueLabel, new Point(_rsiBounds.Right - valueLabel.Width - 6, _rsiBounds.Top + 4));
    }

    /// <summary>Compute overlay series over all candles, sliced to the visible window.</summary>
    private SeriesData BuildSeries()
    {
        var full = EnsureFullSeries();
        var start = _visibleStartIndex;
        var count = _visibleCandles.Count;

        // A series that was not computed stays empty; the matching Has* flag is false, so no
        // draw path ever reads it.
        double[] Slice(double[] values)
        {
            if (values.Length == 0)
            {
                return Array.Empty<double>();
            }

            var slice = new double[count];
            for (var i = 0; i < count; i++)
            {
                var gi = start + i;
                slice[i] = gi >= 0 && gi < values.Length ? values[gi] : double.NaN;
            }

            return slice;
        }

        return new SeriesData
        {
            Close = Slice(full.Close),
            Ma20 = Slice(full.Ma20),
            Ma50 = Slice(full.Ma50),
            BbUpper = Slice(full.BbUpper),
            BbLower = Slice(full.BbLower),
            BbMid = Slice(full.BbMid),
            HaOpen = Slice(full.HaOpen),
            HaHigh = Slice(full.HaHigh),
            HaLow = Slice(full.HaLow),
            HaClose = Slice(full.HaClose),
            Rsi = Slice(full.Rsi),
            HasMa20 = full.HasMa20,
            HasMa50 = full.HasMa50,
            HasBb = full.HasBb,
            HasHa = full.HasHa,
            HasRsi = full.HasRsi,
        };
    }

    /// <summary>
    /// Full-history indicator arrays, rebuilt only when the candle list, the forming candle's
    /// close or the set of enabled overlays changed. Panning, zooming and every crosshair move
    /// reuse the cached arrays and only re-slice the visible window; only the overlays actually
    /// switched on are computed at all.
    /// </summary>
    private FullSeries EnsureFullSeries()
    {
        var total = _allCandles.Count;
        var wantHa = string.Equals((ChartType ?? "Candles").Trim(), "HA", StringComparison.OrdinalIgnoreCase);
        var key = new SeriesKey(
            Candles,
            total,
            total > 0 ? _allCandles[total - 1].Close : 0m,
            ShowMa20,
            ShowMa50,
            ShowBollinger,
            ShowRsi,
            wantHa);

        if (_fullSeries is { } cached && _fullSeriesKey == key)
        {
            return cached;
        }

        var closes = new double[total];
        for (var i = 0; i < total; i++)
        {
            closes[i] = (double)_allCandles[i].Close;
        }

        var hasMa20 = ShowMa20 && total >= 20;
        var hasMa50 = ShowMa50 && total >= 50;
        var hasBb = ShowBollinger && total >= 20;
        var hasRsi = ShowRsi && total > 14;
        var hasHa = wantHa && total >= 1;

        double[] bbUpper, bbLower, bbMid;
        if (hasBb)
        {
            (bbUpper, bbLower, bbMid) = Bollinger(closes);
        }
        else
        {
            bbUpper = bbLower = bbMid = Array.Empty<double>();
        }

        double[] haOpen, haHigh, haLow, haClose;
        if (hasHa)
        {
            (haOpen, haHigh, haLow, haClose) = HeikinAshi(_allCandles);
        }
        else
        {
            haOpen = haHigh = haLow = haClose = Array.Empty<double>();
        }

        var series = new FullSeries
        {
            Close = closes,
            Ma20 = hasMa20 ? Sma(closes, 20) : Array.Empty<double>(),
            Ma50 = hasMa50 ? Sma(closes, 50) : Array.Empty<double>(),
            BbUpper = bbUpper,
            BbLower = bbLower,
            BbMid = bbMid,
            HaOpen = haOpen,
            HaHigh = haHigh,
            HaLow = haLow,
            HaClose = haClose,
            Rsi = hasRsi ? Rsi14(closes) : Array.Empty<double>(),
            HasMa20 = hasMa20,
            HasMa50 = hasMa50,
            HasBb = hasBb,
            HasHa = hasHa,
            HasRsi = hasRsi,
        };

        _fullSeries = series;
        _fullSeriesKey = key;
        return series;
    }

    private static double[] Sma(double[] closes, int period)
    {
        var result = new double[closes.Length];
        double sum = 0;
        for (var i = 0; i < closes.Length; i++)
        {
            sum += closes[i];
            if (i >= period) sum -= closes[i - period];
            result[i] = i >= period - 1 ? sum / period : double.NaN;
        }

        return result;
    }

    /// <summary>Bollinger(20, 2). Rolling sum and sum of squares — the previous form re-read
    /// the whole 20-candle window for every candle.</summary>
    private static (double[] Upper, double[] Lower, double[] Mid) Bollinger(double[] closes)
    {
        const int period = 20;
        var total = closes.Length;
        var upper = new double[total];
        var lower = new double[total];
        var mid = new double[total];
        double sum = 0, sumSquares = 0;
        for (var i = 0; i < total; i++)
        {
            sum += closes[i];
            sumSquares += closes[i] * closes[i];
            if (i >= period)
            {
                sum -= closes[i - period];
                sumSquares -= closes[i - period] * closes[i - period];
            }

            if (i < period - 1)
            {
                upper[i] = lower[i] = mid[i] = double.NaN;
                continue;
            }

            var mean = sum / period;
            // Clamped: rounding on the rolling sums can push a flat window fractionally negative.
            var std = Math.Sqrt(Math.Max(0d, (sumSquares / period) - (mean * mean)));
            mid[i] = mean;
            upper[i] = mean + (2d * std);
            lower[i] = mean - (2d * std);
        }

        return (upper, lower, mid);
    }

    /// <summary>RSI(14), Wilder smoothing.</summary>
    private static double[] Rsi14(double[] closes)
    {
        var total = closes.Length;
        var rsi = new double[total];
        for (var i = 0; i < total; i++) rsi[i] = double.NaN;
        if (total <= 14)
        {
            return rsi;
        }

        double avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= 14; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change >= 0) avgGain += change; else avgLoss -= change;
        }

        avgGain /= 14d;
        avgLoss /= 14d;
        rsi[14] = avgLoss < 1e-12 ? 100d : 100d - 100d / (1d + avgGain / avgLoss);
        for (var i = 15; i < total; i++)
        {
            var change = closes[i] - closes[i - 1];
            var gain = change >= 0 ? change : 0d;
            var loss = change < 0 ? -change : 0d;
            avgGain = (avgGain * 13d + gain) / 14d;
            avgLoss = (avgLoss * 13d + loss) / 14d;
            rsi[i] = avgLoss < 1e-12 ? 100d : 100d - 100d / (1d + avgGain / avgLoss);
        }

        return rsi;
    }

    private static (double[] Open, double[] High, double[] Low, double[] Close) HeikinAshi(IReadOnlyList<DexOhlcvPoint> candles)
    {
        var total = candles.Count;
        var haOpen = new double[total];
        var haHigh = new double[total];
        var haLow = new double[total];
        var haClose = new double[total];
        for (var i = 0; i < total; i++)
        {
            var c = candles[i];
            var close = (double)(c.Open + c.High + c.Low + c.Close) / 4d;
            var open = i == 0
                ? (double)(c.Open + c.Close) / 2d
                : (haOpen[i - 1] + haClose[i - 1]) / 2d;
            haClose[i] = close;
            haOpen[i] = open;
            haHigh[i] = Math.Max((double)c.High, Math.Max(open, close));
            haLow[i] = Math.Min((double)c.Low, Math.Min(open, close));
        }

        return (haOpen, haHigh, haLow, haClose);
    }

    /// <summary>Everything that makes the cached full-history series stale. The last close is
    /// part of it because the newest candle keeps mutating in place while it forms.</summary>
    private readonly record struct SeriesKey(
        object? Source,
        int Count,
        decimal LastClose,
        bool Ma20,
        bool Ma50,
        bool Bb,
        bool Rsi,
        bool Ha);

    private sealed class FullSeries
    {
        public required double[] Close { get; init; }
        public required double[] Ma20 { get; init; }
        public required double[] Ma50 { get; init; }
        public required double[] BbUpper { get; init; }
        public required double[] BbLower { get; init; }
        public required double[] BbMid { get; init; }
        public required double[] HaOpen { get; init; }
        public required double[] HaHigh { get; init; }
        public required double[] HaLow { get; init; }
        public required double[] HaClose { get; init; }
        public required double[] Rsi { get; init; }
        public required bool HasMa20 { get; init; }
        public required bool HasMa50 { get; init; }
        public required bool HasBb { get; init; }
        public required bool HasHa { get; init; }
        public required bool HasRsi { get; init; }
    }

    private sealed class SeriesData
    {
        public required double[] Close { get; init; }
        public required double[] Ma20 { get; init; }
        public required double[] Ma50 { get; init; }
        public required double[] BbUpper { get; init; }
        public required double[] BbLower { get; init; }
        public required double[] BbMid { get; init; }
        public required double[] HaOpen { get; init; }
        public required double[] HaHigh { get; init; }
        public required double[] HaLow { get; init; }
        public required double[] HaClose { get; init; }
        public required double[] Rsi { get; init; }
        public required bool HasMa20 { get; init; }
        public required bool HasMa50 { get; init; }
        public required bool HasBb { get; init; }
        public required bool HasHa { get; init; }
        public required bool HasRsi { get; init; }

        /// <summary>Grow the price domain to include any visible overlay extremes.</summary>
        public void ExpandRange(ref decimal min, ref decimal max)
        {
            if (!HasBb) return;

            var lo = min;
            var hi = max;
            foreach (var v in BbUpper)
            {
                if (double.IsNaN(v)) continue;
                var dv = (decimal)v;
                if (dv < lo) lo = dv;
                if (dv > hi) hi = dv;
            }

            foreach (var v in BbLower)
            {
                if (double.IsNaN(v)) continue;
                var dv = (decimal)v;
                if (dv < lo) lo = dv;
                if (dv > hi) hi = dv;
            }

            min = lo;
            max = hi;
        }
    }

    private void DrawPriceAxis(DrawingContext context)
    {
        var step = (_maxVisiblePrice - _minVisiblePrice) / 10m;
        if (step <= 0)
        {
            step = Math.Max(_maxVisiblePrice * 0.001m, 0.01m);
        }

        var priceFormat = GetPriceFormat(_maxVisiblePrice);
        for (var i = 0; i <= 10; i++)
        {
            var price = _maxVisiblePrice - (step * i);
            var y = Math.Clamp(MapY(price) - 8, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
            var text = new FormattedText(
                price.ToString(priceFormat, CultureInfo.GetCultureInfo("ru-RU")),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                AxisTextBrush);

            context.DrawText(text, new Point(_priceBounds.Left + 10, y));
        }
    }

    private void DrawTimeAxis(DrawingContext context)
    {
        var totalSpan = _visibleCandles[^1].Timestamp - _visibleCandles[0].Timestamp;
        var labelCount = Math.Min(Math.Max(4, (int)(_timeBounds.Width / 140d)), _visibleCandles.Count);
        for (var i = 0; i < labelCount; i++)
        {
            var visibleIndex = labelCount == 1 ? 0 : (int)Math.Round((_visibleCandles.Count - 1) * (i / (double)(labelCount - 1)));
            var candle = _visibleCandles[visibleIndex];
            var x = MapX(_visibleStartIndex + visibleIndex);
            var label = FormatTimeLabel(candle.Timestamp.ToLocalTime(), totalSpan);

            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                AxisTextBrush);

            var drawX = Math.Clamp(x - (text.Width / 2d), _timeBounds.Left + 4, _timeBounds.Right - text.Width - 4);
            context.DrawText(text, new Point(drawX, _timeBounds.Top + 8));
        }
    }

    private void DrawLastPriceMarker(DrawingContext context, decimal lastPrice, double lastPriceY)
    {
        context.DrawLine(LastPriceLinePen, new Point(_chartBounds.Left, lastPriceY), new Point(_chartBounds.Right, lastPriceY));

        var labelRect = new Rect(_priceBounds.Left, Math.Clamp(lastPriceY - 10, _priceBounds.Top + 2, _priceBounds.Bottom - 22), _priceBounds.Width, 20);
        context.DrawRectangle(LastPriceLabelBrush, null, labelRect);

        var label = new FormattedText(
            lastPrice.ToString(GetPriceFormat(lastPrice), CultureInfo.GetCultureInfo("ru-RU")),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            Brushes.White);

        context.DrawText(label, new Point(labelRect.Left + 8, labelRect.Top + 2));
    }

    private void DrawCrosshair(DrawingContext context)
    {
        if (!_pointerInsideChart || !_hasChartLayout || _visibleCandles.Count == 0)
        {
            return;
        }

        var clampedX = Math.Clamp(_pointerPosition.X, _chartBounds.Left, _chartBounds.Right);
        var clampedY = Math.Clamp(_pointerPosition.Y, _chartBounds.Top, _chartBounds.Bottom);
        var hoveredIndex = GetNearestGlobalIndex(clampedX);
        if (hoveredIndex < 0 || hoveredIndex >= _allCandles.Count)
        {
            return;
        }

        var hoveredCandle = _allCandles[hoveredIndex];
        var x = MapX(hoveredIndex);
        var priceAtPointer = MapPrice(clampedY);
        context.DrawLine(CrosshairPen, new Point(x, _chartBounds.Top), new Point(x, _chartBounds.Bottom));
        context.DrawLine(CrosshairPen, new Point(_chartBounds.Left, clampedY), new Point(_chartBounds.Right, clampedY));

        DrawPriceMarker(context, priceAtPointer, clampedY, MarkerBackgroundBrush, Brushes.White);
        DrawTimeMarker(context, hoveredCandle.Timestamp.ToLocalTime(), x);
        DrawHoveredCandleBox(context, hoveredCandle);
        DrawPendingPreview(context, clampedX, clampedY);
    }

    private void DrawPendingPreview(DrawingContext context, double clampedX, double clampedY)
    {
        if (_pendingTrendAnchor is not null && string.Equals(ToolMode, "Trend", StringComparison.OrdinalIgnoreCase))
        {
            context.DrawLine(TrendPreviewPen, new Point(MapX(_pendingTrendAnchor.Value.Index), MapY(_pendingTrendAnchor.Value.Price)), new Point(clampedX, clampedY));
        }

        if (_pendingRectangle is not null && string.Equals(ToolMode, "Rectangle", StringComparison.OrdinalIgnoreCase))
        {
            DrawRectangleShape(context, _pendingRectangle.Value.Start, new ChartAnchor(GetNearestGlobalIndex(clampedX), MapPrice(clampedY)), RectanglePreviewPen);
        }

        if (_pendingChannel is not null && string.Equals(ToolMode, "Channel", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingChannel.Value.SecondAnchor is null)
            {
                context.DrawLine(ChannelPreviewPen, new Point(MapX(_pendingChannel.Value.Start.Index), MapY(_pendingChannel.Value.Start.Price)), new Point(clampedX, clampedY));
            }
            else
            {
                DrawChannelShape(context, _pendingChannel.Value.Start, _pendingChannel.Value.SecondAnchor.Value, new ChartAnchor(GetNearestGlobalIndex(clampedX), MapPrice(clampedY)), ChannelPreviewPen);
            }
        }
    }

    private void DrawPriceMarker(DrawingContext context, decimal price, double y, IBrush background, IBrush foreground)
    {
        var labelRect = new Rect(_priceBounds.Left, Math.Clamp(y - 10, _priceBounds.Top + 2, _priceBounds.Bottom - 22), _priceBounds.Width, 20);
        context.DrawRectangle(background, null, labelRect);

        var label = new FormattedText(
            price.ToString(GetPriceFormat(price), CultureInfo.GetCultureInfo("ru-RU")),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            foreground);

        context.DrawText(label, new Point(labelRect.Left + 8, labelRect.Top + 2));
    }

    private void DrawTimeMarker(DrawingContext context, DateTime timestamp, double x)
    {
        var label = FormatTimeLabel(timestamp, _visibleCandles[^1].Timestamp - _visibleCandles[0].Timestamp);
        var formatted = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Brushes.White);

        var width = formatted.Width + 12;
        var drawX = Math.Clamp(x - (width / 2d), _timeBounds.Left + 2, _timeBounds.Right - width - 2);
        var rect = new Rect(drawX, _timeBounds.Top + 5, width, 20);
        context.DrawRectangle(MarkerBackgroundBrush, null, rect);
        context.DrawText(formatted, new Point(rect.Left + 6, rect.Top + 2));
    }

    private void DrawHoveredCandleBox(DrawingContext context, DexOhlcvPoint candle)
    {
        var info = $"O {candle.Open:N2}  H {candle.High:N2}  L {candle.Low:N2}  C {candle.Close:N2}";
        var text = new FormattedText(
            info,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Brushes.White);

        var rect = new Rect(_chartBounds.Left + 10, _chartBounds.Top + 10, text.Width + 16, 22);
        context.DrawRectangle(HoverBoxBrush, HoverBoxPen, rect);
        context.DrawText(text, new Point(rect.Left + 8, rect.Top + 3));
    }

    private void DrawDrawings(DrawingContext context)
    {
        foreach (var drawing in _drawings)
        {
            var pen = new Pen(new SolidColorBrush(drawing.Color), 2);
            switch (drawing.Type)
            {
                case ChartDrawingType.Horizontal:
                    var y = MapY(drawing.StartPrice);
                    context.DrawLine(pen, new Point(_chartBounds.Left, y), new Point(_chartBounds.Right, y));
                    break;
                case ChartDrawingType.Rectangle when drawing.EndIndex is not null && drawing.EndPrice is not null:
                    DrawRectangleShape(context, new ChartAnchor(drawing.StartIndex, drawing.StartPrice), new ChartAnchor(drawing.EndIndex.Value, drawing.EndPrice.Value), pen);
                    break;
                case ChartDrawingType.Channel when drawing.EndIndex is not null && drawing.EndPrice is not null && drawing.ThirdIndex is not null && drawing.ThirdPrice is not null:
                    DrawChannelShape(context,
                        new ChartAnchor(drawing.StartIndex, drawing.StartPrice),
                        new ChartAnchor(drawing.EndIndex.Value, drawing.EndPrice.Value),
                        new ChartAnchor(drawing.ThirdIndex.Value, drawing.ThirdPrice.Value),
                        pen);
                    break;
                default:
                    if (drawing.EndIndex is not null && drawing.EndPrice is not null)
                    {
                        context.DrawLine(pen,
                            new Point(MapX(drawing.StartIndex), MapY(drawing.StartPrice)),
                            new Point(MapX(drawing.EndIndex.Value), MapY(drawing.EndPrice.Value)));
                    }

                    break;
            }
        }
    }

    private void DrawWalls(DrawingContext context)
    {
        var walls = Walls;
        if (walls is null || walls.Count == 0)
        {
            return;
        }

        foreach (var wall in walls)
        {
            if (wall.Price < _minVisiblePrice || wall.Price > _maxVisiblePrice)
            {
                continue;
            }

            var y = MapY(wall.Price);
            var rgb = wall.IsBid ? WallBidColor : WallAskColor;
            var alpha = (byte)Math.Clamp(90d + (wall.Intensity * 130d), 0d, 255d);
            var lineColor = new Color(alpha, rgb.R, rgb.G, rgb.B);
            var pen = new Pen(new SolidColorBrush(lineColor), 1.5d + (wall.Intensity * 2.5d));
            context.DrawLine(pen, new Point(_chartBounds.Left, y), new Point(_chartBounds.Right, y));

            var label = new FormattedText(
                wall.Price.ToString(GetPriceFormat(wall.Price), CultureInfo.InvariantCulture) + "  " + FormatWallSize(wall.Quantity),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
                10,
                wall.IsBid ? WallBidLabelBrush : WallAskLabelBrush);
            context.DrawText(label, new Point(_chartBounds.Left + 6, y - 12));
        }
    }

    private static string FormatWallSize(decimal qty) =>
        qty >= 1000m
            ? (qty / 1000m).ToString("N1", CultureInfo.InvariantCulture) + "K"
            : qty.ToString("N2", CultureInfo.InvariantCulture);

    private void DrawRectangleShape(DrawingContext context, ChartAnchor start, ChartAnchor end, Pen pen)
    {
        var leftX = Math.Min(MapX(start.Index), MapX(end.Index));
        var rightX = Math.Max(MapX(start.Index), MapX(end.Index));
        var topY = Math.Min(MapY(start.Price), MapY(end.Price));
        var bottomY = Math.Max(MapY(start.Price), MapY(end.Price));
        context.DrawRectangle(null, pen, new Rect(new Point(leftX, topY), new Point(rightX, bottomY)));
    }

    private void DrawChannelShape(DrawingContext context, ChartAnchor start, ChartAnchor end, ChartAnchor offsetAnchor, Pen pen)
    {
        var x1 = MapX(start.Index);
        var y1 = MapY(start.Price);
        var x2 = MapX(end.Index);
        var y2 = MapY(end.Price);
        var x3 = MapX(offsetAnchor.Index);
        var y3 = MapY(offsetAnchor.Price);

        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.0001d)
        {
            return;
        }

        var normalX = -dy / length;
        var normalY = dx / length;
        var offsetDistance = ((x3 - x1) * normalX) + ((y3 - y1) * normalY);
        var offsetX = normalX * offsetDistance;
        var offsetY = normalY * offsetDistance;

        context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        context.DrawLine(pen, new Point(x1 + offsetX, y1 + offsetY), new Point(x2 + offsetX, y2 + offsetY));
        context.DrawLine(pen, new Point(x1, y1), new Point(x1 + offsetX, y1 + offsetY));
        context.DrawLine(pen, new Point(x2, y2), new Point(x2 + offsetX, y2 + offsetY));
    }

    // ── VWAP ────────────────────────────────────────────────────────────────────

    private void DrawVwap(DrawingContext context)
    {
        if (_visibleCandles.Count < 3) return;

        var candles = _visibleCandles;
        int n = candles.Count;

        // Per-candle VWAP & sigma arrays
        var vwaps  = new double[n];
        var sigma1 = new double[n];
        var sigma2 = new double[n];

        double cumTPV  = 0d;   // Σ TP × Vol
        double cumVol  = 0d;   // Σ Vol
        double cumTP2V = 0d;   // Σ TP² × Vol

        for (int i = 0; i < n; i++)
        {
            var c  = candles[i];
            var tp = (double)(c.High + c.Low + c.Close) / 3d;
            var v  = (double)c.Volume;

            cumTPV  += tp * v;
            cumVol  += v;
            cumTP2V += tp * tp * v;

            if (cumVol < 1e-12d)
            {
                vwaps[i]  = tp;
                sigma1[i] = 0d;
                sigma2[i] = 0d;
                continue;
            }

            var w  = cumTPV / cumVol;
            var s2 = Math.Max(0d, cumTP2V / cumVol - w * w);
            var s  = Math.Sqrt(s2);

            vwaps[i]  = w;
            sigma1[i] = s;
            sigma2[i] = s * 2d;
        }


        // Build StreamGeometries for each series
        void DrawSeries(double[] values, Pen pen)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < n; i++)
                {
                    var p = new Point(MapX(_visibleStartIndex + i), MapY((decimal)values[i]));
                    if (!started) { gc.BeginFigure(p, false); started = true; }
                    else gc.LineTo(p);
                }
                gc.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geo);
        }

        // Upper / lower band helpers
        double[] Upper1 = new double[n], Lower1 = new double[n], Upper2 = new double[n], Lower2 = new double[n];
        for (int i = 0; i < n; i++)
        {
            Upper1[i] = vwaps[i] + sigma1[i];
            Lower1[i] = vwaps[i] - sigma1[i];
            Upper2[i] = vwaps[i] + sigma2[i];
            Lower2[i] = vwaps[i] - sigma2[i];
        }

        DrawSeries(Upper2, VwapSigma2Pen);
        DrawSeries(Lower2, VwapSigma2Pen);
        DrawSeries(Upper1, VwapSigma1Pen);
        DrawSeries(Lower1, VwapSigma1Pen);
        DrawSeries(vwaps,  VwapPen);

        // Label on right edge
        var lastVwap = (decimal)vwaps[n - 1];
        var labelY   = Math.Clamp(MapY(lastVwap), _chartBounds.Top + 2, _chartBounds.Bottom - 18);
        var vwapLabel = new FormattedText(
            $"V {lastVwap.ToString(GetPriceFormat(lastVwap), CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
            11,
            VwapLabelBrush);
        context.DrawText(vwapLabel, new Point(_priceBounds.Left + 4, labelY));
    }

    // ── Volume Profile ───────────────────────────────────────────────────────────

    private void DrawVolumeProfile(DrawingContext context)
    {
        if (_visibleCandles.Count < 2) return;

        const int BucketCount   = 48;
        const double ProfilePct = 0.14d;   // fraction of chart width used by bars

        var priceRange = _maxVisiblePrice - _minVisiblePrice;
        if (priceRange <= 0m) return;

        var bucketSize  = priceRange / BucketCount;
        var volumes     = new double[BucketCount];
        var profileWidth = Math.Min(90d, _chartBounds.Width * ProfilePct);

        // Distribute each candle's volume across buckets it spans
        foreach (var c in _visibleCandles)
        {
            if (c.Volume <= 0m || c.High <= c.Low) continue;

            var candleRange = (double)(c.High - c.Low);

            for (int b = 0; b < BucketCount; b++)
            {
                var bucketLow  = _minVisiblePrice + bucketSize * b;
                var bucketHigh = bucketLow + bucketSize;

                var overlapLow  = Math.Max(bucketLow,  c.Low);
                var overlapHigh = Math.Min(bucketHigh, c.High);
                if (overlapHigh <= overlapLow) continue;

                var fraction = (double)((overlapHigh - overlapLow) / (decimal)candleRange);
                volumes[b] += (double)c.Volume * fraction;
            }
        }

        var maxVol  = volumes.Max();
        if (maxVol <= 0d) return;

        // Find POC bucket
        int pocIdx = Array.IndexOf(volumes, maxVol);

        // Value area (68% of total volume around POC)
        var totalVol   = volumes.Sum();
        var valueVol   = totalVol * 0.68d;
        double accum   = volumes[pocIdx];
        int vaLow = pocIdx, vaHigh = pocIdx;
        while (accum < valueVol && (vaLow > 0 || vaHigh < BucketCount - 1))
        {
            var addLow  = vaLow  > 0              ? volumes[vaLow  - 1] : 0d;
            var addHigh = vaHigh < BucketCount - 1 ? volumes[vaHigh + 1] : 0d;
            if (addLow >= addHigh && vaLow > 0)       { vaLow--;  accum += addLow; }
            else if (vaHigh < BucketCount - 1)         { vaHigh++; accum += addHigh; }
            else                                        break;
        }

        double barX = _chartBounds.Right - profileWidth;

        for (int b = 0; b < BucketCount; b++)
        {
            if (volumes[b] <= 0d) continue;

            var barWidth = volumes[b] / maxVol * profileWidth;
            var yTop     = MapY(_minVisiblePrice + bucketSize * (b + 1));
            var yBottom  = MapY(_minVisiblePrice + bucketSize * b);
            var barH     = Math.Max(1d, yBottom - yTop);

            IBrush brush = b == pocIdx       ? VpPocBrush
                         : b >= vaLow && b <= vaHigh ? VpValueBrush
                         : VpNormalBrush;

            context.DrawRectangle(brush, null, new Rect(barX + profileWidth - barWidth, yTop, barWidth, barH));
        }

        // POC horizontal line (full width)
        var pocPrice    = _minVisiblePrice + bucketSize * pocIdx + bucketSize / 2m;
        var pocY        = MapY(pocPrice);
        context.DrawLine(PocPen, new Point(_chartBounds.Left, pocY), new Point(_chartBounds.Right, pocY));

        // POC label on price axis
        var pocLabel = new FormattedText(
            $"POC {pocPrice.ToString(GetPriceFormat(pocPrice), CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10,
            PocLabelBrush);
        var pocLabelY = Math.Clamp(pocY - 9, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
        context.DrawText(pocLabel, new Point(_priceBounds.Left + 4, pocLabelY));

        // Value Area High / Low labels
        var vahPrice = _minVisiblePrice + bucketSize * vaHigh + bucketSize;
        var valPrice = _minVisiblePrice + bucketSize * vaLow;
        var vahY     = Math.Clamp(MapY(vahPrice) - 9, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
        var valY     = Math.Clamp(MapY(valPrice)  - 9, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
        var vahText = new FormattedText($"VAH", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, VpAxisLabelBrush);
        var valText = new FormattedText($"VAL", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, VpAxisLabelBrush);

        context.DrawLine(VahLinePen, new Point(_chartBounds.Left, MapY(vahPrice)), new Point(_chartBounds.Right, MapY(vahPrice)));
        context.DrawLine(VahLinePen, new Point(_chartBounds.Left, MapY(valPrice)), new Point(_chartBounds.Right, MapY(valPrice)));
        context.DrawText(vahText, new Point(_priceBounds.Left + 4, vahY));
        context.DrawText(valText, new Point(_priceBounds.Left + 4, valY));
    }

    private static void DrawEmptyState(DrawingContext context, Rect chartBounds)
    {
        var title = new FormattedText(
            "Loading Binance candles...",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            16,
            BullBrush);

        context.DrawText(title, new Point(chartBounds.Left + 20, chartBounds.Top + 20));
    }

    private double MapY(decimal price)
    {
        var priceRange = Math.Max((double)(_maxVisiblePrice - _minVisiblePrice), 0.00000001d);
        var normalized = (double)(price - _minVisiblePrice) / priceRange;
        return _chartBounds.Bottom - (normalized * _chartBounds.Height);
    }

    private decimal MapPrice(double y)
    {
        var clampedY = Math.Clamp(y, _chartBounds.Top, _chartBounds.Bottom);
        var normalized = (_chartBounds.Bottom - clampedY) / Math.Max(1d, _chartBounds.Height);
        return _minVisiblePrice + ((decimal)normalized * (_maxVisiblePrice - _minVisiblePrice));
    }

    private double MapX(double globalIndex)
    {
        var visibleOffset = globalIndex - _visibleStartIndex;
        var slotWidth = _chartBounds.Width / Math.Max(1, _visibleCount);
        return _chartBounds.Left + (visibleOffset * slotWidth) + (slotWidth / 2d);
    }

    private int GetNearestGlobalIndex(double x)
    {
        if (_visibleCount <= 0)
        {
            return -1;
        }

        var slotWidth = _chartBounds.Width / Math.Max(1, _visibleCount);
        var relative = Math.Clamp(x - _chartBounds.Left, 0, Math.Max(0, _chartBounds.Width - 1));
        var visibleIndex = (int)Math.Round(relative / Math.Max(1d, slotWidth) - 0.5d);
        visibleIndex = Math.Clamp(visibleIndex, 0, _visibleCount - 1);
        return _visibleStartIndex + visibleIndex;
    }

    private bool TryCreateAnchor(Point position, out ChartAnchor anchor)
    {
        anchor = default;
        if (!_chartBounds.Contains(position) || _visibleCandles.Count == 0)
        {
            return false;
        }

        anchor = new ChartAnchor(GetNearestGlobalIndex(position.X), MapPrice(position.Y));
        return true;
    }

    private void OnCandlesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && _allCandles.Count > 0)
        {
            CaptureViewStateForRestore();
        }

        RefreshAllCandles();
        if (_allCandles.Count == 0 && e.Action == NotifyCollectionChangedAction.Reset)
        {
            InvalidateVisual();
            return;
        }

        if (_hasPendingViewRestore && _allCandles.Count > 0)
        {
            RestorePendingViewState();
        }
        else
        {
            EnsureViewWindow();
        }

        InvalidateVisual();
    }

    private void CaptureViewStateForRestore()
    {
        EnsureViewWindow();
        _pendingVisibleCount = _visibleCount;
        _pendingRightOffset = Math.Max(0, _allCandles.Count - (_visibleStartIndex + _visibleCount));
        _hasPendingViewRestore = _pendingVisibleCount > 0;
    }

    private void RestorePendingViewState()
    {
        if (!_hasPendingViewRestore)
        {
            EnsureViewWindow();
            return;
        }

        var total = _allCandles.Count;
        if (total <= 0)
        {
            return;
        }

        _visibleCount = Math.Min(total, Math.Max(MinimumVisibleCandles, _pendingVisibleCount));
        _visibleStartIndex = ClampVisibleStart(total - _visibleCount - _pendingRightOffset, _visibleCount);
        if (total >= Math.Max(MinimumVisibleCandles, _pendingVisibleCount))
        {
            _hasPendingViewRestore = false;
        }
    }

    private void CancelPendingToolState()
    {
        _pendingTrendAnchor = null;
        _pendingRectangle = null;
        _pendingChannel = null;
    }

    private bool TryRemoveDrawing(Point position)
    {
        const double tolerance = 10d;
        for (var index = _drawings.Count - 1; index >= 0; index--)
        {
            if (IsPointNearDrawing(position, _drawings[index], tolerance))
            {
                _drawings.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    private bool IsPointNearDrawing(Point point, ChartDrawing drawing, double tolerance)
    {
        return drawing.Type switch
        {
            ChartDrawingType.Horizontal => Math.Abs(point.Y - MapY(drawing.StartPrice)) <= tolerance,
            ChartDrawingType.Rectangle when drawing.EndIndex is not null && drawing.EndPrice is not null =>
                IsPointNearRectangle(point, new ChartAnchor(drawing.StartIndex, drawing.StartPrice), new ChartAnchor(drawing.EndIndex.Value, drawing.EndPrice.Value), tolerance),
            ChartDrawingType.Channel when drawing.EndIndex is not null && drawing.EndPrice is not null && drawing.ThirdIndex is not null && drawing.ThirdPrice is not null =>
                IsPointNearChannel(point, new ChartAnchor(drawing.StartIndex, drawing.StartPrice), new ChartAnchor(drawing.EndIndex.Value, drawing.EndPrice.Value), new ChartAnchor(drawing.ThirdIndex.Value, drawing.ThirdPrice.Value), tolerance),
            _ when drawing.EndIndex is not null && drawing.EndPrice is not null =>
                DistanceToSegment(point, new Point(MapX(drawing.StartIndex), MapY(drawing.StartPrice)), new Point(MapX(drawing.EndIndex.Value), MapY(drawing.EndPrice.Value))) <= tolerance,
            _ => false
        };
    }

    private bool IsPointNearRectangle(Point point, ChartAnchor start, ChartAnchor end, double tolerance)
    {
        var leftX = Math.Min(MapX(start.Index), MapX(end.Index));
        var rightX = Math.Max(MapX(start.Index), MapX(end.Index));
        var topY = Math.Min(MapY(start.Price), MapY(end.Price));
        var bottomY = Math.Max(MapY(start.Price), MapY(end.Price));

        return DistanceToSegment(point, new Point(leftX, topY), new Point(rightX, topY)) <= tolerance ||
               DistanceToSegment(point, new Point(rightX, topY), new Point(rightX, bottomY)) <= tolerance ||
               DistanceToSegment(point, new Point(rightX, bottomY), new Point(leftX, bottomY)) <= tolerance ||
               DistanceToSegment(point, new Point(leftX, bottomY), new Point(leftX, topY)) <= tolerance;
    }

    private bool IsPointNearChannel(Point point, ChartAnchor start, ChartAnchor end, ChartAnchor offsetAnchor, double tolerance)
    {
        var x1 = MapX(start.Index);
        var y1 = MapY(start.Price);
        var x2 = MapX(end.Index);
        var y2 = MapY(end.Price);
        var x3 = MapX(offsetAnchor.Index);
        var y3 = MapY(offsetAnchor.Price);
        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.0001d)
        {
            return false;
        }

        var normalX = -dy / length;
        var normalY = dx / length;
        var offsetDistance = ((x3 - x1) * normalX) + ((y3 - y1) * normalY);
        var offsetX = normalX * offsetDistance;
        var offsetY = normalY * offsetDistance;

        return DistanceToSegment(point, new Point(x1, y1), new Point(x2, y2)) <= tolerance ||
               DistanceToSegment(point, new Point(x1 + offsetX, y1 + offsetY), new Point(x2 + offsetX, y2 + offsetY)) <= tolerance ||
               DistanceToSegment(point, new Point(x1, y1), new Point(x1 + offsetX, y1 + offsetY)) <= tolerance ||
               DistanceToSegment(point, new Point(x2, y2), new Point(x2 + offsetX, y2 + offsetY)) <= tolerance;
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 0.0001d && Math.Abs(dy) < 0.0001d)
        {
            return Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2));
        }

        var t = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / ((dx * dx) + (dy * dy));
        t = Math.Clamp(t, 0d, 1d);
        var projection = new Point(start.X + (t * dx), start.Y + (t * dy));
        return Math.Sqrt(Math.Pow(point.X - projection.X, 2) + Math.Pow(point.Y - projection.Y, 2));
    }

    private void LoadPersistedDrawings()
    {
        SaveCurrentDrawings();
        _drawings.Clear();
        CancelPendingToolState();
        _loadedPersistenceKey = PersistenceKey;

        if (string.IsNullOrWhiteSpace(_loadedPersistenceKey))
        {
            return;
        }

        if (PersistedDrawings.TryGetValue(_loadedPersistenceKey, out var drawings))
        {
            _drawings.AddRange(drawings.Select(item => item.Clone()));
        }
    }

    private void PersistDrawings()
    {
        if (string.IsNullOrWhiteSpace(PersistenceKey))
        {
            return;
        }

        PersistedDrawings[PersistenceKey] = _drawings.Select(item => item.Clone()).ToList();
    }

    private void SaveCurrentDrawings()
    {
        if (string.IsNullOrWhiteSpace(_loadedPersistenceKey))
        {
            return;
        }

        PersistedDrawings[_loadedPersistenceKey] = _drawings.Select(item => item.Clone()).ToList();
    }

    private static string GetPriceFormat(decimal price) =>
        price switch
        {
            >= 1000m => "N2",
            >= 1m => "N2",
            >= 0.01m => "N4",
            _ => "N6"
        };

    private static string FormatTimeLabel(DateTime timestamp, TimeSpan totalSpan)
    {
        if (totalSpan.TotalDays >= 365)
        {
            return timestamp.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        }

        if (totalSpan.TotalDays >= 30)
        {
            return timestamp.ToString("dd MMM", CultureInfo.InvariantCulture);
        }

        if (totalSpan.TotalDays >= 2)
        {
            return timestamp.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        }

        return totalSpan.TotalHours >= 2
            ? timestamp.ToString("HH:mm", CultureInfo.InvariantCulture)
            : timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private readonly record struct ChartAnchor(int Index, decimal Price);
    private readonly record struct RectangleDraft(ChartAnchor Start);
    private readonly record struct ChannelDraft(ChartAnchor Start, ChartAnchor? SecondAnchor);

    private sealed class ChartDrawing
    {
        public required ChartDrawingType Type { get; init; }
        public required int StartIndex { get; init; }
        public required decimal StartPrice { get; init; }
        public int? EndIndex { get; init; }
        public decimal? EndPrice { get; init; }
        public int? ThirdIndex { get; init; }
        public decimal? ThirdPrice { get; init; }
        public required Color Color { get; init; }

        public ChartDrawing Clone() => new()
        {
            Type = Type,
            StartIndex = StartIndex,
            StartPrice = StartPrice,
            EndIndex = EndIndex,
            EndPrice = EndPrice,
            ThirdIndex = ThirdIndex,
            ThirdPrice = ThirdPrice,
            Color = Color
        };

        public static ChartDrawing CreateTrend(ChartAnchor start, ChartAnchor end) => new()
        {
            Type = ChartDrawingType.Trend,
            StartIndex = start.Index,
            StartPrice = start.Price,
            EndIndex = end.Index,
            EndPrice = end.Price,
            Color = Color.Parse("#E7C65C")
        };

        public static ChartDrawing CreateHorizontal(decimal price) => new()
        {
            Type = ChartDrawingType.Horizontal,
            StartIndex = 0,
            StartPrice = price,
            Color = Color.Parse("#9D7CFF")
        };

        public static ChartDrawing CreateRectangle(ChartAnchor start, ChartAnchor end) => new()
        {
            Type = ChartDrawingType.Rectangle,
            StartIndex = start.Index,
            StartPrice = start.Price,
            EndIndex = end.Index,
            EndPrice = end.Price,
            Color = PositiveColor
        };

        public static ChartDrawing CreateChannel(ChartAnchor start, ChartAnchor end, ChartAnchor offset) => new()
        {
            Type = ChartDrawingType.Channel,
            StartIndex = start.Index,
            StartPrice = start.Price,
            EndIndex = end.Index,
            EndPrice = end.Price,
            ThirdIndex = offset.Index,
            ThirdPrice = offset.Price,
            Color = Color.Parse("#F59E0B")
        };
    }

    private enum ChartDrawingType
    {
        Trend,
        Horizontal,
        Rectangle,
        Channel
    }
}
