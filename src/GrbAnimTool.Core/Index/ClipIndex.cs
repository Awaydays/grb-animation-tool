using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Core.Index;

/// <summary>A text of a clip for the search to look in, with its weight (a match's score is multiplied by it; the clip's own name has 1)
/// and what it adds when it holds a query word another field matches best (<see cref="SearchField.Bonus"/>); <paramref name="Phrase"/>:
/// its words stand together (<see cref="SearchFieldKind.Phrase"/>).</summary>
public readonly record struct IndexSearchField(string Text, double Weight, double Bonus = ClipSearch.FurtherField, bool Phrase = false);

/// <summary>One clip of the index: who plays it and what it is, each category with the source that decided it and why; the readable
/// name (from the tool's list only); the graph documents that name it; and the glossary entries that explain its name's words. An empty
/// kind is <see cref="ClipCategories.Uncategorised"/>.</summary>
public sealed class ClipIndexEntry
{
    /// <summary>The weights of the search fields: a readable name counts as much as the clip's own name, a glossary meaning a little
    /// less, a category word less again (it is shared by many clips).</summary>
    public const double ReadableNameWeight = 1.0, GlossaryWeight = 0.7, CategoryWeight = 0.5;

    /// <summary>What a category adds when it holds a query word another field matches best: more than a further field's usual
    /// <see cref="ClipSearch.FurtherField"/>, so among clips whose names say "death" alike, those whose category is Death come first
    /// (before a death-threat emote whose readable name says it again).</summary>
    public const double CategoryBonus = 1.0;

    public required ulong ClipId { get; init; }
    public required string ClipName { get; init; }
    public string? ReadableName { get; init; }
    public required IReadOnlyList<ClipCategory> Who { get; init; }
    public required IReadOnlyList<ClipCategory> What { get; init; }
    public required IReadOnlyList<IndexDocument> Documents { get; init; }
    public required IReadOnlyList<GlossaryEntry> Explained { get; init; }
    /// <summary>Whether the what came from a container's weak rule of the graph (<see cref="DocumentRule.Weak"/>), as nothing else placed it.</summary>
    public bool WhatFromContainer { get; init; }
    /// <summary>Whether the who is the graph's union over a shared machine narrowed by the name's family (<see cref="NameRules.Narrow"/>).</summary>
    public bool WhoNarrowedByName { get; init; }
    /// <summary>The who-categories that only probably play the clip, in the vocabulary's order: those the name alone gives (its family,
    /// or its family's pick among a shared machine's characters), and those only a machine several layers share gives
    /// (<see cref="DocumentPlacement.Shared"/>: which of them plays which of its states isn't read), and the teammates where the name's
    /// family rules them out (<see cref="FamilyRule.NoTeammates"/>). A who-category a document of one layer alone gives, or the tool's
    /// list, is sure. Every category labelled as the name's is among them (<see cref="ClipIndex.Probable"/>).</summary>
    public IReadOnlyList<string> WhoProbably { get; init; } = [];

    public IReadOnlyList<ClipCategory> Of(CategoryKind kind) => kind == CategoryKind.Who ? Who : What;

    /// <summary>The kind's category names, or <see cref="ClipCategories.Uncategorised"/> alone when nothing placed it.</summary>
    public IReadOnlyList<string> Names(CategoryKind kind) => Of(kind) is { Count: > 0 } l ? l.Select(c => c.Name).ToList() : [ClipCategories.Uncategorised];

    /// <summary>The source that decided the kind, or null when it is uncategorised: the best of its categories' sources. All come from
    /// that one but for what the name adds beside the graph's (<see cref="NameRules.Carried"/>, <see cref="NameRules.CarryExitVehicle"/>),
    /// which leaves the graph the decider. A category added beside the best source's (<see cref="ClipCategory.Beside"/>: the menu rule's
    /// Menu and lobby beside a list's Idle) doesn't count: the list still decided that what.</summary>
    public CategorySource? SourceOf(CategoryKind kind) =>
        Of(kind) is { Count: > 0 } l ? (l.Any(c => !c.Beside) ? l.Where(c => !c.Beside) : l).Min(c => c.Source) : null;

    public bool IsUncategorised(CategoryKind kind) => Of(kind).Count == 0;

