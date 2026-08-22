using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TSM.Models;

namespace TSM.Controls;

public sealed class AccountingGoldChart : Control
{
    private Point? _pointer;
    private bool _pointerRenderQueued;
    private readonly DispatcherTimer _pointerExitTimer;

    public static readonly StyledProperty<IReadOnlyList<AccountingGoldPoint>?> PointsProperty =
        AvaloniaProperty.Register<AccountingGoldChart, IReadOnlyList<AccountingGoldPoint>?>(nameof(Points));

    static AccountingGoldChart() => AffectsRender<AccountingGoldChart>(PointsProperty);

    public AccountingGoldChart()
    {
        _pointerExitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _pointerExitTimer.Tick += (_, _) =>
        {
            _pointerExitTimer.Stop();
            _pointer = null;
            InvalidateVisual();
        };
        PointerEntered += OnChartPointerEntered;
        PointerMoved += OnChartPointerMoved;
        PointerExited += OnChartPointerExited;
    }

    public IReadOnlyList<AccountingGoldPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width <= 100 || bounds.Height <= 60)
            return;
        context.DrawRectangle(Brushes.Transparent, null, bounds);

        var points = Points?.OrderBy(point => point.Timestamp).ToArray() ?? [];
        if (points.Length == 0)
            return;
        var minimum = Math.Min(0, points.Min(point => point.BalanceCopper));
        var maximum = Math.Max(0, points.Max(point => point.BalanceCopper));
        if (minimum == maximum)
            maximum = minimum + Math.Max(100, Math.Abs(minimum) / 10);
        var range = Math.Max(1, maximum - minimum);
        var first = points[0].Timestamp;
        var duration = Math.Max(1, (points[^1].Timestamp - first).TotalSeconds);
        var plot = new Rect(58, 8, Math.Max(1, bounds.Width - 68), Math.Max(1, bounds.Height - 34));
        TimeSeriesChartDrawing.DrawAxes(context, plot, minimum, maximum, first, points[^1].Timestamp, 4);
        Point Plot(AccountingGoldPoint point) => new(
            points.Length == 1 ? plot.Left + plot.Width / 2 : plot.Left + (point.Timestamp - first).TotalSeconds / duration * plot.Width,
            plot.Bottom - (point.BalanceCopper - minimum) / (double)range * plot.Height);
        var zeroY = plot.Bottom - (0 - minimum) / (double)range * plot.Height;
        context.DrawLine(new Pen(Brush.Parse("#525252"), 1), new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));

        var positive = points[^1].BalanceCopper >= 0;
        var color = Brush.Parse(positive ? "#69C58F" : "#F06464");
        var fill = Brush.Parse(positive ? "#2869C58F" : "#28F06464");
        if (points.Length == 1)
        {
            context.DrawEllipse(color, null, Plot(points[0]), 3.5, 3.5);
            DrawHoverIfNeeded(context, bounds, plot, points, minimum, range, color);
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
        DrawHoverIfNeeded(context, bounds, plot, points, minimum, range, color);
    }

    private void DrawHoverIfNeeded(DrawingContext context, Rect bounds, Rect plot,
        AccountingGoldPoint[] points, long minimum, long range, IBrush accent)
    {
        var chartX = _pointer is { } pointer
            ? Math.Clamp(pointer.X, plot.Left, plot.Right)
            : plot.Right;
        if (points.Length == 1)
            chartX = plot.Left + plot.Width / 2;
        var sample = TimeSeriesChartDrawing.InterpolateAtX(points,
            point => point.Timestamp, point => point.BalanceCopper, plot, chartX);
        var marker = new Point(chartX,
            plot.Bottom - (sample.Value - minimum) / (double)range * plot.Height);
        TimeSeriesChartDrawing.DrawHover(context, bounds, plot, marker,
            sample.Timestamp, sample.Value, accent);
    }

    private void OnChartPointerMoved(object? sender, PointerEventArgs args)
    {
        _pointerExitTimer.Stop();
        var position = args.GetPosition(this);
        var pointer = new Point(Math.Round(position.X), Math.Round(position.Y));
        if (_pointer == pointer)
            return;
        _pointer = pointer;
        QueuePointerRender();
    }

    private void OnChartPointerEntered(object? sender, PointerEventArgs args)
    {
        _pointerExitTimer.Stop();
        OnChartPointerMoved(sender, args);
    }

    private void OnChartPointerExited(object? sender, PointerEventArgs args)
    {
        _pointerExitTimer.Stop();
        _pointerExitTimer.Start();
    }

    private void QueuePointerRender()
    {
        if (_pointerRenderQueued)
            return;
        _pointerRenderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pointerRenderQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }
}
