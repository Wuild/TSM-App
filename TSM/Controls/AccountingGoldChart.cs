using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TSM.Models;

namespace TSM.Controls;

public sealed class AccountingGoldChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<AccountingGoldPoint>?> PointsProperty =
        AvaloniaProperty.Register<AccountingGoldChart, IReadOnlyList<AccountingGoldPoint>?>(nameof(Points));

    static AccountingGoldChart() => AffectsRender<AccountingGoldChart>(PointsProperty);

    public IReadOnlyList<AccountingGoldPoint>? Points
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
        var gridPen = new Pen(Brush.Parse("#303030"), 1);
        for (var index = 1; index < 5; index++)
        {
            var y = bounds.Height * index / 5;
            context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
        }
        for (var index = 1; index < 7; index++)
        {
            var x = bounds.Width * index / 7;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        var points = Points?.OrderBy(point => point.Timestamp).ToArray() ?? [];
        if (points.Length == 0)
            return;
        var minimum = Math.Min(0, points.Min(point => point.BalanceCopper));
        var maximum = Math.Max(0, points.Max(point => point.BalanceCopper));
        var range = Math.Max(1, maximum - minimum);
        var first = points[0].Timestamp;
        var duration = Math.Max(1, (points[^1].Timestamp - first).TotalSeconds);
        Point Plot(AccountingGoldPoint point) => new(
            points.Length == 1 ? bounds.Width / 2 : (point.Timestamp - first).TotalSeconds / duration * bounds.Width,
            bounds.Height - 6 - (point.BalanceCopper - minimum) / (double)range * (bounds.Height - 12));
        var zeroY = bounds.Height - 6 - (0 - minimum) / (double)range * (bounds.Height - 12);
        context.DrawLine(new Pen(Brush.Parse("#525252"), 1), new Point(0, zeroY), new Point(bounds.Width, zeroY));

        var positive = points[^1].BalanceCopper >= 0;
        var color = Brush.Parse(positive ? "#69C58F" : "#F06464");
        var fill = Brush.Parse(positive ? "#2869C58F" : "#28F06464");
        if (points.Length == 1)
        {
            context.DrawEllipse(color, null, Plot(points[0]), 3.5, 3.5);
            return;
        }

        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(Plot(points[0]).X, zeroY), true);
            foreach (var point in points)
                geometry.LineTo(Plot(point));
            geometry.LineTo(new Point(Plot(points[^1]).X, zeroY));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(Plot(points[0]), false);
            foreach (var point in points.Skip(1))
                geometry.LineTo(Plot(point));
        }
        context.DrawGeometry(null, new Pen(color, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}
