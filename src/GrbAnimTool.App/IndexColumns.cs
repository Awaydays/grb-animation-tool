using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The clip index's columns in a clip list (readable name, who, what) and the sorting by a click on any column's header. Each
/// index cell is bound to its row's clip and to the list's <see cref="FrameworkElement.Tag"/>, which holds the install's
/// <see cref="ClipIndex"/> once it is ready (null before): setting the Tag fills in every row shown, with no new rows.</summary>
static class IndexColumns
{
    /// <summary>A cell's text, or for its tooltip the same text, none when empty (an empty tooltip would still show a box).</summary>
    sealed class CellText(bool toolTip) : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            string s = values.Length == 2 && ClipOf(values[0]) is { } c && values[1] is ClipIndex x && Enum.TryParse<ClipSort>(parameter as string, out var column)
                ? ClipIndexView.Cell(x, c.Id, column) : "";
            return toolTip && s.Length == 0 ? null : s;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    static readonly CellText Text = new(toolTip: false), Tip = new(toolTip: true);

    /// <summary>A row's clip: the row itself in a clip list; in a clip log's list, the install's clip its ID names (none for an ID the
    /// install doesn't hold, a note or a line that doesn't read).</summary>
    static ClipEntry? ClipOf(object row) => row switch
    {
        ClipEntry c => c,
        Core.Logs.ClipLogLine { Clip: { } c } => c,
        _ => null,
    };

    /// <summary>The binding of an index cell: its row's clip and the list's index, through <paramref name="converter"/>.</summary>
    static MultiBinding Cell(ClipSort column, CellText converter)
    {
        var b = new MultiBinding { Converter = converter, ConverterParameter = column.ToString() };
        b.Bindings.Add(new Binding());
        b.Bindings.Add(new Binding(nameof(FrameworkElement.Tag)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListView), 1) });
        return b;
    }

    /// <summary>Puts the three columns into the list's grid after its column <paramref name="at"/> - 1 (a clip list's first, the clip's
    /// name), with these widths (no what column when <paramref name="what"/> is null), each sortable by its header (its Tag names the
    /// <see cref="ClipSort"/>) in a list that sorts; a cell too narrow for its text ends in an ellipsis and shows it whole on hover. Every
    /// header of the list then takes the compact padding (<see cref="Theme.CompactHeaders"/>). The rows are clips, or a clip log's lines.</summary>
    public static void Insert(ListView list, double readable, double who, double? what, int at = 1)
    {
        var view = (GridView)list.View;
        var columns = new[]
        {
            (ClipSort.ReadableName, "Readable name", readable, "The clip's readable name, from the tool's list (empty where it gives none)."),
            (ClipSort.Who, "Who", who, "Who plays the clip: Player, Teammates, Enemy, NPC (civilians and allies), ... Uncategorised where nothing places it."),
            (ClipSort.What, "What", what ?? 0, "What the clip is: locomotion, weapon handling, a death, cover, ... Uncategorised where nothing places it."),
        }.Where(c => c.Item1 != ClipSort.What || what is not null);
        foreach (var (column, header, width, tip) in columns)
        {
            var cell = new FrameworkElementFactory(typeof(TextBlock));
            cell.SetBinding(TextBlock.TextProperty, Cell(column, Text));
            cell.SetBinding(FrameworkElement.ToolTipProperty, Cell(column, Tip));
            cell.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            view.Columns.Insert(at++, new GridViewColumn
            {
                Width = width,
                CellTemplate = new DataTemplate { VisualTree = cell },
                Header = new GridViewColumnHeader { Content = header, Tag = column.ToString(), ToolTip = tip },
            });
        }
        Theme.CompactHeaders(list);
    }

    /// <summary>Whether a list's rows change when the index arrives: with search text (the search takes the index's fields) or a sort by
    /// one of its columns. Otherwise only the cells fill in, and the list stays where it was scrolled.</summary>
    public static bool RowsChange(string searchText, ClipSort sort) =>
        ClipSearch.HasQuery(searchText) || sort is ClipSort.ReadableName or ClipSort.Who or ClipSort.What;

    /// <summary>A click on a list's column header: the sort after it (<see cref="ClipQuery.NextSort"/>: up, then down, then back to best
    /// match first), with an arrow on the header sorted by; null when the click wasn't on a sortable header.</summary>
    public static (ClipSort Sort, bool Descending)? Click(ListView list, RoutedEventArgs e, ClipSort sort, bool descending)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag } || !Enum.TryParse<ClipSort>(tag, out var clicked)) return null;
        var next = ClipQuery.NextSort(sort, descending, clicked);
        if (list.View is GridView gv)
            foreach (var col in gv.Columns)
                if (col.Header is GridViewColumnHeader { Tag: string t } h)
                {
                    string name = ((string)h.Content).TrimEnd(' ', '▲', '▼');
                    h.Content = t == next.Sort.ToString() ? name + (next.Descending ? " ▼" : " ▲") : name;
                }
        return next;
    }

    /// <summary>Fills a category filter's box: every category of the kind with its clip count (none while there is no index), keeping
    /// the category chosen when the new index has it. The box is enabled only with an index.</summary>
    public static void FillFilter(ComboBox box, ClipIndex? index, CategoryKind kind)
    {
        string? chosen = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        if (index is not null)
            foreach (var (category, n) in ClipIndexView.Counts(index, kind))
                box.Items.Add(new ComboBoxItem { Content = FilterItem(category, n), Tag = category });
        box.SelectedIndex = Math.Max(0, box.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag as string == chosen && chosen is not null));
        box.IsEnabled = index is not null;
    }

    /// <summary>A category filter's item: the category and its clip count. The boxes are wide enough for each category's item with a
    /// count of four digits.</summary>
    public static string FilterItem(string category, int count) => $"{category}  ({count:N0})";

    /// <summary>Chooses a category in a filter's box by its name (null: All). False when the box doesn't hold it.</summary>
    public static bool Choose(ComboBox box, string? category)
    {
        int i = box.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => x.Tag as string == category);
        if (i < 0) return false;
        box.SelectedIndex = i;
        return true;
    }
}
