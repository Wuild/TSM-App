using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TSM.Models;

namespace TSM.Controls;

public sealed class PriceHistoryChart : Control
{
    private Point? _pointer;
    private bool _pointerRenderQueued;
    private readonly DispatcherTimer _pointerExitTimer;

    public static readonly StyledProperty<IReadOnlyList<AuctionPriceHistoryPoint>?> PointsProperty =
        AvaloniaProperty.Register<PriceHistoryChart, IReadOnlyList<AuctionPriceHistoryPoint>?>(nameof(Points));

    static PriceHistoryChart() => AffectsRender<PriceHistoryChart>(PointsProperty);

    public PriceHistoryChart()
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

    public IReadOnlyList<AuctionPriceHistoryPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width <= 80 || bounds.Height <= 50)
            return;
        context.DrawRectangle(Brushes.Transparent, null, bounds);

        var points = Points?.Where(point => point.MinBuyout is > 0).OrderBy(point => point.Timestamp).ToArray() ?? [];
        if (points.Length == 0)
            return;
        var minimum = points.Min(point => point.MinBuyout!.Value);
        var maximum = points.Max(point => point.MinBuyout!.Value);
        if (minimum == maximum)
            maximum = minimum + Math.Max(100, minimum / 10);
        var range = Math.Max(1, maximum - minimum);
        var first = points[0].Timestamp;
        var duration = Math.Max(1, (points[^1].Timestamp - first).TotalSeconds);
        var plot = new Rect(48, 5, Math.Max(1, bounds.Width - 54), Math.Max(1, bounds.Height - 26));
        TimeSeriesChartDrawing.DrawAxes(context, plot, minimum, maximum, first, points[^1].Timestamp, 2);
        Point Plot(AuctionPriceHistoryPoint point) => new(
            points.Length == 1 ? plot.Left + plot.Width / 2 : plot.Left + (point.Timestamp - first).TotalSeconds / duration * plot.Width,
            plot.Bottom - (point.MinBuyout!.Value - minimum) / (double)range * plot.Height);

        var orange = Brush.Parse("#F2A93B");
        var linePen = new Pen(orange, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (points.Length == 1)
        {
            context.DrawEllipse(orange, null, Plot(points[0]), 3.5, 3.5);
            DrawHoverIfNeeded(context, bounds, plot, points, minimum, range, orange);
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
        DrawHoverIfNeeded(context, bounds, plot, points, minimum, range, orange);
    }

    private void DrawHoverIfNeeded(DrawingContext context, Rect bounds, Rect plot,
        AuctionPriceHistoryPoint[] points, long minimum, long range, IBrush accent)
    {
        var chartX = _pointer is { } pointer
            ? Math.Clamp(pointer.X, plot.Left, plot.Right)
            : plot.Right;
        if (points.Length == 1)
            chartX = plot.Left + plot.Width / 2;
        var sample = TimeSeriesChartDrawing.InterpolateAtX(points,
            point => point.Timestamp, point => point.MinBuyout!.Value, plot, chartX);
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
