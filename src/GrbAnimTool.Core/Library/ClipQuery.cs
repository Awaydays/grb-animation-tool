using System.Globalization;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Library;

/// <summary>Which clips the status filter lets through. <see cref="All"/> includes clips of unknown status.</summary>
public enum StatusFilter { All, Vanilla, Replaced, Added }

/// <summary>The column a clip list is sorted by. <see cref="ReadableName"/>, <see cref="Who"/> and <see cref="What"/> are the clip
/// index's columns (see <see cref="ClipQuery.Index"/>). <see cref="Relevance"/> is no column: the search's best matches first (see
/// <see cref="ClipSearch"/>), and index order while there is no search text.</summary>
public enum ClipSort { Name, Index, Length, Ticks, Tracks, Status, ReadableName, Who, What, Relevance, Datafile }

/// <summary>A search over the clips of an install: a name (or hex ID) text, a status, a family (the name's first part), a length range
/// and, with the clip index, a who- and a what-category. Every part left empty lets every clip through.</summary>
public sealed record ClipQuery
{
    /// <summary>The install's clip index, for the category filters and the sorts by its columns; null while there is none (the
    /// screens build it in the background). Without one every clip counts as uncategorised and has no readable name. The search's
    /// fields are set apart (<see cref="Fields"/>; the index's are <see cref="ClipIndex.SearchSource"/>).</summary>
    public ClipIndex? Index { get; init; }
    /// <summary>The who-category a clip must be in (<see cref="ClipCategories.Who"/>, <see cref="ClipCategories.Uncategorised"/>
    /// included), or null for every clip. With <see cref="What"/> both must hold: who Enemy and what Death are the enemies' deaths.</summary>
    public string? Who { get; init; }
    /// <summary>The what-category a clip must be in (<see cref="ClipCategories.What"/>, Uncategorised included), or null for every clip.</summary>
    public string? What { get; init; }

    /// <summary>Words of the clip's name in any order, each whole, the start of a word or a part of one, any case, small typos forgiven
    /// (see <see cref="ClipSearch"/>). Text starting with 0x is a hex ID (a substring of its 16 hex digits); text of eight or more hex
    /// digits also matches IDs. Text with no word and no hex digit lets every clip through, as empty text does.</summary>
    public string Text { get; init; } = "";
    /// <summary>The texts the search looks in for each clip, with their weights; null for the clip's name and its hex ID
    /// (<see cref="ClipSearch.NameAndId"/>). Pass the same delegate on every query (make it once per install): a list's search is kept
    /// per field source, told apart by delegate equality, so a new lambda each time builds the search again.</summary>
    public Func<ClipEntry, IEnumerable<SearchField>>? Fields { get; init; }
    public StatusFilter Status { get; init; } = StatusFilter.All;
    /// <summary>Only the rows that aren't the game's own: a modded file (<see cref="ClipEntry.Modded"/>, a 1_-_ file) and a record
    /// changed inside the game's files (status Replaced, or Added for an ID the game lacks).</summary>
    public bool ModdedOnly { get; init; }
    /// <summary>The family (see <see cref="FamilyOf"/>), or null for every family.</summary>
    public string? Family { get; init; }
    /// <summary>The shortest and longest clip let through, in seconds, or null for no bound.</summary>
    public float? MinSeconds { get; init; }
    public float? MaxSeconds { get; init; }

    /// <summary>The family of a clip: its name up to the first underscore (mca, mcm, nme, ...), lower case; the whole name if it has none.</summary>
    public static string FamilyOf(string name)
    {
        int u = name.IndexOf('_');
        return (u > 0 ? name[..u] : name).ToLowerInvariant();
    }

    /// <summary>Every family among the clips with its clip count, by family name.</summary>
    public static IReadOnlyList<(string Family, int Count)> Families(IEnumerable<ClipEntry> clips) =>
        clips.GroupBy(c => FamilyOf(c.Name)).Select(g => (g.Key, g.Count())).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

    /// <summary>Whether the clip passes the filters and the search text as a list of its own: the same as <see cref="Apply"/> on
    /// [<paramref name="c"/>]. Which typos a search forgives depends on the list (a word some clip holds whole takes none), so to know
    /// whether a list shows the clip, give the list: <see cref="Matches(ClipEntry, IEnumerable{ClipEntry})"/>.</summary>
    public bool Matches(ClipEntry c) => PassesFilters(c) && (!ClipSearch.HasQuery(Text) || new ClipSearch([c], Fields).Search(Text).Count > 0);

