using System.Windows;
using System.Windows.Controls;

namespace GrbAnimTool.App;

/// <summary>A screen's panes in a window of any height: the content fills the height it is given, as it would with no scroll viewer
/// (its lists and its 3D view taking the room left), but never less than its least height (<see cref="LeastHeight"/>); in a window too
/// short for that, the content keeps its least height and scrolls, so every control stays reachable. The content's height is set
/// explicitly before each measure of it (<see cref="MeasureOverride"/>), never left to the scroll viewer's own measure at infinite height,
/// which would make a list realise every row: a screen laid out for the first time, when its tab is clicked, its lists already full,
/// included.</summary>
public sealed class FitScroll : ScrollViewer
{
    /// <summary>The least height the content needs to show every control and a few rows of each list; asked again after each layout.</summary>
    public Func<double> LeastHeight { get; set; } = () => 0;

    /// <summary>Panes kept as tall as the view, at the top, but never under their own least height (a 3D viewer: in a short window it stays
    /// in sight with its 3D view still usable, rather than growing with the panes beside it and running out of sight).</summary>
    public List<(FrameworkElement Pane, Func<double> Least)> InView { get; } = [];

    /// <summary>The least height of a viewer pane (a grid of rows): its other rows as they measure, and <paramref name="view"/>'s row at
    /// <paramref name="viewLeast"/>.</summary>
    public static double ViewerLeast(Panel pane, FrameworkElement view, double viewLeast = LeastView) =>
        pane.Children.OfType<FrameworkElement>().Where(c => c != view).Sum(c => c.DesiredSize.Height) + viewLeast + view.Margin.Top + view.Margin.Bottom;

    /// <summary>A 3D view's least height: room for the skeletons under their overlay text.</summary>
    public const double LeastView = 220;

    public FitScroll()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        // after a layout the least heights may have changed (the panes' own content): fitted again to the same view
        LayoutUpdated += (_, _) => Fit(_view);
    }

    /// <summary>The view's height, from the last measure: the one source of the content's height, so a measure and an arrange that differ
    /// by a fraction of a pixel (layout rounding at a scaled screen) never set it by turns.</summary>
    double _view;

    /// <summary>The content's height set for the height offered before the scroll viewer measures it: its first measure (the content's
    /// own height still unset) is at a height, never infinite.</summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsFinite(constraint.Height))
        {
            _view = constraint.Height;
            Fit(_view);
        }
        return base.MeasureOverride(constraint);
    }

    /// <summary>A change of a height smaller than this is no change: the layout settles.</summary>
    const double Tolerance = 1;

    /// <summary>The height the content takes in a view <paramref name="view"/> high: the view's, or its least height when that is more
    /// (or a pane's least, kept in view). A height within <see cref="Tolerance"/> of the one set stays.</summary>
    void Fit(double view)
    {
        if (Content is not FrameworkElement c || view <= 0) return;
        double h = Math.Max(view, LeastHeight());
        foreach (var (pane, least) in InView)
        {
            double ph = Math.Max(view, least());
            h = Math.Max(h, ph);
            if (Changes(pane.Height, ph, view))
            {
                pane.VerticalAlignment = VerticalAlignment.Top;
                pane.Height = ph;
            }
        }
        if (Changes(c.Height, h, view)) c.Height = h;
    }

    /// <summary>Whether a height <paramref name="was"/> is set anew to <paramref name="to"/>: when unset, or <see cref="Tolerance"/> away;
    /// and the view's own height when it fits (no rounding up: one a fraction taller than a view it fits is brought down, else a scroll bar
    /// would show for a fraction of a pixel, or a pane's foot be cut off; one a fraction shorter stays).</summary>
    static bool Changes(double was, double to, double view) =>
        double.IsNaN(was) || Math.Abs(was - to) >= Tolerance || was > to + 0.01 && to <= view + 0.01;
}
