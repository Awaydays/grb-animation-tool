using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>Draws a clip's marks on a strip over the timeline's slider: its events as orange ticks in the top half (each with its class
/// and time on hover), its feet's plants as bars below (the left foot over the right), and the play head as a thin line, each in the
/// theme's colour for it (so they follow a change of theme).</summary>
static class TimelineMarks
{
    static T Coloured<T>(T shape, string colour) where T : Shape
    {
        shape.SetResourceReference(shape is Line ? Shape.StrokeProperty : Shape.FillProperty, Theme.Key(colour));
        return shape;
    }

    /// <summary>Fills <paramref name="strip"/> for a clip of <paramref name="duration"/> seconds; returns the play head's line, for
    /// <see cref="MoveHead"/>.</summary>
    public static Line Draw(Canvas strip, double duration, IReadOnlyList<EventMark> events, IReadOnlyList<PlantSpan> plants)
    {
        strip.Children.Clear();
        double w = strip.ActualWidth, h = strip.ActualHeight > 0 ? strip.ActualHeight : 22;
        double lastTickSeconds = duration;
        foreach (var p in plants)
        {
            double x0 = ClipMarks.X(p.From / 60.0, lastTickSeconds, w), x1 = ClipMarks.X(p.To / 60.0, lastTickSeconds, w);
            var r = Coloured(new Rectangle
            {
                Width = Math.Max(2, x1 - x0), Height = 4,
                ToolTip = FormattableString.Invariant($"{p.FootName} foot planted, ticks {p.From} to {p.To} ({p.From / 60.0:F3} s to {p.To / 60.0:F3} s), plant slot {p.Slot}"),
            }, p.Foot == 0 ? "StripLeft" : "StripRight");
            Canvas.SetLeft(r, x0);
            Canvas.SetTop(r, h / 2 + 1 + (p.Foot == 0 ? 0 : 5));
            strip.Children.Add(r);
        }
        foreach (var e in events)
        {
            var r = Coloured(new Rectangle { Width = 3, Height = h / 2 - 1, ToolTip = e.Describe() }, "StripEvent");
            Canvas.SetLeft(r, ClipMarks.X(e.Seconds, duration, w) - 1.5);
            Canvas.SetTop(r, 1);
            strip.Children.Add(r);
        }
        var head = Coloured(new Line { StrokeThickness = 1, Y1 = 0, Y2 = h, IsHitTestVisible = false }, "StripHead");
        strip.Children.Add(head);
        return head;
    }

    /// <summary>Puts the play head at <paramref name="seconds"/>.</summary>
    public static void MoveHead(Line? head, Canvas strip, double seconds, double duration)
    {
        if (head is null) return;
        head.X1 = head.X2 = ClipMarks.X(seconds, duration, strip.ActualWidth);
    }

    /// <summary>The strip's summary: how many events and plants it shows.</summary>
    public static string Summary(IReadOnlyList<EventMark> events, IReadOnlyList<PlantSpan> plants) =>
        $"{events.Count} event{(events.Count == 1 ? "" : "s")}, {plants.Count} plant{(plants.Count == 1 ? "" : "s")}";
}
