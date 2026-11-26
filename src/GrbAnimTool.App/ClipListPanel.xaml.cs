using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The clip list every screen that shows the install's clips uses (Library, Edit, Import, and any later one, a log's viewer
/// among them): the search (<see cref="ClipSearch"/>, best matches first), the filters (status, family, length, who, what), the list with
/// the same columns everywhere (name, the clip index's readable name, who and what, then index, length, ticks, tracks and status), each
/// sortable by its header, and the count. A screen gives it the clip set (<see cref="SetClips"/>) and the clip index once it is ready
/// (<see cref="SetIndex"/>: the index's columns fill in and its filters open); it may add a fixed constraint of its own
/// (<see cref="SetConstraint"/>), a switch for it in the filter row (<see cref="ExtraFilter"/>) and a column of its own after the index's
/// (<see cref="AddColumn"/>). A row picked raises <see cref="Picked"/>; the screen shows its own clip with <see cref="Show"/>.</summary>
public partial class ClipListPanel : UserControl
{
    /// <summary>The columns' widths, the same on every screen: the name, and the clip index's readable name, who ("Enemy, NPC" whole;
    /// more with an ellipsis, whole on hover) and what, the four in sight in the Edit and Import screens' panels at their first width.</summary>
    public const double NameWidth = 200, ReadableWidth = 140, WhoWidth = 110, WhatWidth = 95;

    /// <summary>The four columns every clip list starts with, by their sort names, then the rest every list has.</summary>
    public static readonly IReadOnlyList<string> CommonColumns = ["Name", "ReadableName", "Who", "What"];
    public static readonly IReadOnlyList<string> OtherColumns = ["Index", "Length", "Ticks", "Tracks", "Status"];