    /// <summary>Whether the clip is in the category (<see cref="ClipCategories.Uncategorised"/> when nothing placed the kind). The list's
    /// category filters ask it of every clip on every key typed, so it reads the categories as they stand, without a list of names.</summary>
    public bool In(CategoryKind kind, string category)
    {
        var l = Of(kind);
        if (l.Count == 0) return category == ClipCategories.Uncategorised;
        for (int i = 0; i < l.Count; i++) if (l[i].Name == category) return true;
        return false;
    }

    /// <summary>The kind's category names joined as the clip list's column shows them ("Enemy, NPC"; "Uncategorised" when nothing placed it).</summary>
    public string NamesText(CategoryKind kind) => string.Join(", ", Names(kind));

    /// <summary>The texts the clip search looks in besides the clip's name: the readable name, what the name's abbreviations stand for
    /// (<see cref="SearchText"/>: each glossary entry's own search words, "draw" for get, or else its meaning's short head as the name
    /// rules read it, "soldiers" for mil; never the explanation after it, which speaks of the family's clips in general), and the
    /// category words (Uncategorised included, so a search for it finds them), each with <see cref="CategoryBonus"/>. An entry's own
    /// search words are a phrase: "left shoulder" (LHS) counts for a query that matches its last word, so "shoulder", "shoulder aim"
    /// and "left shoulder" find the left-shoulder clips, but "left" or "aim left" don't through it, nor "right shoulder" through a name
    /// that says right (<see cref="SearchFieldKind.Phrase"/>); the compass's ("north-east" for NE) each word alone
    /// (<see cref="ClipIndex.ToSearchField"/>).</summary>
    public IReadOnlyList<IndexSearchField> SearchFields
    {
        get
        {
            var l = new List<IndexSearchField>();
            if (ReadableName is { } n) l.Add(new(n, ReadableNameWeight));
            foreach (var g in Explained) if (SearchText(g) is { } text) l.Add(new(text, GlossaryWeight, Phrase: g.Search is not null));
            foreach (var c in Names(CategoryKind.Who).Concat(Names(CategoryKind.What)).Distinct(StringComparer.Ordinal)) l.Add(new(c, CategoryWeight, CategoryBonus));
            return l;
        }
    }

    /// <summary>What the search looks in for a glossary entry: its own search words (<see cref="GlossaryEntry.Search"/>) when it gives
    /// them, else its meaning's short head (<see cref="NameRules.ShortHead"/>); null when neither (a head too long to be an expansion,
    /// such as "in hits, a light hit", which speaks of hits and deaths at once).</summary>
    public static string? SearchText(GlossaryEntry g) => g.Search ?? NameRules.ShortHead(g.Meaning);
}

/// <summary>The clip index of one install: for every clip of its animation datafile, who plays it and what it is, from three sources,
/// best first: the game's animation graph (<see cref="GraphRules"/> over <see cref="GraphFacts"/>), the list shipped with the tool
/// (<see cref="ClipIndexList"/>), the clip's name (<see cref="NameRules"/>). Per kind, the best source that places the clip at all decides
/// it; a worse one only fills a kind the better ones left empty. Two refinements: a container's weak category of the graph (a layer whose
/// clips are of many kinds) comes after the name, filling the what only where nothing else places it (labelled as the graph's); and
/// where the graph's who is the union of the layers that play a shared machine, the name's family may decide among them (what only that
/// decision keeps is labelled as the name's, what a document of one layer alone gives stays the graph's, <see cref="NameRules.Narrow"/>); and where a grab machine names a clip whose name says carry, the name adds Carry and drag
/// beside the graph's Melee and takedown, and Vehicle for a carry out of a vehicle (labelled as the name's, <see cref="NameRules.Carried"/>,
/// <see cref="NameRules.CarryExitVehicle"/>). Readable names come from the tool's list only. Build it with <see cref="ClipIndexCache.Open"/>,
/// which keeps the graph's part between runs.</summary>
public sealed class ClipIndex
{
    public required IReadOnlyList<ClipIndexEntry> Entries { get; init; }
    public required GraphFacts Facts { get; init; }
    /// <summary>The tool's list used, or null when this build holds none.</summary>
    public ClipIndexList? List { get; init; }
    /// <summary>The install's fingerprint when built from one.</summary>
    public IndexFingerprint? Fingerprint { get; init; }
    /// <summary>The graph's placement of every document (who and what its clips are), by its place in <see cref="GraphFacts.Documents"/>.</summary>
    public required IReadOnlyList<DocumentPlacement> DocumentPlacements { get; init; }

