using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CryptoAITerminal.TerminalUI.Controls;

public class CexPriceChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<Point>?> PointsProperty =
        AvaloniaProperty.Register<CexPriceChart, IReadOnlyList<Point>?>(nameof(Points));

    public static readonly StyledProperty<bool> IsPositiveTrendProperty =
        AvaloniaProperty.Register<CexPriceChart, bool>(nameof(IsPositiveTrend), true);

    private INotifyCollectionChanged? _subscribedCollection;

    static CexPriceChart()
    {
        AffectsRender<CexPriceChart>(PointsProperty, IsPositiveTrendProperty);
    }

    public IReadOnlyList<Point>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public bool IsPositiveTrend
    {
        get => GetValue(IsPositiveTrendProperty);
        set => SetValue(IsPositiveTrendProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PointsProperty)
        {
            if (_subscribedCollection is not null)
            {
                _subscribedCollection.CollectionChanged -= OnPointsCollectionChanged;
                _subscribedCollection = null;
            }

            if (Points is INotifyCollectionChanged notifyCollectionChanged)
            {
                _subscribedCollection = notifyCollectionChanged;
                _subscribedCollection.CollectionChanged += OnPointsCollectionChanged;
            }

            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(12, 12, Math.Max(0, Bounds.Width - 24), Math.Max(0, Bounds.Height - 24));
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        DrawBackground(context, bounds);
        DrawGrid(context, bounds);

        var points = Points?.ToList() ?? [];
        if (points.Count == 0)
        {
            DrawEmptyState(context, bounds);
            return;
        }

        var sourceWidth = Math.Max(points.Max(point => point.X), 1d);
        var sourceHeight = Math.Max(points.Max(point => point.Y), 1d);

        var scaled = points
            .Select(point => new Point(
                bounds.Left + ((point.X / sourceWidth) * bounds.Width),
                bounds.Top + ((point.Y / sourceHeight) * bounds.Height)))
            .ToList();

        if (scaled.Count == 1)
        {
            var single = scaled[0];
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#21E6C1")), null, single, 5, 5);
            return;
        }

        var lineColor = Color.Parse(IsPositiveTrend ? "#21E6C1" : "#FF6B6B");
        var areaColor = Color.Parse(IsPositiveTrend ? "#2D21E6C1" : "#2DFF6B6B");
        var softLineColor = Color.Parse(IsPositiveTrend ? "#7021E6C1" : "#70FF6B6B");
        var pen = new Pen(new SolidColorBrush(lineColor), 2.6);
        var guidePen = new Pen(new SolidColorBrush(softLineColor), 1.2);

        var geometry = new StreamGeometry();
        using (var geo = geometry.Open())
        {
            geo.BeginFigure(new Point(scaled[0].X, bounds.Bottom), true);
            geo.LineTo(scaled[0]);
            for (var index = 1; index < scaled.Count; index++)
            {
                geo.LineTo(scaled[index]);
            }

            geo.LineTo(new Point(scaled[^1].X, bounds.Bottom));
            geo.EndFigure(true);
        }

        context.DrawGeometry(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(areaColor, 0),
                    new GradientStop(Color.Parse("#00000000"), 1)
                }
            },
            null,
            geometry);

        for (var index = 1; index < scaled.Count; index++)
        {
            context.DrawLine(pen, scaled[index - 1], scaled[index]);
        }

        var lastPoint = scaled[^1];
        context.DrawLine(guidePen, new Point(bounds.Left, lastPoint.Y), new Point(bounds.Right, lastPoint.Y));
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#4021E6C1")), null, lastPoint, 10, 10);
        context.DrawEllipse(new SolidColorBrush(lineColor), null, lastPoint, 4, 4);
    }

    private static void DrawBackground(DrawingContext context, Rect bounds)
    {
        context.DrawRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#0C131B"), 0),
                    new GradientStop(Color.Parse("#0A1017"), 1)
                }
            },
            new Pen(new SolidColorBrush(Color.Parse("#233040")), 1),
            bounds,
            14,
            14);
    }

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#17212D")), 1);
        for (var index = 1; index < 5; index++)
        {
            var y = bounds.Top + ((bounds.Height / 5d) * index);
            context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }

        for (var index = 1; index < 6; index++)
        {
            var x = bounds.Left + ((bounds.Width / 6d) * index);
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private static void DrawEmptyState(DrawingContext context, Rect bounds)
    {
        var title = new FormattedText(
            "Collecting market ticks...",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            16,
            new SolidColorBrush(Color.Parse("#21E6C1")));

        var subtitle = new FormattedText(
            "The line chart appears automatically as soon as live price points accumulate.",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            new SolidColorBrush(Color.Parse("#8FA3B8")));

        context.DrawText(title, new Point(bounds.Left + 22, bounds.Top + 22));
        context.DrawText(subtitle, new Point(bounds.Left + 22, bounds.Top + 52));
    }

    private void OnPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }
}