    /// <summary>Whether <see cref="Apply"/> on <paramref name="clips"/> holds <paramref name="c"/>.</summary>
    public bool Matches(ClipEntry c, IEnumerable<ClipEntry> clips) =>
        PassesFilters(c) && (ClipSearch.HasQuery(Text)
            ? SearchOver(clips).Search(Text, PassesFilters).Any(h => ReferenceEquals(h.Clip, c))
            : clips.Contains(c));

    /// <summary>The search over <paramref name="clips"/>: kept with the list when it is one (an install's clip list), built for this
    /// call otherwise.</summary>
    ClipSearch SearchOver(IEnumerable<ClipEntry> clips) =>
        clips is IReadOnlyList<ClipEntry> list ? ClipSearch.For(list, Fields) : new ClipSearch(clips, Fields);

    /// <summary>The row a list scrolls to after its rows change (a search, a filter, a sort, the index arriving): the selected clip when
    /// the new rows still hold it, so the clip on show stays in sight; otherwise the first row, the best match; null for no rows.</summary>
    public static ClipEntry? ScrollTarget(IReadOnlyList<ClipEntry> rows, ClipEntry? selected) =>
        selected is not null && rows.Contains(selected) ? selected : rows.Count > 0 ? rows[0] : null;

    /// <summary>The sort after a click on a column's header, from the sort before it: another column sorts by that column, up; the same
    /// column's second click sorts down; its third goes back to the search's order (<see cref="ClipSort.Relevance"/>: best match
    /// first, index order without search text).</summary>
    public static (ClipSort Sort, bool Descending) NextSort(ClipSort sort, bool descending, ClipSort clicked) =>
        clicked == ClipSort.Relevance ? (ClipSort.Relevance, false)
        : clicked != sort ? (clicked, false)
        : !descending ? (sort, true)
        : (ClipSort.Relevance, false);

    bool PassesFilters(ClipEntry c)
    {
        switch (Status)
        {
            case StatusFilter.Vanilla when c.Status != ClipStatus.Vanilla:
            case StatusFilter.Replaced when c.Status != ClipStatus.Replaced:
            case StatusFilter.Added when c.Status != ClipStatus.Added:
                return false;
        }
        if (ModdedOnly && !c.Modded && c.Status is not (ClipStatus.Replaced or ClipStatus.Added)) return false;
        if (Family is not null && FamilyOf(c.Name) != Family) return false;
        if (MinSeconds is float lo && c.Duration < lo) return false;
        if (MaxSeconds is float hi && c.Duration > hi) return false;
        if (Who is not null && !InCategory(c, CategoryKind.Who, Who)) return false;
        if (What is not null && !InCategory(c, CategoryKind.What, What)) return false;
        return true;
    }

    /// <summary>Whether the clip is in the category by the index; a clip the index doesn't hold (or no index) is uncategorised.</summary>
    bool InCategory(ClipEntry c, CategoryKind kind, string category) =>
        Index?.Find(c.Id) is { } e ? e.In(kind, category) : category == ClipCategories.Uncategorised;

    /// <summary>The clips that match, sorted by <paramref name="sort"/> (then by index, so the order is stable). With
    /// <see cref="ClipSort.Relevance"/> and search text they come best match first, as <see cref="ClipSearch.Search"/> ranks them;
    /// <paramref name="descending"/> reverses either order. The search over a list (an install's clip list) is built once and kept
    /// (<see cref="ClipSearch.For"/>); over any other sequence it is built for the call. The filters apply in the search, so a query
    /// that matches no clip they let through is tried again with typos for every word. The index's columns sort as
    /// <see cref="SortByIndex"/> says.</summary>
    /// <para>The rows of one clip (a modded file and the game's own, from the unpacked folder) stay together, where the first of them falls.</para>
    public List<ClipEntry> Apply(IEnumerable<ClipEntry> clips, ClipSort sort = ClipSort.Index, bool descending = false) => Grouped(Sorted(clips, sort, descending));

    /// <summary><paramref name="list"/> with each clip's rows moved up to its first row, in their own order; the list itself when no clip
    /// has two rows.</summary>
    internal static List<ClipEntry> Grouped(List<ClipEntry> list)
    {
        if (!list.Any(c => c.FromFile)) return list;
        var of = new Dictionary<ulong, List<ClipEntry>>();
        var order = new List<List<ClipEntry>>();
        foreach (var c in list)
        {
            if (!of.TryGetValue(c.Id, out var g)) { of[c.Id] = g = []; order.Add(g); }
            g.Add(c);
        }
        return order.Count == list.Count ? list : [.. order.SelectMany(g => g)];
    }

