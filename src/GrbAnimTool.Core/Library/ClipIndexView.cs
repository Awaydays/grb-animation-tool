using GrbAnimTool.Core.Index;

namespace GrbAnimTool.Core.Library;

/// <summary>One category line of the clip info: the category with the label of the source that decided it, and the rule's reason.</summary>
public sealed record CategoryLine(string Text, string? Reason);

/// <summary>What the screens show of the clip index: the clip list's columns, the clip info's lines and the category filters' counts.</summary>
public static class ClipIndexView
{
    /// <summary>The most graph documents the clip info names; the rest are counted.</summary>
    public const int DocumentsShown = 6;

    /// <summary>A clip's text in one of the index's list columns: its readable name (empty where the tool's list gives none), or its who-
    /// or what-categories joined ("Enemy, NPC"; "Uncategorised" when nothing placed the kind). Empty for any other column, without an
    /// index, or for a clip the index doesn't hold.</summary>
    public static string Cell(ClipIndex? index, ulong clipId, ClipSort column)
    {
        if (index?.Find(clipId) is not { } e) return "";
        return column switch
        {
            ClipSort.ReadableName => e.ReadableName ?? "",
            ClipSort.Who => e.NamesText(CategoryKind.Who),
            ClipSort.What => e.NamesText(CategoryKind.What),
            _ => "",
        };
    }

    /// <summary>The clip info's lines for one kind: each category with the label of its source in brackets, "Enemy (from the game's
    /// graph)", with the reason; a who-category of the graph with the part of the graph that decided it instead (<see cref="ClipCategory.Part"/>),
    /// so an odd-looking one can be judged at once, those with the same part on one line, "Enemy and NPC, from the game's graph: the generic
    /// NPC layer (GEN_NPC-LayerGraph)", their reasons one under the other; a who-category that only probably plays the clip with "probably"
    /// before it, "probably Teammates (from the name)", by the same rule as <see cref="PlayedBy"/> (<see cref="ClipIndexEntry.WhoProbably"/>),
    /// so the two always agree (a line joins only categories alike in that too); <see cref="ClipCategories.Uncategorised"/> alone, with no
    /// source, when nothing placed the kind.</summary>
    public static IReadOnlyList<CategoryLine> CategoryLines(ClipIndexEntry e, CategoryKind kind)
    {
        if (e.IsUncategorised(kind))
            return [new CategoryLine(ClipCategories.Uncategorised, "Neither the game's graph, nor the tool's list, nor the clip's name places it.")];
        var lines = new List<(List<ClipCategory> Cats, bool Probably)>();
        foreach (var c in e.Of(kind))
        {
            bool probably = kind == CategoryKind.Who && e.WhoProbably.Contains(c.Name);
            if (kind == CategoryKind.Who && c.Part is not null && lines.Count > 0 && lines[^1].Probably == probably && lines[^1].Cats[0].Part == c.Part)
                lines[^1].Cats.Add(c);
            else lines.Add(([c], probably));
        }
        return lines.Select(l =>
        {
            var c = l.Cats[0];
            string names = ClipUsers.List(l.Cats.Select(x => x.Name).ToList());
            return new CategoryLine((l.Probably ? "probably " : "")
                + (kind == CategoryKind.Who && c.Part is { } part ? $"{names}, {c.SourceLabel}: {part}" : $"{names} ({c.SourceLabel})"),
                l.Cats.Select(x => x.Reason).Distinct(StringComparer.Ordinal).Count() == 1 ? c.Reason
                    : string.Join("\n", l.Cats.Select(x => $"{x.Name}: {x.Reason}")));
        }).ToList();
    }

    /// <summary>The clip info's "Played by" value: who plays the clip in one line (<see cref="ClipUsers.Text"/>), its first letter a capital.</summary>
    public static string PlayedBy(ClipIndexEntry e) => Capital(ClipUsers.Of(e).Text);

    /// <summary>The line the Edit and Import screens show beside the clip being replaced: "Played by: Player; probably also Teammates
    /// and Enemy". <paramref name="indexError"/>: why the clip index couldn't be built, when it couldn't.</summary>
    public static string PlayedByLine(ClipIndex? index, ulong clipId, string? indexError = null) => "Played by: " + ClipUsers.Of(index, clipId, indexError).Text;