    readonly Dictionary<ulong, ClipIndexEntry> _byId = [];

    ClipIndexEntry? Lookup(ulong id)
    {
        lock (_byId)
        {
            if (_byId.Count == 0) foreach (var e in Entries) _byId.TryAdd(e.ClipId, e);
            return _byId.GetValueOrDefault(id);
        }
    }

    public ClipIndexEntry? Find(ulong clipId) => Lookup(clipId);

    /// <summary>The search fields of a clip (<see cref="ClipIndexEntry.SearchFields"/>), none for a clip the index doesn't hold: the hook
    /// for the clip search's extra fields.</summary>
    public IReadOnlyList<IndexSearchField> SearchFields(ulong clipId) => Lookup(clipId)?.SearchFields ?? [];

    /// <summary>An index field as the clip search takes it: its text as words written apart (<see cref="SearchFieldKind.Prose"/>: never
    /// matched across them but for two typed together; <see cref="SearchFieldKind.Phrase"/> for a phrase), with the index's weight and bonus.
    /// The compass is a case of its own: a phrase made only of compass words ("north-east") is a <see cref="SearchFieldKind.Compound"/>,
    /// as each of its words is a direction (north finds the diagonals ahead, east those to the right), where a phrase's leading word
    /// only tells its last word apart ("left" of "left shoulder" is no direction).</summary>
    public static SearchField ToSearchField(IndexSearchField f) =>
        new(f.Text, f.Weight, !f.Phrase ? SearchFieldKind.Prose : IsCompass(f.Text) ? SearchFieldKind.Compound : SearchFieldKind.Phrase, f.Bonus);

    static readonly string[] CompassWords = ["north", "south", "east", "west"];

    static bool IsCompass(string text) => ClipSearch.Words(text) is { Count: > 1 } words && words.All(w => CompassWords.Contains(w));

    Func<ClipEntry, IEnumerable<SearchField>>? _searchSource;

    /// <summary>The clip search's fields with this index (<see cref="ClipQuery.Fields"/>): the clip's name and hex ID, as the search
    /// takes them without an index (<see cref="ClipSearch.NameAndId"/>), then the index's fields of the clip (<see cref="SearchFields"/>,
    /// each through <see cref="ToSearchField"/>). It is one delegate for the index's life, so a list's search with it is built once
    /// (<see cref="ClipSearch.For"/> tells field sources apart by the delegate).</summary>
    public Func<ClipEntry, IEnumerable<SearchField>> SearchSource =>
        LazyInitializer.EnsureInitialized(ref _searchSource, () => c => ClipSearch.NameAndId(c).Concat(SearchFields(c.Id).Select(ToSearchField)));

    /// <summary>The index of an install's clips from its graph's facts and the tool's list.</summary>
    public static ClipIndex Build(GameInstall g, GraphFacts facts, ClipIndexList? list, IndexFingerprint? fingerprint = null) =>
        Build(g.ForgeClips.Select(c => (c.Id, c.Name)), facts, list, fingerprint);

