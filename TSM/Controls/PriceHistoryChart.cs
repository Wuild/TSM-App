using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TSM.Models;

namespace TSM.Controls;

public sealed class PriceHistoryChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<AuctionPriceHistoryPoint>?> PointsProperty =
        AvaloniaProperty.Register<PriceHistoryChart, IReadOnlyList<AuctionPriceHistoryPoint>?>(nameof(Points));

    static PriceHistoryChart() => AffectsRender<PriceHistoryChart>(PointsProperty);

    public IReadOnlyList<AuctionPriceHistoryPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;
        var gridPen = new Pen(Brush.Parse("#292929"), 1);
        for (var index = 1; index < 4; index++)
        {
            var y = bounds.Height * index / 4;
            context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
        }

        var points = Points?.Where(point => point.MinBuyout is > 0).OrderBy(point => point.Timestamp).ToArray() ?? [];
        if (points.Length == 0)
            return;
        var minimum = points.Min(point => point.MinBuyout!.Value);
        var maximum = points.Max(point => point.MinBuyout!.Value);
        var range = Math.Max(1, maximum - minimum);
        var first = points[0].Timestamp;
        var duration = Math.Max(1, (points[^1].Timestamp - first).TotalSeconds);
        Point Plot(AuctionPriceHistoryPoint point) => new(
            points.Length == 1 ? bounds.Width / 2 : (point.Timestamp - first).TotalSeconds / duration * bounds.Width,
            bounds.Height - 5 - (point.MinBuyout!.Value - minimum) / (double)range * (bounds.Height - 10));

        var orange = Brush.Parse("#F2A93B");
        var linePen = new Pen(orange, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (points.Length == 1)
        {
            context.DrawEllipse(orange, null, Plot(points[0]), 3.5, 3.5);
            return;
        }
        var geometry = new StreamGeometry();
        using (var chart = geometry.Open())
        {
            chart.BeginFigure(Plot(points[0]), false);
            foreach (var point in points.Skip(1))
                chart.LineTo(Plot(point));
        }
        context.DrawGeometry(null, linePen, geometry);
    }
}
