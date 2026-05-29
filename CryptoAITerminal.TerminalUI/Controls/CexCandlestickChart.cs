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

    private const int MinimumVisibleCandles = 20;
    private const int DefaultRecentVisibleCandles = 180;
    private static readonly Dictionary<string, List<ChartDrawing>> PersistedDrawings = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ChartDrawing> _drawings = [];
    private INotifyCollectionChanged? _subscribedCollection;
    private IReadOnlyList<DexOhlcvPoint> _allCandles = Array.Empty<DexOhlcvPoint>();
    private IReadOnlyList<DexOhlcvPoint> _visibleCandles = Array.Empty<DexOhlcvPoint>();
    private Rect _chartBounds;
    private Rect _priceBounds;
    private Rect _timeBounds;
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
        AffectsRender<CexCandlestickChart>(CandlesProperty, ToolModeProperty, ClearDrawingsVersionProperty, ResetViewVersionProperty, PersistenceKeyProperty, ShowVwapProperty, ShowVolumeProfileProperty);
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
        _chartBounds = new Rect(bounds.Left, bounds.Top, Math.Max(0, bounds.Width - priceAxisWidth), Math.Max(0, bounds.Height - timeAxisHeight));
        _priceBounds = new Rect(_chartBounds.Right, _chartBounds.Top, priceAxisWidth, _chartBounds.Height);
        _timeBounds = new Rect(_chartBounds.Left, _chartBounds.Bottom, _chartBounds.Width, timeAxisHeight);
        _hasChartLayout = _chartBounds.Width > 0 && _chartBounds.Height > 0;

        DrawBackdrop(context, bounds, _chartBounds, _priceBounds, _timeBounds);
        DrawGrid(context, _chartBounds);

        RefreshAllCandles();
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

        _minVisiblePrice = _visibleCandles.Min(candle => candle.Low);
        _maxVisiblePrice = _visibleCandles.Max(candle => candle.High);
        var padding = (_maxVisiblePrice - _minVisiblePrice) * 0.06m;
        if (padding <= 0)
        {
            padding = Math.Max(_maxVisiblePrice * 0.002m, 0.00000001m);
        }

        _minVisiblePrice -= padding;
        _maxVisiblePrice += padding;

        DrawPriceAxis(context);
        DrawTimeAxis(context);
        DrawCandles(context);
        if (ShowVolumeProfile) DrawVolumeProfile(context);
        if (ShowVwap) DrawVwap(context);
        DrawDrawings(context);
        DrawLastPriceMarker(context, _visibleCandles[^1].Close, MapY(_visibleCandles[^1].Close));
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
        _pointerPosition = position;
        _pointerInsideChart = _chartBounds.Contains(position);

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
                }
            }
        }

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

    private void SubscribeToCandles()
    {
        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnCandlesCollectionChanged;
            _subscribedCollection = null;
        }

        if (Candles is INotifyCollectionChanged notifyCollectionChanged)
        {
            _subscribedCollection = notifyCollectionChanged;
            _subscribedCollection.CollectionChanged += OnCandlesCollectionChanged;
        }
    }

    private void RefreshAllCandles()
    {
        _allCandles = Candles?
            .Where(candle => candle.High > 0 && candle.Low > 0 && candle.Open > 0 && candle.Close > 0)
            .OrderBy(candle => candle.Timestamp)
            .ToList() ?? (IReadOnlyList<DexOhlcvPoint>)Array.Empty<DexOhlcvPoint>();
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
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#091018")), null, bounds);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#0B131C")), new Pen(new SolidColorBrush(Color.Parse("#223243")), 1), chartBounds);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#0E1721")), new Pen(new SolidColorBrush(Color.Parse("#2A4053")), 1), priceBounds);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#0C141D")), new Pen(new SolidColorBrush(Color.Parse("#223243")), 1), timeBounds);
    }

    private void DrawGrid(DrawingContext context, Rect chartBounds)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#182533")), 1);
        for (var row = 1; row < 10; row++)
        {
            var y = chartBounds.Top + (chartBounds.Height / 10d * row);
            context.DrawLine(gridPen, new Point(chartBounds.Left, y), new Point(chartBounds.Right, y));
        }

        for (var column = 1; column < 10; column++)
        {
            var x = chartBounds.Left + (chartBounds.Width / 10d * column);
            context.DrawLine(gridPen, new Point(x, chartBounds.Top), new Point(x, chartBounds.Bottom));
        }
    }

    private void DrawCandles(DrawingContext context)
    {
        var slotWidth = _chartBounds.Width / Math.Max(1, _visibleCandles.Count);
        var candleBodyWidth = Math.Clamp(slotWidth * 0.72d, 3d, 24d);

        for (var visibleIndex = 0; visibleIndex < _visibleCandles.Count; visibleIndex++)
        {
            var candle = _visibleCandles[visibleIndex];
            var globalIndex = _visibleStartIndex + visibleIndex;
            var centerX = MapX(globalIndex);
            var openY = MapY(candle.Open);
            var closeY = MapY(candle.Close);
            var highY = MapY(candle.High);
            var lowY = MapY(candle.Low);
            var bullish = candle.Close >= candle.Open;
            var color = bullish ? Color.Parse("#21E6C1") : Color.Parse("#FF6B6B");
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, 1);

            context.DrawLine(pen, new Point(centerX, highY), new Point(centerX, lowY));

            var bodyTop = Math.Min(openY, closeY);
            var bodyBottom = Math.Max(openY, closeY);
            var bodyHeight = Math.Max(2d, bodyBottom - bodyTop);
            var bodyRect = new Rect(centerX - (candleBodyWidth / 2d), bodyTop, candleBodyWidth, bodyHeight);
            context.DrawRectangle(brush, pen, bodyRect);
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
                new SolidColorBrush(Color.Parse("#DCE8F5")));

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
                new SolidColorBrush(Color.Parse("#DCE8F5")));

            var drawX = Math.Clamp(x - (text.Width / 2d), _timeBounds.Left + 4, _timeBounds.Right - text.Width - 4);
            context.DrawText(text, new Point(drawX, _timeBounds.Top + 8));
        }
    }

    private void DrawLastPriceMarker(DrawingContext context, decimal lastPrice, double lastPriceY)
    {
        var linePen = new Pen(new SolidColorBrush(Color.Parse("#365A66")), 1);
        context.DrawLine(linePen, new Point(_chartBounds.Left, lastPriceY), new Point(_chartBounds.Right, lastPriceY));

        var labelRect = new Rect(_priceBounds.Left, Math.Clamp(lastPriceY - 10, _priceBounds.Top + 2, _priceBounds.Bottom - 22), _priceBounds.Width, 20);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#19C9AF")), null, labelRect);

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
        var crosshairPen = new Pen(new SolidColorBrush(Color.Parse("#4E677E")), 1);
        context.DrawLine(crosshairPen, new Point(x, _chartBounds.Top), new Point(x, _chartBounds.Bottom));
        context.DrawLine(crosshairPen, new Point(_chartBounds.Left, clampedY), new Point(_chartBounds.Right, clampedY));

        DrawPriceMarker(context, priceAtPointer, clampedY, Color.Parse("#16212C"), Brushes.White);
        DrawTimeMarker(context, hoveredCandle.Timestamp.ToLocalTime(), x);
        DrawHoveredCandleBox(context, hoveredCandle);
        DrawPendingPreview(context, clampedX, clampedY);
    }

    private void DrawPendingPreview(DrawingContext context, double clampedX, double clampedY)
    {
        if (_pendingTrendAnchor is not null && string.Equals(ToolMode, "Trend", StringComparison.OrdinalIgnoreCase))
        {
            var previewPen = new Pen(new SolidColorBrush(Color.Parse("#E7C65C")), 2);
            context.DrawLine(previewPen, new Point(MapX(_pendingTrendAnchor.Value.Index), MapY(_pendingTrendAnchor.Value.Price)), new Point(clampedX, clampedY));
        }

        if (_pendingRectangle is not null && string.Equals(ToolMode, "Rectangle", StringComparison.OrdinalIgnoreCase))
        {
            DrawRectangleShape(context, _pendingRectangle.Value.Start, new ChartAnchor(GetNearestGlobalIndex(clampedX), MapPrice(clampedY)), new Pen(new SolidColorBrush(Color.Parse("#3DDC84")), 2));
        }

        if (_pendingChannel is not null && string.Equals(ToolMode, "Channel", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingChannel.Value.SecondAnchor is null)
            {
                var previewPen = new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), 2);
                context.DrawLine(previewPen, new Point(MapX(_pendingChannel.Value.Start.Index), MapY(_pendingChannel.Value.Start.Price)), new Point(clampedX, clampedY));
            }
            else
            {
                DrawChannelShape(context, _pendingChannel.Value.Start, _pendingChannel.Value.SecondAnchor.Value, new ChartAnchor(GetNearestGlobalIndex(clampedX), MapPrice(clampedY)), new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), 2));
            }
        }
    }

    private void DrawPriceMarker(DrawingContext context, decimal price, double y, Color backgroundColor, IBrush foreground)
    {
        var labelRect = new Rect(_priceBounds.Left, Math.Clamp(y - 10, _priceBounds.Top + 2, _priceBounds.Bottom - 22), _priceBounds.Width, 20);
        context.DrawRectangle(new SolidColorBrush(backgroundColor), null, labelRect);

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
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#16212C")), null, rect);
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
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#121B25")), new Pen(new SolidColorBrush(Color.Parse("#274055")), 1), rect);
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

        var vwapPen   = new Pen(new SolidColorBrush(Color.Parse("#FBBF24")), 1.6);
        var s1Pen     = new Pen(new SolidColorBrush(Color.Parse("#60A5FA")), 1.0) { DashStyle = new DashStyle([5, 3], 0) };
        var s2Pen     = new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 0.8) { DashStyle = new DashStyle([3, 4], 0) };

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

        DrawSeries(Upper2, s2Pen);
        DrawSeries(Lower2, s2Pen);
        DrawSeries(Upper1, s1Pen);
        DrawSeries(Lower1, s1Pen);
        DrawSeries(vwaps,  vwapPen);

        // Label on right edge
        var lastVwap = (decimal)vwaps[n - 1];
        var labelY   = Math.Clamp(MapY(lastVwap), _chartBounds.Top + 2, _chartBounds.Bottom - 18);
        var vwapLabel = new FormattedText(
            $"V {lastVwap.ToString(GetPriceFormat(lastVwap), CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
            11,
            new SolidColorBrush(Color.Parse("#FBBF24")));
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

        var normalBrush = new SolidColorBrush(Color.Parse("#1A3A5C"));
        var pocBrush    = new SolidColorBrush(Color.Parse("#7C3AED"));
        var valueBrush  = new SolidColorBrush(Color.Parse("#1A4A6C"));

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

            IBrush brush = b == pocIdx       ? pocBrush
                         : b >= vaLow && b <= vaHigh ? valueBrush
                         : normalBrush;

            context.DrawRectangle(brush, null, new Rect(barX + profileWidth - barWidth, yTop, barWidth, barH));
        }

        // POC horizontal line (full width)
        var pocPrice    = _minVisiblePrice + bucketSize * pocIdx + bucketSize / 2m;
        var pocY        = MapY(pocPrice);
        var pocPen      = new Pen(new SolidColorBrush(Color.Parse("#A855F7")), 1.0) { DashStyle = new DashStyle([6, 3], 0) };
        context.DrawLine(pocPen, new Point(_chartBounds.Left, pocY), new Point(_chartBounds.Right, pocY));

        // POC label on price axis
        var pocLabel = new FormattedText(
            $"POC {pocPrice.ToString(GetPriceFormat(pocPrice), CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10,
            new SolidColorBrush(Color.Parse("#A855F7")));
        var pocLabelY = Math.Clamp(pocY - 9, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
        context.DrawText(pocLabel, new Point(_priceBounds.Left + 4, pocLabelY));

        // Value Area High / Low labels
        var vahPrice = _minVisiblePrice + bucketSize * vaHigh + bucketSize;
        var valPrice = _minVisiblePrice + bucketSize * vaLow;
        var vahY     = Math.Clamp(MapY(vahPrice) - 9, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
        var valY     = Math.Clamp(MapY(valPrice)  - 9, _priceBounds.Top + 2, _priceBounds.Bottom - 18);
        var vpLabelBrush = new SolidColorBrush(Color.Parse("#60A5FA"));

        var vahText = new FormattedText($"VAH", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, vpLabelBrush);
        var valText = new FormattedText($"VAL", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, vpLabelBrush);

        var vahLinePen = new Pen(new SolidColorBrush(Color.Parse("#3060A0")), 0.7) { DashStyle = new DashStyle([4, 4], 0) };
        context.DrawLine(vahLinePen, new Point(_chartBounds.Left, MapY(vahPrice)), new Point(_chartBounds.Right, MapY(vahPrice)));
        context.DrawLine(vahLinePen, new Point(_chartBounds.Left, MapY(valPrice)), new Point(_chartBounds.Right, MapY(valPrice)));
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
            new SolidColorBrush(Color.Parse("#21E6C1")));

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
            Color = Color.Parse("#3DDC84")
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