    /// <summary>The index of <paramref name="clips"/> (ID and name each) from the graph's facts and the tool's list.</summary>
    public static ClipIndex Build(IEnumerable<(ulong Id, string Name)> clips, GraphFacts facts, ClipIndexList? list, IndexFingerprint? fingerprint = null)
    {
        var placed = GraphRules.Place(facts);
        var all = clips.ToList();
        // the carries into vehicles that the graph places as Vehicle: the evidence for the carries out of the same vehicles
        var carryEntries = NameRules.CarryEntryVehicles(all
            .Where(c => NameRules.CarryVehicleWord(c.Name, "entry") is not null && facts.Clips.ContainsKey(c.Id))
            .Select(c => (c.Name, (IReadOnlyList<ClipCategory>)GraphRules.ForClip(facts.Clips[c.Id], facts, placed).What)));
        var entries = new List<ClipIndexEntry>();
        foreach (var (id, name) in all)
        {
            List<ClipCategory> gWho = [], gWhat = [], gWeak = [];
            List<IndexDocument> docs = [];
            bool npcSideOnly = false, narrowedWho = false;
            // the who-categories a document of one layer alone gives: the graph's sure ones
            var sureByGraph = new HashSet<string>(StringComparer.Ordinal);
            if (facts.Clips.TryGetValue(id, out var cf))
            {
                foreach (int d in cf.Documents) if (!placed[d].Shared) sureByGraph.UnionWith(placed[d].Who.Keys);
                (gWho, gWhat) = GraphRules.ForClip(cf, facts, placed);
                gWhat = NameRules.Unwounded(name, gWhat, cf, placed);
                gWhat = NameRules.Carried(name, gWhat, cf, facts, placed);
                gWhat = NameRules.CarryExitVehicle(name, gWhat, carryEntries);
                gWeak = NameRules.Corroborate(name, GraphRules.WeakWhat(cf, facts, placed));
                if (NameRules.Narrow(name, cf, facts, placed) is { } narrowed) (gWho, narrowedWho) = (narrowed, true);
                docs = cf.Documents.Select(d => facts.Documents[d]).ToList();
                npcSideOnly = cf.Documents.Count > 0 && cf.Documents.All(d => placed[d].UnderNpcMachine);
            }
            var listed = list?.Clips.GetValueOrDefault(id);
            List<ClipCategory> lWho = listed is null ? [] : listed.Who.Select(c => new ClipCategory(c, CategorySource.ToolList, "the tool's list places it")).ToList();
            List<ClipCategory> lWhat = listed is null ? [] : listed.What.Select(c => new ClipCategory(c, CategorySource.ToolList, "the tool's list places it")).ToList();
            var (nWho, nWhat, explained) = NameRules.Place(name, list?.Glossary);
            // a main-character family says nothing of a clip only the NPC machine's documents name: NPCs play it
            if (npcSideOnly && nWho.Exists(c => c.Name == ClipCategories.Player)) nWho = [];
            var what = First(gWhat, GraphRules.Sort(lWhat, ClipCategories.What), nWhat, gWeak);
            bool whatFromContainer = what.Count > 0 && ReferenceEquals(what, gWeak);
            // a clip only the menus' character's layer names: Menu and lobby beside the what the best source gives (an idle, a draw)
            if (cf is not null && GraphRules.MenuOnly(cf, placed) && !what.Any(c => c.Name == ClipCategories.MenuAndLobby))
                what = GraphRules.Sort([.. what, GraphRules.MenuCategory(cf, facts) with { Beside = what.Count > 0 }], ClipCategories.What);
            var who = First(gWho, GraphRules.Sort(lWho, ClipCategories.Who), nWho);
            // a family that rules the teammates out (the heavies', the civilians'): the teammates' own document only probably plays it
            string? noTeammates = NameRules.FamilyOf(name)?.NoTeammates;
            if (noTeammates is not null)
                who = who.Select(c => c.Name == ClipCategories.Teammates && c.Source == CategorySource.Graph
                    ? c with { Reason = $"{c.Reason}; but the name's family says {noTeammates}, so the teammates only probably play it" } : c).ToList();
            entries.Add(new ClipIndexEntry
            {
                ClipId = id, ClipName = name, ReadableName = listed?.Name, Documents = docs, Explained = explained,
                Who = who,
                What = what,
                WhatFromContainer = whatFromContainer,
                WhoNarrowedByName = narrowedWho,
                WhoProbably = who.Where(c => Probable(c, sureByGraph, noTeammates is not null)).Select(c => c.Name).ToList(),
            });
        }
        return new ClipIndex { Entries = entries, Facts = facts, List = list, Fingerprint = fingerprint, DocumentPlacements = placed };

        // the best source that places the kind at all decides it
        static IReadOnlyList<ClipCategory> First(params List<ClipCategory>[] bySource) => bySource.FirstOrDefault(l => l.Count > 0) ?? [];
    }