    List<ClipEntry> Sorted(IEnumerable<ClipEntry> clips, ClipSort sort, bool descending)
    {
        List<ClipEntry> list;
        if (!ClipSearch.HasQuery(Text)) list = clips.Where(PassesFilters).ToList();
        else
        {
            list = SearchOver(clips).Search(Text, PassesFilters).Select(h => h.Clip).ToList();
            if (sort == ClipSort.Relevance)
            {
                if (descending) list.Reverse();
                return list;
            }
        }
        if (sort is ClipSort.ReadableName or ClipSort.Who or ClipSort.What) return SortByIndex(list, sort, descending);
        Comparison<ClipEntry> cmp = sort switch
        {
            ClipSort.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            ClipSort.Length => (a, b) => a.Duration.CompareTo(b.Duration),
            ClipSort.Ticks => (a, b) => a.LastTick.CompareTo(b.LastTick),
            ClipSort.Tracks => (a, b) => a.TrackCount.CompareTo(b.TrackCount),
            ClipSort.Status => (a, b) => a.Status.CompareTo(b.Status),
            ClipSort.Datafile => (a, b) => string.Compare(a.DatafileName, b.DatafileName, StringComparison.OrdinalIgnoreCase),
            _ => (_, _) => 0,
        };
        list.Sort((a, b) =>
        {
            int r = cmp(a, b);
            if (r == 0) r = a.Index.CompareTo(b.Index);
            return descending ? -r : r;
        });
        return list;
    }

    /// <summary>The sorts by the index's columns: by the readable name (case ignored), or by the categories in the vocabulary's order
    /// (<see cref="ClipCategories.Who"/>, <see cref="ClipCategories.What"/>: the first category, then the next; Uncategorised, last in
    /// it, last), then by index. A clip with nothing in the column (no readable name, or not in the index) comes after the others
    /// whichever way the column is sorted. Each clip's key is read once, not at every comparison.</summary>
    List<ClipEntry> SortByIndex(List<ClipEntry> list, ClipSort sort, bool descending)
    {
        var keyed = list.Select(c => (Clip: c, Key: IndexKey(c, sort))).ToList();
        var by = sort == ClipSort.ReadableName ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        keyed.Sort((a, b) =>
        {
            if ((a.Key is null) != (b.Key is null)) return a.Key is null ? 1 : -1;
            int r = by.Compare(a.Key, b.Key);
            if (r == 0) r = a.Clip.Index.CompareTo(b.Clip.Index);
            return descending ? -r : r;
        });
        return keyed.Select(k => k.Clip).ToList();
    }

    /// <summary>A clip's key in an index column: its readable name, or its categories each as one character of its place in the
    /// vocabulary (so an ordinal comparison orders by the first category, then the next); null when the column holds nothing for it.</summary>
    string? IndexKey(ClipEntry c, ClipSort sort)
    {
        if (Index?.Find(c.Id) is not { } e) return null;
        if (sort == ClipSort.ReadableName) return e.ReadableName;
        var kind = sort == ClipSort.Who ? CategoryKind.Who : CategoryKind.What;
        var order = ClipCategories.Of(kind);
        return string.Concat(e.Names(kind).Select(n =>
        {
            int at = 0;
            while (at < order.Count && order[at] != n) at++;
            return (char)('A' + at);
        }));
    }

    /// <summary>A clip by its name (case-insensitive) or its ID in hex (with or without 0x), or null.</summary>
    public static ClipEntry? Find(IEnumerable<ClipEntry> clips, string nameOrId)
    {
        string t = nameOrId.Trim();
        var byName = clips.FirstOrDefault(c => string.Equals(c.Name, t, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        string h = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
        return ulong.TryParse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ? clips.FirstOrDefault(c => c.Id == id) : null;
    }

    /// <summary>A length bound typed by the user: empty for none, else seconds (a dot or a comma as the decimal point). False when it isn't
    /// a number of zero or more.</summary>
    public static bool TryParseSeconds(string? text, out float? seconds)
    {
        seconds = null;
        string t = (text ?? "").Trim().Replace(',', '.');
        if (t.Length == 0) return true;
        if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || !float.IsFinite(v)) return false;
        seconds = v;
        return true;
    }
}