    IReadOnlyList<ClipEntry> _clips = [];
    ClipIndex? _index;
    List<ClipEntry> _rows = [];
    // best matches first until a column header is clicked
    ClipSort _sort = ClipSort.Relevance;
    bool _descending;
    // a search takes a few milliseconds: the wait only gathers a burst of keys (a held key, a paste) into one
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(30) };
    // the list sets its own boxes or rows: no search per change, and no row picked
    bool _filling;
    ClipEntry? _shown;
    Func<ClipEntry, bool>? _constraint;
    string? _constraintNote;

    /// <summary>The readable-name column's width: the Library, with no screen's own columns beside it, gives it more room
    /// (<see cref="LibraryReadableWidth"/>) than the shared lists can.</summary>
    public double ReadableColumnWidth
    {
        get => ReadableColumn().Width;
        set => ReadableColumn().Width = value;
    }

    public const double LibraryReadableWidth = 220;

    GridViewColumn ReadableColumn() => ((GridView)List.View).Columns.Single(c => c.Header is GridViewColumnHeader { Tag: string t } && t == nameof(ClipSort.ReadableName));

    public ClipListPanel()
    {
        InitializeComponent();
        IndexColumns.Insert(List, readable: ReadableWidth, who: WhoWidth, what: WhatWidth);
        FillCategoryFilters();
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Apply(); };
        // a screen shown again (another tab chosen, then this one) lays its list out anew, scrolled to the top: it shows the clip shown again
        List.Loaded += (_, _) => { if (_shown is { } s && _rows.Contains(s)) List.ScrollIntoView(s); };
        // a row scrolled into view scrolls the list only, never the screen around it (a screen whose panes scroll in a short window)
        RequestBringIntoView += (_, e) => { if (e.TargetObject is DependencyObject d && (d == List || List.IsAncestorOf(d))) e.Handled = true; };
        UpdateCount();
    }

    // ---- what a screen gives the list ----

    /// <summary>Takes a clip set (an install's clips; none: an empty list): the family filter lists its families, the clip index goes
    /// (it belongs to the old set: <see cref="SetIndex"/> brings the new one), the clip shown and the constraint stay as the screen set
    /// them, and the rows are worked out anew.</summary>
    public void SetClips(IReadOnlyList<ClipEntry>? clips)
    {
        _clips = clips ?? [];
        _index = null;
        List.Tag = null;
        _shown = null;
        _filling = true;
        FamilyBox.Items.Clear();
        FamilyBox.Items.Add(new ComboBoxItem { Content = "All families", Tag = null, IsSelected = true });
        foreach (var (f, n) in ClipQuery.Families(_clips)) FamilyBox.Items.Add(new ComboBoxItem { Content = $"{f}  ({n:N0})", Tag = f });
        _filling = false;
        FillCategoryFilters();
        Apply();
    }

    /// <summary>Takes the clip set's clip index (null: none, or not ready): the readable name, who and what columns fill in on every row
    /// shown, the Who and What filters open with each category's count, and the search takes the index's fields (the rows change only
    /// when a search or a sort by one of the index's columns depends on it).</summary>
    public void SetIndex(ClipIndex? index)
    {
        _index = index;
        List.Tag = index;
        FillCategoryFilters();
        if (IndexColumns.RowsChange(SearchBox.Text, _sort)) Apply();
        else UpdateCount();
    }

    /// <summary>A fixed constraint of the screen's on top of the search and the filters (the Import screen's clips of the file's length,
    /// the Edit screen's clips that allow an edit), null for none, with a note the count shows after it (what the constraint leaves out,
    /// or that it isn't known yet).</summary>
    /// <remarks>With <paramref name="keepPlace"/> (the constraint changed on its own, as the Edit screen's marks arriving while the list is
    /// browsed) the list stays where it was browsed; without it (a change the person made, as a file chosen or a switch ticked) it shows
    /// the clip shown, else its first row, as a new search does.</remarks>
    public void SetConstraint(Func<ClipEntry, bool>? keep, string? note = null, bool keepPlace = false)
    {
        _constraint = keep;
        _constraintNote = note;
        Apply(keepPlace);
    }

    /// <summary>The note after the count alone (the constraint's progress, say): the constraint and the rows stay as they are.</summary>
    public void SetConstraintNote(string? note)
    {
        _constraintNote = note;
        UpdateCount();
    }

    /// <summary>A switch of the screen's own in the filter row, after the filters (for its constraint).</summary>
    public object? ExtraFilter
    {
        get => ExtraFilterHost.Content;
        set => ExtraFilterHost.Content = value;
    }

    /// <summary>The value a screen's own column (<see cref="AddColumn"/>) reads beside each row's clip: setting it fills in every row shown.</summary>
    public static readonly DependencyProperty ColumnDataProperty = DependencyProperty.Register(nameof(ColumnData), typeof(object), typeof(ClipListPanel));

    public object? ColumnData
    {
        get => GetValue(ColumnDataProperty);
        set => SetValue(ColumnDataProperty, value);
    }

    /// <summary>A cell of a screen's own column: its text from the row's clip and <see cref="ColumnData"/>, or for its tooltip the same
    /// text's tip, none when empty.</summary>
    sealed class OwnCell(Func<ClipEntry, object?, string> text) : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            string s = values.Length == 2 && values[0] is ClipEntry c ? text(c, values[1] == DependencyProperty.UnsetValue ? null : values[1]) : "";
            return s.Length == 0 && parameter is "tip" ? null : s;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Adds a column of the screen's own after the clip index's (the Edit screen's edits each clip allows): its cells' text from
    /// the row's clip and <see cref="ColumnData"/>, in <paramref name="font"/> when given, and their tooltip from <paramref name="tip"/>.</summary>
    public GridViewColumn AddColumn(string header, double width, string headerTip, Func<ClipEntry, object?, string> text, Func<ClipEntry, object?, string> tip,
        string? font = null)
    {
        MultiBinding Cell(Func<ClipEntry, object?, string> f, string? parameter)
        {
            var b = new MultiBinding { Converter = new OwnCell(f), ConverterParameter = parameter };
            b.Bindings.Add(new Binding());
            b.Bindings.Add(new Binding(nameof(ColumnData)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ClipListPanel), 1) });
            return b;
        }
        var cell = new FrameworkElementFactory(typeof(TextBlock));
        cell.SetBinding(TextBlock.TextProperty, Cell(text, null));
        cell.SetBinding(FrameworkElement.ToolTipProperty, Cell(tip, "tip"));
        cell.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (font is not null) cell.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily(font));
        var column = new GridViewColumn
        {
            Width = width,
            CellTemplate = new DataTemplate { VisualTree = cell },
            Header = new GridViewColumnHeader { Content = header, ToolTip = headerTip },
        };
        return Place(column);
    }

    /// <summary>Puts a screen's own column after the four every list starts with, and after the screen's own columns added before it.</summary>
    GridViewColumn Place(GridViewColumn column)
    {
        ((GridView)List.View).Columns.Insert(CommonColumns.Count + _ownColumns++, column);
        Theme.CompactHeaders(List);
        return column;
    }

    int _ownColumns;

    /// <summary>A badge cell's visibility: shown when its row's word is the badge's own.</summary>
    sealed class BadgeShown(Func<ClipEntry, object?, string> word, string own) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Length == 2 && values[0] is ClipEntry c && word(c, values[1] == DependencyProperty.UnsetValue ? null : values[1]) == own ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    MultiBinding RowBinding(IMultiValueConverter converter, string? parameter = null)
    {
        var b = new MultiBinding { Converter = converter, ConverterParameter = parameter };
        b.Bindings.Add(new Binding());
        b.Bindings.Add(new Binding(nameof(ColumnData)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ClipListPanel), 1) });
        return b;
    }

    /// <summary>Adds a column of the screen's own whose cells are a check's badge: its word (PASS, WARN or FAIL, from
    /// <paramref name="word"/>) on the palette's colour for it, as the checks lists show them, each colour a dynamic resource so a change
    /// of theme recolours it; the cell's tooltip from <paramref name="tip"/>.</summary>
    public GridViewColumn AddBadgeColumn(string header, double width, string headerTip, Func<ClipEntry, object?, string> word, Func<ClipEntry, object?, string> tip)
    {
        var cell = new FrameworkElementFactory(typeof(Grid));
        cell.SetValue(Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        cell.SetBinding(FrameworkElement.ToolTipProperty, RowBinding(new OwnCell(tip), "tip"));
        foreach (var (w, key) in new[] { ("PASS", "PassBadge"), ("WARN", "WarnBadge"), ("FAIL", "FailBadge") })
        {
            var badge = new FrameworkElementFactory(typeof(Border));
            badge.SetResourceReference(Border.BackgroundProperty, Theme.Key(key));
            badge.SetResourceReference(Border.BorderBrushProperty, Theme.Key("BadgeOutline"));
            badge.SetResourceReference(Border.BorderThicknessProperty, Theme.Key("BadgeOutlineWidth"));
            badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            badge.SetValue(Border.PaddingProperty, new Thickness(4, 1, 4, 1));
            badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            badge.SetBinding(UIElement.VisibilityProperty, RowBinding(new BadgeShown(word, w)));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, w);
            text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key("BadgeText"));
            text.SetValue(TextBlock.FontSizeProperty, 10.0);
            text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            badge.AppendChild(text);
            cell.AppendChild(badge);
        }
        return Place(new GridViewColumn
        {
            Width = width,
            CellTemplate = new DataTemplate { VisualTree = cell },
            Header = new GridViewColumnHeader { Content = header, ToolTip = headerTip },
        });
    }

    /// <summary>The rows a list shows at least (its least height, <see cref="LeastHeight"/>).</summary>
    public const int LeastRows = 5;

    /// <summary>The list is never measured at infinite height, whatever holds it (a scroll viewer measures its content so): it would then
    /// realise a row for every clip, thousands of them, on the window's thread. Offered no limit, it takes its least height.</summary>
    protected override Size MeasureOverride(Size constraint) =>
        base.MeasureOverride(double.IsFinite(constraint.Height) ? constraint : new Size(constraint.Width, LeastHeight));

    /// <summary>The least height the list needs: its search and filters as they wrap at its width, <see cref="LeastRows"/> rows under its
    /// header, and its count (a screen in a short window scrolls to give it that: <see cref="FitScroll"/>).</summary>
    public double LeastHeight => FilterGrid.DesiredSize.Height + FilterGrid.Margin.Bottom + 30 + LeastRows * 21 + 12 + CountText.DesiredSize.Height + CountText.Margin.Top;

    /// <summary>Takes a new clip set in place of the one shown (the Package screen's kept clips as they change): the search, the filters
    /// (the family chosen, when the new set has it), the sort, the clip index and the clip shown stay; the rows are worked out anew.</summary>
    public void ReplaceClips(IReadOnlyList<ClipEntry> clips)
    {
        _clips = clips;
        string? family = (FamilyBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _filling = true;
        FamilyBox.Items.Clear();
        FamilyBox.Items.Add(new ComboBoxItem { Content = "All families", Tag = null });
        foreach (var (f, n) in ClipQuery.Families(_clips)) FamilyBox.Items.Add(new ComboBoxItem { Content = $"{f}  ({n:N0})", Tag = f });
        FamilyBox.SelectedIndex = Math.Max(0, FamilyBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => family is not null && i.Tag as string == family));
        _filling = false;
        Apply();
    }

    // ---- the clip shown and the rows ----

    /// <summary>The rows the search, the filters and the constraint give, in the list's order.</summary>
    public IReadOnlyList<ClipEntry> Rows => _rows;

    /// <summary>The clip set the list shows (<see cref="SetClips"/>).</summary>
    public IReadOnlyList<ClipEntry> Clips => _clips;

    /// <summary>The clip the screen shows (<see cref="Show"/>, or the row last picked): selected whenever the rows hold it, and the row the
    /// list scrolls to.</summary>
    public ClipEntry? Shown => _shown;

    /// <summary>The row selected, or null.</summary>
    public ClipEntry? SelectedClip => List.SelectedItem as ClipEntry;

    /// <summary>Raised when a row is selected by a click or a key (null: the selection cleared), never when the list sets its own.</summary>
    public event Action<ClipEntry?>? Picked;

    /// <summary>The screen shows <paramref name="c"/> (null: none): selected and scrolled to when the rows hold it, the selection cleared
    /// when they don't; no <see cref="Picked"/>.</summary>
    public void Show(ClipEntry? c)
    {
        _shown = c;
        _filling = true;
        if (c is not null && _rows.Contains(c)) { List.SelectedItem = c; List.ScrollIntoView(c); }
        else List.SelectedItem = null;
        _filling = false;
    }

    /// <summary>Selects the clip named <paramref name="clip"/> (by name or hex ID) as a click on its row would, clearing the search and
    /// the filters when they hide it (a screen's constraint stays). Returns the clip, or null when the clip set holds none of that name
    /// or the constraint hides it.</summary>
    public ClipEntry? Select(string clip) => ClipQuery.Find(_clips, clip) is { } c ? Select(c) : null;

    /// <summary>Selects <paramref name="c"/> as a click on its row would (<see cref="Picked"/> raised), clearing the search and the
    /// filters when they hide it (a screen's constraint stays). Returns the clip, or null when the clip set doesn't hold it or the
    /// constraint hides it.</summary>
    public ClipEntry? Select(ClipEntry c)
    {
        if (!_clips.Contains(c)) return null;
        if (!_rows.Contains(c)) ClearFilters();
        if (!_rows.Contains(c)) return null;
        List.SelectedItem = c;
        List.ScrollIntoView(c);
        return c;
    }

    /// <summary>The search, the filters and the sort as the boxes stand.</summary>
    public ClipQuery Query()
    {
        bool okMin = ClipQuery.TryParseSeconds(MinBox.Text, out var min), okMax = ClipQuery.TryParseSeconds(MaxBox.Text, out var max);
        // a length that isn't a number: the box's border in the theme's red; else the theme's own border
        foreach (var (box, ok) in new[] { (MinBox, okMin), (MaxBox, okMax) })
            if (ok) box.ClearValue(Control.BorderBrushProperty);
            else box.SetResourceReference(Control.BorderBrushProperty, Theme.Key("Error"));
        return new ClipQuery
        {
            Text = SearchBox.Text,
            Index = _index,
            Fields = _index?.SearchSource,
            Status = (StatusFilter)Math.Max(0, StatusBox.SelectedIndex),
            ModdedOnly = ModdedBox.IsChecked == true,
            Family = (FamilyBox.SelectedItem as ComboBoxItem)?.Tag as string,
            Who = (WhoBox.SelectedItem as ComboBoxItem)?.Tag as string,
            What = (WhatBox.SelectedItem as ComboBoxItem)?.Tag as string,
            MinSeconds = okMin ? min : null,
            MaxSeconds = okMax ? max : null,
        };
    }

    /// <summary>Works out the rows anew: the search, the filters, the sort, then the constraint. The clip shown stays selected when the rows
    /// hold it; the list shows it, else its first row (the best match).</summary>
    public void Apply() => Apply(keepPlace: false);

    /// <summary>As <see cref="Apply()"/>; with <paramref name="keepPlace"/> (only the screen's constraint changed, as the Edit screen's marks
    /// arriving while the list is browsed) the row at the top of the list stays there, or, when the new rows leave it out, the first row
    /// after it that they hold, else the last before it.</summary>
    void Apply(bool keepPlace)
    {
        _searchDelay.Stop();
        var old = _rows;
        // a scroll still waiting for the layout (one asked for in this same turn) is where the list is
        if (keepPlace) Scroller()?.UpdateLayout();
        int topAt = keepPlace ? TopIndex() : -1;
        IEnumerable<ClipEntry> rows = Query().Apply(_clips, _sort, _descending);
        if (_constraint is { } keep) rows = rows.Where(keep);
        _rows = rows.ToList();
        _filling = true;
        List.ItemsSource = _rows;
        if (_shown is not null && _rows.Contains(_shown)) List.SelectedItem = _shown;
        ClipEntry? top = null;
        if (topAt >= 0)
        {
            var now = _rows.ToHashSet();
            for (int i = topAt; i < old.Count && top is null; i++) if (now.Contains(old[i])) top = old[i];
            for (int i = topAt - 1; i >= 0 && top is null; i--) if (now.Contains(old[i])) top = old[i];
        }
        int at = top is null ? -1 : _rows.IndexOf(top);
        if (at >= 0 && Scroller() is { } sv)
        {
            sv.UpdateLayout();
            sv.ScrollToVerticalOffset(at);
        }
        // new rows keep the old scroll offset: show the clip shown when they hold it, else the first row, the best match
        else if (ClipQuery.ScrollTarget(_rows, _shown) is { } row) List.ScrollIntoView(row);
        _filling = false;
        UpdateCount();
    }

    /// <summary>The list's own scroll viewer (its offset counts rows), once laid out.</summary>
    ScrollViewer? Scroller()
    {
        static ScrollViewer? Find(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                if (VisualTreeHelper.GetChild(d, i) is var c && (c as ScrollViewer ?? Find(c)) is { } sv) return sv;
            return null;
        }
        return Find(List);
    }

    /// <summary>The place in the rows of the row at the top of the list as it is scrolled, or -1 before it is laid out.</summary>
    int TopIndex() => Scroller() is { } sv && sv.CanContentScroll && (int)sv.VerticalOffset is var i && i >= 0 && i < _rows.Count && List.ItemsSource == _rows ? i : -1;

    /// <summary>The row at the top of the list as it is scrolled, or null before it is laid out (for tests).</summary>
    internal ClipEntry? TopRow => TopIndex() is var i and >= 0 ? _rows[i] : null;

    /// <summary>Scrolls the list so the row at <paramref name="index"/> is at its top (for tests; with <paramref name="lay"/> false, the
    /// scroll left waiting for the layout).</summary>
    internal void ScrollToRow(int index, bool lay = true)
    {
        if (Scroller() is not { } sv) return;
        sv.ScrollToVerticalOffset(index);
        if (lay) sv.UpdateLayout();
    }

    void UpdateCount() =>
        CountText.Text = $"{_rows.Count:N0} of {_clips.Count:N0} clips" + (_constraintNote is { Length: > 0 } n ? $" · {n}" : "")
            + (_index is null && _indexNote is { Length: > 0 } x ? $" · {x}" : "");

    string? _indexNote;

    /// <summary>While the clip index is read: where it stands, said after the count ("the clip index: reading the game's graph, 40%";
    /// null: nothing said). Gone once the index is here.</summary>
    public void SetIndexNote(string? note)
    {
        _indexNote = note;
        UpdateCount();
    }

    /// <summary>Empties the search and sets every filter to all (a screen's constraint stays), and works out the rows anew.</summary>
    public void ClearFilters()
    {
        _filling = true;
        SearchBox.Text = "";
        StatusBox.SelectedIndex = 0;
        ModdedBox.IsChecked = false;
        FamilyBox.SelectedIndex = 0;
        WhoBox.SelectedIndex = WhatBox.SelectedIndex = 0;
        MinBox.Text = MaxBox.Text = "";
        _filling = false;
        Apply();
    }

    /// <summary>For a picture: the search text and the category filters (null: all) set, the rows worked out at once. Returns an error
    /// message when a filter has no such category, or null.</summary>
    public string? SetForShot(string? search, string? who, string? what)
    {
        _filling = true;
        SearchBox.Text = search ?? "";
        bool w = IndexColumns.Choose(WhoBox, who), x = IndexColumns.Choose(WhatBox, what);
        _filling = false;
        if (!w || !x) return $"the clip index's filters have no category {(w ? what : who)}";
        Apply();
        return null;
    }

    void FillCategoryFilters()
    {
        bool was = _filling;
        _filling = true;
        IndexColumns.FillFilter(WhoBox, _index, CategoryKind.Who);
        IndexColumns.FillFilter(WhatBox, _index, CategoryKind.What);
        _filling = was;
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling && IsLoaded) Apply();
    }

    void OnLengthChanged(object sender, TextChangedEventArgs e) => OnFilterChanged(sender, e);

    void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        // a column's first click sorts up, the second down, the third goes back to the search's order (best match first)
        if (IndexColumns.Click(List, e, _sort, _descending) is not { } next) return;
        (_sort, _descending) = next;
        Apply();
    }

    void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        // the row picked is the one the list keeps selected and scrolls to from now on, until the screen shows another
        if (List.SelectedItem is ClipEntry c) _shown = c;
        Picked?.Invoke(List.SelectedItem as ClipEntry);
    }
}