    /// <summary>Whether a who-category only probably plays the clip: one the name gives (its family, or its family's pick among a
    /// shared machine's characters, <see cref="NameRules.Narrow"/>); one of the graph that no document of one layer alone gives
    /// (<paramref name="sureByGraph"/>: only machines several layers share name the clip); and the teammates of a family that rules them
    /// out (<see cref="FamilyRule.NoTeammates"/>). The tool's list, and a document of one layer alone, are sure. One rule for the clip
    /// info's labels and the users' words, so the two always agree.</summary>
    static bool Probable(ClipCategory c, HashSet<string> sureByGraph, bool noTeammates) => c.Source switch
    {
        CategorySource.ToolList => false,
        CategorySource.Name => true,
        _ => !sureByGraph.Contains(c.Name) || noTeammates && c.Name == ClipCategories.Teammates,
    };

    /// <summary>The numbers over the index: clips per category, uncategorised per kind, per kind how many each source decided, and how
    /// many have a readable name.</summary>
    public IndexCensus Census()
    {
        var c = new IndexCensus { Clips = Entries.Count, Documents = Facts.Documents.Count, DocumentFailures = Facts.Failures.Count };
        foreach (var kind in new[] { CategoryKind.Who, CategoryKind.What })
        {
            var per = c.PerCategory[kind];
            var by = c.DecidedBy[kind];
            foreach (var name in ClipCategories.Of(kind)) per[name] = 0;
            foreach (var e in Entries)
            {
                foreach (var n in e.Names(kind)) per[n] = per.GetValueOrDefault(n) + 1;
                if (e.SourceOf(kind) is { } s) by[s] = by.GetValueOrDefault(s) + 1;
            }
        }
        c.ReadableNames = Entries.Count(e => e.ReadableName is not null);
        c.NamedByGraph = Entries.Count(e => e.Documents.Count > 0);
        c.DocumentsPlacingWho = DocumentPlacements.Count(p => p.Who.Count > 0);
        c.DocumentsPlacingWhat = DocumentPlacements.Count(p => p.What.Count > 0 || p.WeakWhat.Count > 0);
        c.WhatByContainer = Entries.Count(e => e.WhatFromContainer);
        c.WhoNarrowedByName = Entries.Count(e => e.WhoNarrowedByName);
        c.SeveralUsers = Entries.Count(e => e.Who.Count > 1);
        c.SeveralUsersProbably = Entries.Count(e => e.Who.Count > 1 && e.WhoProbably.Count > 0);
        c.WhoProbably = Entries.Count(e => e.WhoProbably.Count > 0);
        if (List is { } l)
        {
            c.ListClips = l.Clips.Count; c.ListUnsure = l.UnsureClips; c.ListGlossary = l.Glossary.Count; c.ListProblems = l.Problems.Count; c.ListError = l.Error;
            // where the list and the graph both place a kind, the graph decides: does the list say the same, or only some of it?
            foreach (var e in Entries)
            {
                if (!l.Clips.TryGetValue(e.ClipId, out var listed)) continue;
                foreach (var kind in new[] { CategoryKind.Who, CategoryKind.What })
                {
                    var said = kind == CategoryKind.Who ? listed.Who : listed.What;
                    if (said.Count == 0 || e.SourceOf(kind) != CategorySource.Graph) continue;
                    var graph = e.Names(kind);
                    var tally = said.All(graph.Contains) ? c.ListWithinGraph : c.ListBeyondGraph;
                    tally[kind] = tally.GetValueOrDefault(kind) + 1;
                    if (kind != CategoryKind.Who) continue;
                    // the list's who is sure: it should leave out none of the graph's sure users, and name none the graph has only as probable
                    if (e.Who.Any(u => !e.WhoProbably.Contains(u.Name) && !said.Contains(u.Name))) c.ListFewerThanGraphSure++;
                    if (said.Any(e.WhoProbably.Contains)) c.ListSureWhereGraphProbable++;
                }
            }
        }
        c.ListBuiltIn = List is not null;
        return c;
    }
}