    /// <summary>The Package screen's notice before Build, never stopping it: the clips several kinds of character play, each on a line of
    /// its own with who plays it, and that replacing them changes them for all of them; the clips one kind only probably plays; those the
    /// tool can't place; and, while there is no clip index (not ready yet, when Build waits for it, or it couldn't be built:
    /// <paramref name="indexError"/>, when Build goes on) or for clips it doesn't hold, that who plays them isn't known. Null when no clip
    /// needs one, or the package holds none.</summary>
    public static string? PackageNotice(ClipIndex? index, IEnumerable<(string Name, ulong Id)> clips, string? indexError = null)
    {
        var all = clips.ToList();
        if (all.Count == 0) return null;
        string these = all.Count == 1 ? "this clip" : "these clips";
        if (index is null)
            return indexError is null
                ? $"Who plays {these} isn't known yet: the clip index isn't ready (the Library screen builds it). Build opens once it is and this "
                    + "notice says who plays each clip; a replaced clip changes for every kind of character that plays it."
                : $"Who plays {these} isn't known: {ClipUsers.Of(null, 0, indexError).UnknownWhy}. A replaced clip changes for every kind of "
                    + "character that plays it. It doesn't stop Build.";
        var several = new List<string>();
        var unsure = new List<string>();
        var unplaced = new List<string>();
        var notHeld = new List<string>();
        foreach (var (name, id) in all)
        {
            var u = ClipUsers.Of(index, id);
            if (!u.Known) notHeld.Add(name);
            else if (u.Several) several.Add($"  {name}: {u.Text}");
            else if (u.OneProbable) unsure.Add($"  {name}: {u.Text}");
            else if (u.Uncategorised) unplaced.Add(name);
        }
        var said = new List<string>();
        if (several.Count > 0)
            said.Add($"More than one kind of character plays {(several.Count == 1 ? "this clip" : "these clips")}; replacing "
                + $"{(several.Count == 1 ? "it changes it" : "them changes them")} for all of them:\n{string.Join("\n", several)}");
        if (unsure.Count > 0)
            said.Add($"The tool isn't sure who plays {(unsure.Count == 1 ? "this clip" : "these clips")}: others may play "
                + $"{(unsure.Count == 1 ? "it" : "them")} too:\n{string.Join("\n", unsure)}");
        if (unplaced.Count > 0)
            said.Add($"The tool can't tell who plays {string.Join(", ", unplaced)}, so it can't say who else {(unplaced.Count == 1 ? "its replacement changes" : "their replacements change")}.");
        if (notHeld.Count > 0)
            said.Add($"Who plays {string.Join(", ", notHeld)} isn't known: the clip index doesn't hold {(notHeld.Count == 1 ? "it" : "them")}.");
        return said.Count == 0 ? null : string.Join("\n", said);
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The graph documents (state machines and layers) that name the clip, by name, each name once in the index's order, with
    /// how many documents bear it when several do ("Death-StateMachine (2)": the generic NPCs' and the teammates'): at most
    /// <paramref name="max"/>, then a line counting the rest; "none" when no document names it.</summary>
    public static IReadOnlyList<string> DocumentLines(ClipIndexEntry e, int max = DocumentsShown)
    {
        var names = e.Documents.GroupBy(d => d.Name, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key).ToList();
        if (names.Count == 0) return ["none"];
        if (names.Count <= max) return names;
        return [.. names.Take(max), $"and {names.Count - max} more"];
    }

    /// <summary>Every category of the kind with how many clips of the index are in it, in the vocabulary's order (Uncategorised last),
    /// for the category filters.</summary>
    public static IReadOnlyList<(string Category, int Count)> Counts(ClipIndex index, CategoryKind kind)
    {
        var counts = ClipCategories.Of(kind).ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        foreach (var e in index.Entries)
            foreach (var n in e.Names(kind))
                if (counts.ContainsKey(n)) counts[n]++;
        return ClipCategories.Of(kind).Select(n => (n, counts[n])).ToList();
    }

    /// <summary>How the index was had, for the line under the install's summary: its clips and readable names, where its graph part came
    /// from (<paramref name="outcome"/>; null when it was built for this run alone and not kept), how long that took, why it couldn't be
    /// kept, and the graph documents that didn't read.</summary>
    public static string Status(ClipIndex index, CacheOutcome? outcome, TimeSpan time, string? saveError)
    {
        string from = outcome switch
        {
            CacheOutcome.Reused => "read from the cache",
            CacheOutcome.RebuiltChanged => "built again from the game's graph (the game files changed)",
            CacheOutcome.RebuiltUnreadable => "built again from the game's graph (the cache file couldn't be read)",
            _ => "built from the game's graph",
        };
        string kept = outcome is null ? ", for this run only (not kept)"
            : outcome == CacheOutcome.Reused ? ""
            : saveError is null ? ", and kept in the cache"
            : $", but it couldn't be kept: {saveError}";
        string s = FormattableString.Invariant(
            $"Clip index: {index.Entries.Count:N0} clips, {index.Entries.Count(e => e.ReadableName is not null):N0} with a readable name; {from} in {time.TotalSeconds:F1} s{kept}.");
        int failed = index.Facts.Failures.Count;
        if (failed > 0) s += $" {failed} graph document{(failed == 1 ? "" : "s")} didn't read: some clips may lack the graph's categories.";
        return s;
    }
}
