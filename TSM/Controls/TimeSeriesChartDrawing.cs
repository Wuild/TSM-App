using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace TSM.Controls;

internal static class TimeSeriesChartDrawing
{
    private static readonly Typeface AxisTypeface = new("Inter");
    private static readonly IBrush AxisBrush = Brush.Parse("#899198");
    private static readonly IBrush GridBrush = Brush.Parse("#2B2D2F");

    public static void DrawAxes(
        DrawingContext context,
        Rect plot,
        long minimum,
        long maximum,
        DateTimeOffset first,
        DateTimeOffset last,
        int valueIntervals = 3)
    {
        var gridPen = new Pen(GridBrush, 1);
        for (var index = 0; index <= valueIntervals; index++)
        {
            var ratio = index / (double)valueIntervals;
            var y = plot.Bottom - ratio * plot.Height;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = minimum + (long)Math.Round((maximum - minimum) * ratio);
            var label = Text(FormatCompactMoney(value), 9, AxisBrush);
            context.DrawText(label, new Point(Math.Max(0, plot.Left - label.Width - 7), y - label.Height / 2));
        }

        for (var index = 0; index <= 2; index++)
        {
            var ratio = index / 2d;
            var x = plot.Left + ratio * plot.Width;
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var timestamp = first + TimeSpan.FromTicks((long)((last - first).Ticks * ratio));
            var label = Text(FormatAxisTime(timestamp, last - first), 9, AxisBrush);
            var labelX = Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width);
            context.DrawText(label, new Point(labelX, plot.Bottom + 5));
        }
    }

    public static void DrawHover(
        DrawingContext context,
        Rect bounds,
        Rect plot,
        Point point,
        DateTimeOffset timestamp,
        long copper,
        IBrush accent)
    {
        var marker = new Point(Math.Round(point.X) + 0.5, Math.Round(point.Y) + 0.5);
        context.DrawLine(new Pen(Brush.Parse("#899198"), 1),
            new Point(marker.X, plot.Top), new Point(marker.X, plot.Bottom));
        context.DrawEllipse(Brush.Parse("#111315"), new Pen(accent, 2), marker, 4, 4);

        var money = FormatMoney(copper);
        var valueText = Text(money, 11, Brush.Parse("#F4F5F6"));
        ColorMoney(valueText, money);
        var timeText = Text(timestamp.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture),
            9, AxisBrush);
        var width = Math.Min(180, Math.Max(1, bounds.Width - 6));
        var height = Math.Ceiling(valueText.Height + timeText.Height + 11);
        var x = marker.X + 12;
        if (x + width > bounds.Right - 3)
            x = marker.X - width - 12;
        x = Math.Round(Math.Clamp(x, bounds.Left + 3, bounds.Right - width - 3));
        var y = Math.Round(Math.Clamp(marker.Y - height / 2,
            bounds.Top + 3, bounds.Bottom - height - 3));
        var card = new Rect(x, y, width, height);
        context.DrawRectangle(Brush.Parse("#F0101112"), new Pen(Brush.Parse("#4A4E52"), 1), card, 4);
        context.DrawText(valueText, new Point(card.X + 8, card.Y + 5));
        context.DrawText(timeText, new Point(card.X + 8, card.Y + 5 + valueText.Height));
    }

    public static (DateTimeOffset Timestamp, long Value) InterpolateAtX<T>(
        IReadOnlyList<T> points,
        Func<T, DateTimeOffset> timestamp,
        Func<T, long> value,
        Rect plot,
        double pointerX)
    {
        if (points.Count == 1)
            return (timestamp(points[0]), value(points[0]));
        var firstTime = timestamp(points[0]);
        var lastTime = timestamp(points[^1]);
        var ratio = Math.Clamp((pointerX - plot.Left) / plot.Width, 0, 1);
        var target = firstTime + TimeSpan.FromTicks((long)((lastTime - firstTime).Ticks * ratio));
        var lower = 0;
        var upper = points.Count - 1;
        while (upper - lower > 1)
        {
            var middle = lower + (upper - lower) / 2;
            if (timestamp(points[middle]) <= target)
                lower = middle;
            else
                upper = middle;
        }
        var leftTime = timestamp(points[lower]);
        var rightTime = timestamp(points[upper]);
        var intervalTicks = (rightTime - leftTime).Ticks;
        if (intervalTicks <= 0)
            return (target, value(points[upper]));
        var intervalRatio = Math.Clamp((target - leftTime).Ticks / (double)intervalTicks, 0, 1);
        var leftValue = value(points[lower]);
        var rightValue = value(points[upper]);
        var interpolated = leftValue + (rightValue - (double)leftValue) * intervalRatio;
        return (target, (long)Math.Round(interpolated));
    }

    public static string FormatMoney(long copper)
    {
        var sign = copper < 0 ? "−" : string.Empty;
        var value = copper == long.MinValue ? long.MaxValue : Math.Abs(copper);
        var gold = value / 10_000;
        var silver = value % 10_000 / 100;
        var remainder = value % 100;
        return gold > 0
            ? $"{sign}{gold:N0}g {silver:00}s {remainder:00}c"
            : silver > 0
                ? $"{sign}{silver:N0}s {remainder:00}c"
                : $"{sign}{remainder:N0}c";
    }

    private static string FormatCompactMoney(long copper)
    {
        var sign = copper < 0 ? "−" : string.Empty;
        var value = copper == long.MinValue ? long.MaxValue : Math.Abs(copper);
        var gold = value / 10_000d;
        if (gold >= 1_000_000)
            return $"{sign}{gold / 1_000_000:0.#}mg";
        if (gold >= 1_000)
            return $"{sign}{gold / 1_000:0.#}kg";
        if (gold >= 1)
            return $"{sign}{gold:0.#}g";
        return value >= 100 ? $"{sign}{value / 100d:0.#}s" : $"{sign}{value}c";
    }

    private static string FormatAxisTime(DateTimeOffset timestamp, TimeSpan span)
    {
        var local = timestamp.ToLocalTime();
        if (span.TotalDays >= 180)
            return local.ToString("MMM yy", CultureInfo.CurrentCulture);
        if (span.TotalDays >= 2)
            return local.ToString("d MMM", CultureInfo.CurrentCulture);
        return local.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    private static FormattedText Text(string value, double size, IBrush brush) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisTypeface, size, brush);

    private static void ColorMoney(FormattedText text, string value)
    {
        var tokenStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var brush = value[index] switch
            {
                'g' => Brush.Parse("#FFD100"),
                's' => Brush.Parse("#C7CED6"),
                'c' => Brush.Parse("#C77C43"),
                _ => null
            };
            if (brush is null)
                continue;
            text.SetForegroundBrush(brush, tokenStart, index - tokenStart + 1);
            tokenStart = index + 1;
            while (tokenStart < value.Length && value[tokenStart] == ' ')
                tokenStart++;
        }
    }
}