/// <summary>The index's numbers on one install.</summary>
public sealed class IndexCensus
{
    public int Clips, Documents, DocumentFailures, NamedByGraph, DocumentsPlacingWho, DocumentsPlacingWhat, ReadableNames;
    /// <summary>The clips whose what a container's weak rule of the graph filled (nothing else placed it), and those whose who the name's
    /// family narrowed among a shared machine's characters.</summary>
    public int WhatByContainer, WhoNarrowedByName;
    /// <summary>The clips more than one who-category plays, and of them those with a category that only probably plays it
    /// (<see cref="ClipIndexEntry.WhoProbably"/>); and every clip with such a category.</summary>
    public int SeveralUsers, SeveralUsersProbably, WhoProbably;
    public bool ListBuiltIn;
    public int ListClips, ListUnsure, ListGlossary, ListProblems;
    public string? ListError;
    /// <summary>Clips per category of each kind (a clip counts once in each of its categories; Uncategorised when nothing placed the kind).</summary>
    public Dictionary<CategoryKind, Dictionary<string, int>> PerCategory { get; } = new() { [CategoryKind.Who] = [], [CategoryKind.What] = [] };
    /// <summary>Per kind, how many clips each source decided.</summary>
    public Dictionary<CategoryKind, Dictionary<CategorySource, int>> DecidedBy { get; } = new() { [CategoryKind.Who] = [], [CategoryKind.What] = [] };

    /// <summary>Per kind, the clips the list and the graph both place, where every category the list gives is among the graph's, and
    /// where the list gives one the graph doesn't (the graph decides; the list's is not shown).</summary>
    public Dictionary<CategoryKind, int> ListWithinGraph { get; } = [];
    public Dictionary<CategoryKind, int> ListBeyondGraph { get; } = [];
    /// <summary>Of those for who: the clips whose list entry leaves out one of the graph's sure users (one not in
    /// <see cref="ClipIndexEntry.WhoProbably"/>), and those whose list entry names, as sure, a user the graph has only as probable.</summary>
    public int ListFewerThanGraphSure, ListSureWhereGraphProbable;

    public int Uncategorised(CategoryKind kind) => PerCategory[kind].GetValueOrDefault(ClipCategories.Uncategorised);
    public int Decided(CategoryKind kind, CategorySource source) => DecidedBy[kind].GetValueOrDefault(source);

    public IEnumerable<string> Lines()
    {
        yield return $"clips {Clips}; graph documents {Documents} ({DocumentFailures} not read), {NamedByGraph} clips named by a document; "
            + $"documents placing who {DocumentsPlacingWho}, what {DocumentsPlacingWhat}";
        yield return !ListBuiltIn ? "the tool's list: none built into this copy"
            : ListError is not null ? $"the tool's list: refused ({ListError})"
            : $"the tool's list: {ListClips} clips ({ListUnsure} unsure, left out), {ListGlossary} glossary entries, {ListProblems} problems; "
                + $"where the graph also places them, the list says the graph's or part of it for who {ListWithinGraph.GetValueOrDefault(CategoryKind.Who)}, "
                + $"what {ListWithinGraph.GetValueOrDefault(CategoryKind.What)}, and more than the graph for who {ListBeyondGraph.GetValueOrDefault(CategoryKind.Who)}, "
                + $"what {ListBeyondGraph.GetValueOrDefault(CategoryKind.What)}; of who, fewer than the graph's sure users {ListFewerThanGraphSure}, "
                + $"sure where the graph has a user only as probable {ListSureWhereGraphProbable}";
        foreach (var kind in new[] { CategoryKind.Who, CategoryKind.What })
        {
            string k = kind == CategoryKind.Who ? "who" : "what";
            yield return $"{k}: " + string.Join(", ", ClipCategories.Of(kind).Select(n => $"{n} {PerCategory[kind].GetValueOrDefault(n)}"));
            yield return $"{k} decided {ClipCategories.FromGraph} {Decided(kind, CategorySource.Graph)}, {ClipCategories.FromToolList} {Decided(kind, CategorySource.ToolList)}, "
                + $"{ClipCategories.FromName} {Decided(kind, CategorySource.Name)}; {ClipCategories.Uncategorised} {Uncategorised(kind)}"
                + (kind == CategoryKind.Who ? $"; narrowed by the name's family among a shared machine's {WhoNarrowedByName}"
                    : $"; of the graph's, by a container alone (nothing else placing it) {WhatByContainer}");
            if (kind == CategoryKind.Who)
                yield return $"who: played by more than one {SeveralUsers} (with a probable one {SeveralUsersProbably}); "
                    + $"with a probable one {WhoProbably}; {ClipUsers.ProbablyMeaning}";
        }
        yield return $"readable names {ReadableNames}";
    }
}
