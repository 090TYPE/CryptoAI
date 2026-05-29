using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Controls;

public class DexPriceChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DexOhlcvPoint>?> CandlesProperty =
        AvaloniaProperty.Register<DexPriceChart, IReadOnlyList<DexOhlcvPoint>?>(nameof(Candles));

    public static readonly StyledProperty<bool> ShowVwapProperty =
        AvaloniaProperty.Register<DexPriceChart, bool>(nameof(ShowVwap), defaultValue: true);

    private INotifyCollectionChanged? _subscribedCollection;

    static DexPriceChart()
    {
        AffectsRender<DexPriceChart>(CandlesProperty, ShowVwapProperty);
    }

    public bool ShowVwap
    {
        get => GetValue(ShowVwapProperty);
        set => SetValue(ShowVwapProperty, value);
    }

    public IReadOnlyList<DexOhlcvPoint>? Candles
    {
        get => GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CandlesProperty)
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

            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var chartBounds = new Rect(12, 12, Math.Max(0, Bounds.Width - 24), Math.Max(0, Bounds.Height - 24));
        if (chartBounds.Width <= 0 || chartBounds.Height <= 0)
        {
            return;
        }

        DrawBackdrop(context, chartBounds);
        DrawGrid(context, chartBounds);

        var candles = Candles?
            .Where(candle => candle.High > 0 && candle.Low > 0 && candle.Open > 0 && candle.Close > 0)
            .ToList();

        if (candles is null || candles.Count == 0)
        {
            DrawEmptyState(context, chartBounds);
            return;
        }

        var minPrice = candles.Min(candle => candle.Low);
        var maxPrice = candles.Max(candle => candle.High);
        var padding = (maxPrice - minPrice) * 0.08m;
        if (padding <= 0)
        {
            padding = Math.Max(maxPrice * 0.02m, 0.00000001m);
        }

        minPrice -= padding;
        maxPrice += padding;

        var priceRange = Math.Max((double)(maxPrice - minPrice), 0.00000001d);
        var slotWidth = chartBounds.Width / candles.Count;
        var candleBodyWidth = Math.Clamp(slotWidth * 0.56d, 5d, 14d);
        var linePen = new Pen(new SolidColorBrush(Color.Parse("#1EF2D2")), 3);
        var latestLinePen = new Pen(new SolidColorBrush(Color.Parse("#661EF2D2")), 1.2);

        double MapY(decimal price)
        {
            var normalized = (double)(price - minPrice) / priceRange;
            return chartBounds.Bottom - (normalized * chartBounds.Height);
        }

        DrawAreaFill(context, chartBounds, candles, MapY);
        DrawVolumeBars(context, chartBounds, candles);
        DrawPriceLabels(context, chartBounds, minPrice, maxPrice, MapY);
        if (ShowVwap) DrawVwap(context, chartBounds, candles, MapY);

        var closePoints = new List<Point>(candles.Count);
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            var centerX = chartBounds.Left + (slotWidth * index) + (slotWidth / 2d);
            var openY = MapY(candle.Open);
            var closeY = MapY(candle.Close);
            var highY = MapY(candle.High);
            var lowY = MapY(candle.Low);
            var bullish = candle.Close >= candle.Open;
            var brush = new SolidColorBrush(Color.Parse(bullish ? "#2EA043" : "#F85149"));

            closePoints.Add(new Point(centerX, closeY));

            if (candles.Count == 1)
            {
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#1EF2D2")), null, new Point(centerX, closeY), 6, 6);
                continue;
            }

            context.DrawLine(new Pen(brush, 1.3), new Point(centerX, highY), new Point(centerX, lowY));

            var bodyTop = Math.Min(openY, closeY);
            var bodyBottom = Math.Max(openY, closeY);
            var bodyHeight = Math.Max(3d, bodyBottom - bodyTop);
            var bodyRect = new Rect(centerX - (candleBodyWidth / 2d), bodyTop, candleBodyWidth, bodyHeight);
            context.DrawRectangle(brush, new Pen(brush, 1), bodyRect, 2, 2);
        }

        for (var index = 1; index < closePoints.Count; index++)
        {
            context.DrawLine(linePen, closePoints[index - 1], closePoints[index]);
        }

        if (closePoints.Count > 0)
        {
            var lastPoint = closePoints[^1];
            context.DrawLine(latestLinePen, new Point(chartBounds.Left, lastPoint.Y), new Point(chartBounds.Right, lastPoint.Y));
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#401EF2D2")), null, lastPoint, 12, 12);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#1EF2D2")), null, lastPoint, 5, 5);
        }
    }

    private static void DrawBackdrop(DrawingContext context, Rect bounds)
    {
        var background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#111820"), 0),
                new GradientStop(Color.Parse("#0A0F15"), 1)
            }
        };

        context.DrawRectangle(background, new Pen(new SolidColorBrush(Color.Parse("#24303D")), 1), bounds, 12, 12);
    }

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#1F2934")), 1);

        for (var index = 1; index < 5; index++)
        {
            var y = bounds.Top + ((bounds.Height / 5d) * index);
            context.DrawLine(gridPen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }

        for (var index = 1; index < 6; index++)
        {
            var x = bounds.Left + ((bounds.Width / 6d) * index);
            context.DrawLine(gridPen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private static void DrawAreaFill(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<DexOhlcvPoint> candles,
        Func<decimal, double> mapY)
    {
        if (candles.Count < 2)
        {
            return;
        }

        var closePoints = new List<Point>(candles.Count);
        var slotWidth = bounds.Width / candles.Count;
        for (var index = 0; index < candles.Count; index++)
        {
            var centerX = bounds.Left + (slotWidth * index) + (slotWidth / 2d);
            closePoints.Add(new Point(centerX, mapY(candles[index].Close)));
        }

        var geometry = new StreamGeometry();
        using (var geo = geometry.Open())
        {
            geo.BeginFigure(new Point(closePoints[0].X, bounds.Bottom), true);
            geo.LineTo(closePoints[0]);
            for (var index = 1; index < closePoints.Count; index++)
            {
                geo.LineTo(closePoints[index]);
            }

            geo.LineTo(new Point(closePoints[^1].X, bounds.Bottom));
            geo.EndFigure(true);
        }

        var areaBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#2A14E0C1"), 0),
                new GradientStop(Color.Parse("#0014E0C1"), 1)
            }
        };

        context.DrawGeometry(areaBrush, null, geometry);
    }

    private static void DrawVolumeBars(DrawingContext context, Rect bounds, IReadOnlyList<DexOhlcvPoint> candles)
    {
        var maxVolume = candles.Max(candle => candle.Volume);
        if (maxVolume <= 0 || candles.Count < 2)
        {
            return;
        }

        var slotWidth = bounds.Width / candles.Count;
        var volumeAreaHeight = Math.Min(52, bounds.Height * 0.22);
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            var barHeight = (double)(candle.Volume / maxVolume) * volumeAreaHeight;
            var barWidth = Math.Clamp(slotWidth * 0.55d, 2d, 10d);
            var centerX = bounds.Left + (slotWidth * index) + (slotWidth / 2d);
            var barRect = new Rect(
                centerX - (barWidth / 2d),
                bounds.Bottom - barHeight,
                barWidth,
                Math.Max(1d, barHeight));

            var brush = new SolidColorBrush(Color.Parse(candle.Close >= candle.Open ? "#1E7A51" : "#7A2A35"));
            context.DrawRectangle(brush, null, barRect, 2, 2);
        }
    }

    private static void DrawPriceLabels(
        DrawingContext context,
        Rect bounds,
        decimal minPrice,
        decimal maxPrice,
        Func<decimal, double> mapY)
    {
        foreach (var price in new[] { maxPrice, (maxPrice + minPrice) / 2m, minPrice })
        {
            var y = mapY(price) - 10;
            var text = new FormattedText(
                price.ToString("$0.000000", System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                new SolidColorBrush(Color.Parse("#8B949E")));

            context.DrawText(text, new Point(bounds.Right - text.Width - 10, Math.Max(bounds.Top + 4, y)));
        }
    }

    private static void DrawVwap(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<DexOhlcvPoint> candles,
        Func<decimal, double> mapY)
    {
        if (candles.Count < 3) return;

        int n = candles.Count;
        var vwaps  = new double[n];
        var sigma1 = new double[n];
        var sigma2 = new double[n];

        double cumTPV = 0d, cumVol = 0d, cumTP2V = 0d;
        for (int i = 0; i < n; i++)
        {
            var c  = candles[i];
            var tp = (double)(c.High + c.Low + c.Close) / 3d;
            var v  = (double)c.Volume;
            cumTPV  += tp * v;
            cumVol  += v;
            cumTP2V += tp * tp * v;

            if (cumVol < 1e-12d) { vwaps[i] = tp; continue; }
            var w  = cumTPV / cumVol;
            var s  = Math.Sqrt(Math.Max(0d, cumTP2V / cumVol - w * w));
            vwaps[i]  = w;
            sigma1[i] = s;
            sigma2[i] = s * 2d;
        }

        var slotWidth = bounds.Width / n;
        double XAt(int i) => bounds.Left + slotWidth * i + slotWidth / 2d;

        void DrawLine(double[] vals, Color stroke, double thickness, bool dashed = false)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < n; i++)
                {
                    var pt = new Point(XAt(i), mapY((decimal)vals[i]));
                    if (!started) { gc.BeginFigure(pt, false); started = true; }
                    else gc.LineTo(pt);
                }
                gc.EndFigure(false);
            }
            var pen = dashed
                ? new Pen(new SolidColorBrush(stroke), thickness) { DashStyle = new DashStyle([5, 3], 0) }
                : new Pen(new SolidColorBrush(stroke), thickness);
            context.DrawGeometry(null, pen, geo);
        }

        double[] U1 = new double[n], L1 = new double[n], U2 = new double[n], L2 = new double[n];
        for (int i = 0; i < n; i++)
        {
            U1[i] = vwaps[i] + sigma1[i]; L1[i] = vwaps[i] - sigma1[i];
            U2[i] = vwaps[i] + sigma2[i]; L2[i] = vwaps[i] - sigma2[i];
        }

        DrawLine(U2, Color.Parse("#3B82F6"), 0.8, true);
        DrawLine(L2, Color.Parse("#3B82F6"), 0.8, true);
        DrawLine(U1, Color.Parse("#60A5FA"), 1.0, true);
        DrawLine(L1, Color.Parse("#60A5FA"), 1.0, true);
        DrawLine(vwaps, Color.Parse("#FBBF24"), 1.4);
    }

    private static void DrawEmptyState(DrawingContext context, Rect bounds)
    {
        var title = new FormattedText(
            "Collecting live DEX ticks...",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            16,
            new SolidColorBrush(Color.Parse("#1EF2D2")));

        var subtitle = new FormattedText(
            "The chart will start drawing as soon as internal samples accumulate.",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            new SolidColorBrush(Color.Parse("#8B949E")));

        context.DrawText(title, new Point(bounds.Left + 24, bounds.Top + 28));
        context.DrawText(subtitle, new Point(bounds.Left + 24, bounds.Top + 56));
    }

    private void OnCandlesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }
}
