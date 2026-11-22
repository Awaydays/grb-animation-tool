using System.Diagnostics;
using System.Text;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using Xunit.Abstractions;
using static GrbAnimTool.Core.Index.ClipCategories;

namespace GrbAnimTool.Tests;

/// <summary>The clip index in the screens' logic, with no game data: its fields in the search, the category filters, the sorts by its
/// columns, the columns' and the clip info's texts, and the command line of a picture's search and filters.</summary>
public sealed class IndexSearchTests
{
    /// <summary>Clips of these names, indexes 0, 1, ... and IDs 0x000001DE00000000 + index, with no record.</summary>
    static readonly ClipEntry[] Clips = new[]
    {
        "pmc_rif_death_head",        // 0: the list's Enemy, the graph's Death (two death machines)
        "civ_walk_01",               // 1: the list's NPC and Locomotion
        "mca_emote_Hello",           // 2: the list's Player and Emote
        "mystery_clip",              // 3: nothing places it
        "mcm_nwp_fm_std_V1_cycle",   // 4: a readable name only; the name's family and word place it
        "pmc_rif_reload",            // 5: not in the list; the name's family and word place it
        "npc_takedown_victim",       // 6: the list's Enemy and NPC; the graph's takedown and carry machines
    }.Select((n, i) => new ClipEntry { Index = i, Id = 0x000001DE00000000UL + (ulong)i, Name = n, Bytes = ReadOnlyMemory<byte>.Empty }).ToArray();

    const string ListJson = """
        { "format": 1,
          "glossary": [ { "abbr": "nwp", "meaning": "no weapon (holstered)", "sure": true } ],
          "clips": [
            { "id": "0x000001DE00000000", "name": "Soldier death, shot in the head", "who": ["Enemy"], "what": ["Death"], "sure": true },
            { "id": "0x000001DE00000001", "name": "Civilian walk", "who": ["NPC"], "what": ["Locomotion"], "sure": true },
            { "id": "0x000001DE00000002", "name": "Greeting wave", "who": ["Player"], "what": ["Emote"], "sure": true },
            { "id": "0x000001DE00000004", "name": "Holstered walk, standing", "sure": true },
            { "id": "0x000001DE00000006", "who": ["Enemy", "NPC"], "what": ["Melee and takedown"], "sure": true }
          ] }
        """;

    /// <summary>Graph documents no branch reaches (so the graph says nothing of who): two death machines naming clip 0, and a takedown,
    /// a grab and a carry machine naming clip 6.</summary>
    static GraphFacts Graph() => new()
    {
        Documents =
        [
            new IndexDocument(0x10, "Death-StateMachine", [], [], 1), new IndexDocument(0x11, "Death-StateMachine", [], [], 1),
            new IndexDocument(0x12, "CQC-StateMachine", [], [], 1), new IndexDocument(0x13, "Grab-StateMachine", [], [], 1),
            new IndexDocument(0x14, "Carry-StateMachine", [], [], 1),
        ],
        Clips = new Dictionary<ulong, ClipGraphFacts> { [Clips[0].Id] = new([0, 1], []), [Clips[6].Id] = new([2, 3, 4], []) },
        Failures = [], ClipsLookedFor = Clips.Length,
    };

    static ClipIndex Index() => ClipIndex.Build(Clips.Select(c => (c.Id, c.Name)), Graph(), ClipIndexList.Read(new MemoryStream(Encoding.UTF8.GetBytes(ListJson))));

    static string[] Names(IEnumerable<ClipEntry> clips) => clips.Select(c => c.Name).ToArray();

    [Fact]
    public void TheIndexsFieldsGoIntoTheSearchWithTheirWeights()
    {
        var x = Index();
        var source = x.SearchSource;
        Assert.Same(source, x.SearchSource);   // one delegate for the index: a list's search with it is built once
        var walk = Clips[4];
        var fields = source(walk).ToList();
        // the name and the hex ID first, as without the index, then each index field as words written apart, with its weight
        Assert.Equal(ClipSearch.NameAndId(walk), fields.Take(2));
        Assert.Equal(x.SearchFields(walk.Id).Select(f => new SearchField(f.Text, f.Weight, SearchFieldKind.Prose, f.Bonus)), fields.Skip(2));
        Assert.Contains(new SearchField("Holstered walk, standing", ClipIndexEntry.ReadableNameWeight, SearchFieldKind.Prose), fields);
        Assert.Contains(new SearchField("no weapon", ClipIndexEntry.GlossaryWeight, SearchFieldKind.Prose), fields);   // the meaning's head, not its bracket
        Assert.DoesNotContain(fields, f => f.Weight == ClipIndexEntry.GlossaryWeight && f.Text.Contains("holstered", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(new SearchField(Player, ClipIndexEntry.CategoryWeight, SearchFieldKind.Prose, ClipIndexEntry.CategoryBonus), fields);
        Assert.Contains(new SearchField(Locomotion, ClipIndexEntry.CategoryWeight, SearchFieldKind.Prose, ClipIndexEntry.CategoryBonus), fields);
        Assert.Equal(new SearchField("x", 0.7, SearchFieldKind.Prose), ClipIndex.ToSearchField(new IndexSearchField("x", 0.7)));
        // prose is never matched across its words; a name is
        SearchField[] Extra(ClipEntry c, SearchFieldKind kind) => [new SearchField(c.Name), new SearchField(c.Index == 3 ? "death, electrocuted" : "", 1, kind)];
        var prose = new ClipSearch(Clips, c => Extra(c, SearchFieldKind.Prose));
        var words = new ClipSearch(Clips, c => Extra(c, SearchFieldKind.Words));
        Assert.Empty(prose.Search("the"));                                                   // deat(h e)lectrocuted
        Assert.Equal(["mystery_clip"], words.Search("the").Select(h => h.Clip.Name));
        Assert.Equal(["mystery_clip"], prose.Search("electro").Select(h => h.Clip.Name));   // a word's start as ever
        Assert.Equal(["mystery_clip"], prose.Search("teryc").Select(h => h.Clip.Name));     // across the name's words
        // a clip the index doesn't hold: its name and ID alone
        var stranger = new ClipEntry { Index = 99, Id = 0x77, Name = "other_clip", Bytes = ReadOnlyMemory<byte>.Empty };
        Assert.Equal(ClipSearch.NameAndId(stranger), source(stranger));

        var q = new ClipQuery { Index = x, Fields = source };
        string[] Found(string text) => Names((q with { Text = text }).Apply(Clips, ClipSort.Relevance));
        Assert.Equal(["mcm_nwp_fm_std_V1_cycle"], Found("holstered walk"));       // the readable name's words
        Assert.Equal(["mca_emote_Hello"], Found("greting"));                      // the readable name's word, a typo forgiven
        Assert.Equal(["pmc_rif_death_head"], Found("enemy death"));              // a category word and the name's word
        Assert.Equal(["mystery_clip"], Found("uncategorised"));                   // both kinds unplaced
        Assert.Equal(["pmc_rif_reload", "pmc_rif_death_head", "npc_takedown_victim"], Found("enemy"));
        Assert.Empty(new ClipQuery { Text = "holstered walk" }.Apply(Clips, ClipSort.Relevance));   // by the names alone: none
        // the scores: a readable name weighs as the name, a category word half; "death" in the readable name and the category too adds
        // their bonuses over the name's whole word
        var s = new ClipSearch(Clips, source);
        Assert.Equal(2 * ClipSearch.WholeWord * ClipIndexEntry.ReadableNameWeight, s.Search("holstered walk")[0].Score, 9);
        Assert.All(s.Search("enemy"), h => Assert.Equal(ClipSearch.WholeWord * ClipIndexEntry.CategoryWeight, h.Score, 9));
        Assert.Equal(ClipSearch.WholeWord * (1 + ClipIndexEntry.CategoryWeight) + ClipSearch.FurtherField + ClipIndexEntry.CategoryBonus,
            s.Search("enemy death")[0].Score, 9);
    }

    [Fact]
    public void TheCategoryFiltersCombineWithEachOtherAndTheOtherFilters()
    {
        var x = Index();
        var q = new ClipQuery { Index = x };
        string[] Shown(ClipQuery query) => Names(query.Apply(Clips));
        Assert.Equal(["pmc_rif_death_head", "pmc_rif_reload", "npc_takedown_victim"], Shown(q with { Who = Enemy }));
        Assert.Equal(["pmc_rif_death_head"], Shown(q with { Who = Enemy, What = Death }));
        Assert.Equal(["npc_takedown_victim"], Shown(q with { Who = Npc, What = MeleeAndTakedown }));
        Assert.Empty(Shown(q with { Who = Npc, What = Death }));
        Assert.Equal(["civ_walk_01", "mcm_nwp_fm_std_V1_cycle"], Shown(q with { What = Locomotion }));
        Assert.Equal(["mystery_clip"], Shown(q with { Who = Uncategorised }));
        Assert.Equal(["mystery_clip"], Shown(q with { What = Uncategorised }));
        // with the family filter and the search text
        Assert.Equal(["civ_walk_01"], Shown(q with { What = Locomotion, Family = "civ" }));
        Assert.Equal(["mcm_nwp_fm_std_V1_cycle"], Shown(q with { What = Locomotion, Text = "holstered", Fields = x.SearchSource }));
        Assert.Equal(["pmc_rif_reload"], Shown(q with { Who = Enemy, Text = "pmc", What = WeaponHandling }));
        Assert.True((q with { Who = Enemy, What = Death }).Matches(Clips[0], Clips));
        Assert.False((q with { Who = Enemy, What = Death }).Matches(Clips[5], Clips));
        Assert.False((q with { Who = Enemy, What = Death }).Matches(Clips[5]));
        // without an index (not ready yet) every clip is uncategorised
        Assert.Equal(Names(Clips), Shown(new ClipQuery { Who = Uncategorised, What = Uncategorised }));
        Assert.Empty(Shown(new ClipQuery { Who = Enemy }));
    }

    [Fact]
    public void TheIndexsColumnsSortWithEmptyCellsLast()
    {
        var q = new ClipQuery { Index = Index() };
        // readable names A to Z, case ignored, then the clips with none, whichever way
        Assert.Equal(["civ_walk_01", "mca_emote_Hello", "mcm_nwp_fm_std_V1_cycle", "pmc_rif_death_head", "mystery_clip", "pmc_rif_reload", "npc_takedown_victim"],
            Names(q.Apply(Clips, ClipSort.ReadableName)));
        Assert.Equal(["pmc_rif_death_head", "mcm_nwp_fm_std_V1_cycle", "mca_emote_Hello", "civ_walk_01", "npc_takedown_victim", "pmc_rif_reload", "mystery_clip"],
            Names(q.Apply(Clips, ClipSort.ReadableName, descending: true)));
        // the categories in the vocabulary's order: Player, Enemy, Enemy and NPC, NPC, Uncategorised
        Assert.Equal(["mca_emote_Hello", "mcm_nwp_fm_std_V1_cycle", "pmc_rif_death_head", "pmc_rif_reload", "npc_takedown_victim", "civ_walk_01", "mystery_clip"],
            Names(q.Apply(Clips, ClipSort.Who)));
        // Locomotion, Weapon handling, Emote, Death, Melee and takedown (with Carry and drag), Uncategorised
        Assert.Equal(["civ_walk_01", "mcm_nwp_fm_std_V1_cycle", "pmc_rif_reload", "mca_emote_Hello", "pmc_rif_death_head", "npc_takedown_victim", "mystery_clip"],
            Names(q.Apply(Clips, ClipSort.What)));
        Assert.Equal(["mystery_clip", "npc_takedown_victim", "pmc_rif_death_head"], Names(q.Apply(Clips, ClipSort.What, descending: true)).Take(3));
        // after a search, and without an index (every cell empty: index order)
        Assert.Equal(["pmc_rif_death_head", "pmc_rif_reload"], Names((q with { Text = "pmc" }).Apply(Clips, ClipSort.Who)));
        Assert.Equal(Names(Clips), Names(new ClipQuery().Apply(Clips, ClipSort.ReadableName)));
        Assert.Equal(Names(Clips), Names(new ClipQuery().Apply(Clips, ClipSort.What)));
        // the header's clicks go up, down and back to best match for these columns as for the others
        Assert.Equal((ClipSort.Who, false), ClipQuery.NextSort(ClipSort.Relevance, false, ClipSort.Who));
        Assert.Equal((ClipSort.Who, true), ClipQuery.NextSort(ClipSort.Who, false, ClipSort.Who));
        Assert.Equal((ClipSort.Relevance, false), ClipQuery.NextSort(ClipSort.Who, true, ClipSort.Who));
    }

    [Fact]
    public void TheColumnsTheClipInfoAndTheFilterCounts()
    {
        var x = Index();
        ClipIndexEntry E(int i) => x.Find(Clips[i].Id)!;
        Assert.Equal("Holstered walk, standing", ClipIndexView.Cell(x, Clips[4].Id, ClipSort.ReadableName));
        Assert.Equal("", ClipIndexView.Cell(x, Clips[3].Id, ClipSort.ReadableName));      // none in the list: empty
        Assert.Equal("Enemy, NPC", ClipIndexView.Cell(x, Clips[6].Id, ClipSort.Who));
        Assert.Equal("Melee and takedown, Carry and drag", ClipIndexView.Cell(x, Clips[6].Id, ClipSort.What));
        Assert.Equal("Uncategorised", ClipIndexView.Cell(x, Clips[3].Id, ClipSort.What));
        Assert.Equal("", ClipIndexView.Cell(null, Clips[4].Id, ClipSort.Who));            // no index yet
        Assert.Equal("", ClipIndexView.Cell(x, 0x77, ClipSort.Who));                       // a clip the index doesn't hold
        Assert.Equal("", ClipIndexView.Cell(x, Clips[4].Id, ClipSort.Length));
        // the clip info: each category with its source's label, in the README's words, and the reason
        Assert.Equal(["Enemy (from the tool's list)"], ClipIndexView.CategoryLines(E(0), CategoryKind.Who).Select(l => l.Text));
        var death = Assert.Single(ClipIndexView.CategoryLines(E(0), CategoryKind.What));
        Assert.Equal("Death (from the game's graph)", death.Text);
        Assert.Equal("Death-StateMachine names it: the death machine; 1 more document too", death.Reason);
        Assert.Equal(["Enemy (from the tool's list)", "NPC (from the tool's list)"], ClipIndexView.CategoryLines(E(6), CategoryKind.Who).Select(l => l.Text));
        // a user the name alone gives: "probably", as Played by says it
        Assert.Equal(["probably Player (from the name)"], ClipIndexView.CategoryLines(E(4), CategoryKind.Who).Select(l => l.Text));
        Assert.Contains("cycle", Assert.Single(ClipIndexView.CategoryLines(E(4), CategoryKind.What)).Reason);
        Assert.Equal([Uncategorised], ClipIndexView.CategoryLines(E(3), CategoryKind.What).Select(l => l.Text));
        Assert.Equal(["from the game's graph", "from the tool's list", "from the name"], new[] { FromGraph, FromToolList, FromName });
        // the graph's documents naming the clip, each name once with how many bear it, a short list
        Assert.Equal(["Death-StateMachine (2)"], ClipIndexView.DocumentLines(E(0)));
        Assert.Equal(["CQC-StateMachine", "Grab-StateMachine", "Carry-StateMachine"], ClipIndexView.DocumentLines(E(6)));
        Assert.Equal(["CQC-StateMachine", "Grab-StateMachine", "and 1 more"], ClipIndexView.DocumentLines(E(6), max: 2));
        Assert.Equal(["none"], ClipIndexView.DocumentLines(E(3)));
        // the filters' counts, every category in the vocabulary's order
        // pmc_ (clip 5) reads Enemy + NPC by its family: allied riflemen share the hostile riflemen's archetype
        Assert.Equal(ClipCategories.Who.Zip(new[] { 2, 0, 3, 3, 0, 0, 0, 1 }), ClipIndexView.Counts(x, CategoryKind.Who));
        // the clip info's played-by line: the users in one line, its first letter a capital
        Assert.Equal("Enemy and NPC", ClipIndexView.PlayedBy(E(6)));
        Assert.Equal("Probably Player", ClipIndexView.PlayedBy(E(4)));
        Assert.Equal("Played by: Enemy and NPC", ClipIndexView.PlayedByLine(x, Clips[6].Id));
        Assert.Equal("Played by: " + ClipUsers.NotKnownText, ClipIndexView.PlayedByLine(null, Clips[6].Id));
        // the Package screen's notice names the clips several kinds of character play, each on its line, those one kind only probably
        // plays, and those it can't place; none for a clip one kind plays, sure
        Assert.Null(ClipIndexView.PackageNotice(x, [("one", Clips[0].Id)]));
        Assert.Equal($"More than one kind of character plays this clip; replacing it changes it for all of them:\n  {Clips[6].Name}: Enemy and NPC\n"
            + "The tool isn't sure who plays this clip: others may play it too:\n  one: probably Player",
            ClipIndexView.PackageNotice(x, [(Clips[6].Name, Clips[6].Id), ("one", Clips[4].Id), ("sure", Clips[0].Id)]));
        // before the index: that who plays them isn't known yet, never nothing
        Assert.StartsWith("Who plays this clip isn't known yet", ClipIndexView.PackageNotice(null, [(Clips[6].Name, Clips[6].Id)]));
        var what = ClipIndexView.Counts(x, CategoryKind.What).ToDictionary(k => k.Category, k => k.Count);
        Assert.Equal(ClipCategories.What, ClipIndexView.Counts(x, CategoryKind.What).Select(k => k.Category));
        Assert.Equal((2, 1, 1, 1, 1, 1, 1, 0), (what[Locomotion], what[WeaponHandling], what[Emote], what[Death], what[MeleeAndTakedown], what[CarryAndDrag], what[Uncategorised], what[Cover]));
        // the line under the install's summary
        var t = TimeSpan.FromSeconds(0.2);
        Assert.Equal("Clip index: 7 clips, 4 with a readable name; read from the cache in 0.2 s.", ClipIndexView.Status(x, CacheOutcome.Reused, t, null));
        Assert.EndsWith("built from the game's graph in 0.2 s, and kept in the cache.", ClipIndexView.Status(x, CacheOutcome.Built, t, null));
        Assert.EndsWith("(the game files changed) in 0.2 s, but it couldn't be kept: the disk is full.", ClipIndexView.Status(x, CacheOutcome.RebuiltChanged, t, "the disk is full"));
        Assert.EndsWith("built from the game's graph in 0.2 s, for this run only (not kept).", ClipIndexView.Status(x, null, t, null));
    }

    [Fact]
    public void AListKeepsTheSelectedClipInViewWhenItStaysInTheRows()
    {
        var x = Index();
        var q = new ClipQuery { Index = x, Fields = x.SearchSource };
        var selected = Clips[5];   // pmc_rif_reload
        // a filter that keeps it: the list shows it, not its first row
        var enemy = (q with { Who = Enemy }).Apply(Clips, ClipSort.Relevance);
        Assert.NotSame(selected, enemy[0]);
        Assert.Same(selected, ClipQuery.ScrollTarget(enemy, selected));
        // a search and a sort that keep it
        Assert.Same(selected, ClipQuery.ScrollTarget((q with { Text = "pmc" }).Apply(Clips, ClipSort.Who, descending: true), selected));
        // one that hides it, or no clip selected: the first row, the best match; no rows: none
        var deaths = (q with { What = Death }).Apply(Clips, ClipSort.Relevance);
        Assert.Same(deaths[0], ClipQuery.ScrollTarget(deaths, selected));
        Assert.Same(enemy[0], ClipQuery.ScrollTarget(enemy, null));
        Assert.Null(ClipQuery.ScrollTarget([], selected));
    }

    [Fact]
    public void APictureTakesTheListsSearchAndFiltersAndARunItsIndexCache()
    {
        var a = ViewerArgs.Parse(["--game", "g", "--index-cache", "cache", "--shot", "a.png|mil_death|0.5|on|library|enemy death|enemy|DEATH",
            "--shot", "b.png|||||greting", "--shot", "c.png|x||||||Weapon handling"]);
        Assert.Equal("cache", a.IndexCache);
        Assert.True(a.Automated);
        Assert.Equal(new ShotSpec("a.png", "mil_death", 0.5f, true, "library", "enemy death", Enemy, Death), a.Shots[0]);
        Assert.Equal(new ShotSpec("b.png", null, null, null, null, "greting"), a.Shots[1]);
        Assert.Equal(new ShotSpec("c.png", "x", null, null, null, null, null, WeaponHandling), a.Shots[2]);
        Assert.Null(ViewerArgs.Parse(["--game", "g"]).IndexCache);
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--shot", "a.png|||||x|Soldier"]));        // not a who-category
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--shot", "a.png||||||Enemy|Dying"]));    // not a what-category
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--shot", "a.png|||||||Death|dark|extra"]));    // ten parts
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--index-cache", "cache"]));                                // needs --game
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--index-cache"]));
    }
}

/// <summary>The clip index on the vanilla install in the search and the screens' texts: the example searches (greting, enemy death,
/// holstered walk), the clip info of known clips, and the time per keystroke with the index's fields.</summary>
[Collection("install")]
public sealed class IndexSearchGameTests(InstallFixture f, ITestOutputHelper output)
{
    ClipIndex X => f.Index;

    ClipIndexEntry E(ClipEntry c) => X.Find(c.Id)!;

    List<ClipEntry> Search(string text)
    {
        var r = new ClipQuery { Text = text, Index = X, Fields = X.SearchSource }.Apply(f.Install.Clips, ClipSort.Relevance);
        output.WriteLine($"'{text}': {r.Count} found; first {string.Join(", ", r.Take(5).Select(c => $"{c.Name} ({E(c).ReadableName ?? E(c).NamesText(CategoryKind.Who) + " / " + E(c).NamesText(CategoryKind.What)})"))}");
        return r;
    }

    [GameDataFact]
    public void GretingFindsTheGreetingClipsByTheirReadableNames()
    {
        // no vanilla clip's name holds "greet": the greeting clips are found through their readable names, the typo forgiven
        Assert.DoesNotContain(f.Install.Clips, c => c.Name.Contains("greet", StringComparison.OrdinalIgnoreCase));
        var greeting = X.Entries.Where(e => e.ReadableName is { } n && ClipSearch.Words(n).Contains("greeting")).Select(e => e.ClipName).Order().ToList();
        Assert.Equal(7, greeting.Count);
        // exactly the seven, the greeting emote first: no other clip comes in
        var r = Search("greting");
        Assert.Equal(greeting, r.Select(c => c.Name).Order());
        Assert.Equal("mca_emote_Hello", r[0].Name);
    }

    bool NameHolds(ClipEntry c, string word) =>
        ClipSearch.Words(c.Name).Contains(word) || (E(c).ReadableName is { } n && ClipSearch.Words(n).Contains(word));

    /// <summary>The whole result's clips outside the category asked for, written out.</summary>
    List<ClipEntry> Outside(List<ClipEntry> r, string what)
    {
        var off = r.Where(c => !E(c).In(CategoryKind.What, what)).ToList();
        output.WriteLine($"   {off.Count} of {r.Count} not {what}: {string.Join(", ", off.Select(c => $"{c.Name} [{E(c).NamesText(CategoryKind.What)}]"))}");
        return off;
    }

    [GameDataFact]
    public void TeammatesFindsTheTeammatesClipsByTheirCategory()
    {
        var teammates = X.Entries.Where(e => e.In(CategoryKind.Who, Teammates)).Select(e => e.ClipId).ToHashSet();
        Assert.Equal(3_677, teammates.Count);
        var r = Search("teammates");
        // every clip the teammates play is found by its Who, and nothing else but the teammates' family's (its glossary: AI teammates)
        Assert.Subset(r.Select(c => c.Id).ToHashSet(), teammates);
        Assert.All(r, c => Assert.True(E(c).In(CategoryKind.Who, Teammates) || E(c).Explained.Any(g => g.Abbreviation == "tm8"), c.Name));
        // the word's start finds the same clips
        Assert.Equal(r.Select(c => c.Id).ToHashSet(), Search("teammate").Select(c => c.Id).ToHashSet());
        // with the What filter, the teammates' deaths
        Assert.Equal(X.Entries.Count(e => e.In(CategoryKind.Who, Teammates) && e.In(CategoryKind.What, Death)),
            new ClipQuery { Index = X, Who = Teammates, What = Death }.Apply(f.Install.Clips).Count);
    }

    [GameDataFact]
    public void EnemyDeathFindsTheEnemiesDeaths()
    {
        var deaths = X.Entries.Where(e => e.In(CategoryKind.Who, Enemy) && e.In(CategoryKind.What, Death)).Select(e => e.ClipId).ToHashSet();
        var r = Search("enemy death");
        // every enemy death is found, and the result is the enemies' clips whose name says death or whose what is Death; a glossary
        // meaning's explanation ("their shared deaths, hits and cover moves") brings none in
        Assert.Subset(r.Select(c => c.Id).ToHashSet(), deaths);
        Assert.All(r, c => Assert.True(E(c).In(CategoryKind.Who, Enemy), c.Name));
        Assert.All(r, c => Assert.True(E(c).In(CategoryKind.What, Death) || NameHolds(c, "death"), c.Name));
        Assert.Equal(234, r.Count);
        // the 6 outside Death die in a vehicle, at a pintle gun or in a skydive's fall: their names say death
        var off = Outside(r, Death);
        Assert.Equal(6, off.Count);
        Assert.All(off, c => Assert.True(NameHolds(c, "death"), c.Name));
        // the enemies' deaths whose names say death come first, every one, before the 6 whose names alone do: the Death category
        // adds its bonus to the name's match
        Assert.All(r.Take(20), c => Assert.True(E(c).In(CategoryKind.What, Death), c.Name));
        int firstOff = r.FindIndex(c => !E(c).In(CategoryKind.What, Death));
        Assert.Equal(X.Entries.Count(e => deaths.Contains(e.ClipId) && NameHolds(f.Install.FindClip(e.ClipName)!, "death")), firstOff);
        Assert.Equal(227, firstOff);
        // the two filters give the enemy deaths, every one
        Assert.Equal(deaths, new ClipQuery { Index = X, Who = Enemy, What = Death }.Apply(f.Install.Clips).Select(c => c.Id).ToHashSet());
        Assert.Equal(228, deaths.Count);
    }

    [GameDataFact]
    public void SoldierDeathFindsTheSoldiersDeaths()
    {
        // "soldier" is the start of mil's "soldiers" and a word of the soldiers' readable names
        var r = Search("soldier death");
        Assert.Equal(230, r.Count);
        Assert.All(r, c => Assert.True(NameHolds(c, "death"), c.Name));
        var off = Outside(r, Death);
        Assert.Equal(3, off.Count);
        Assert.All(r, c => Assert.True(E(c).In(CategoryKind.Who, Enemy), c.Name));
    }

    [GameDataFact]
    public void HitFindsTheHitReactions()
    {
        var r = Search("hit");
        // the hit reactions and the clips whose name says hit (a downed character hit, a face's reaction, a close-combat hit), no
        // heavy or light kind of death brought in by a meaning's explanation
        // (a word of the name or the readable name starting so: Hit01, "hitting the ground")
        bool Starts(ClipEntry c) => ClipSearch.Words(c.Name + " " + E(c).ReadableName).Any(w => w.StartsWith("hit", StringComparison.Ordinal));
        Assert.All(r, c => Assert.True(E(c).In(CategoryKind.What, HitReaction) || Starts(c), c.Name));
        Assert.Equal(311, r.Count);
        Assert.Equal(46, Outside(r, HitReaction).Count);
        Assert.DoesNotContain(r, c => E(c).In(CategoryKind.What, Death) && !Starts(c));
    }

    [GameDataFact]
    public void ShortQueriesFindTheirFewClips()
    {
        // the greeting emote alone, by its name's words; no "fm" expansion ("free move") or readable name matched across its words
        Assert.Equal(["mca_emote_Hello"], Search("emo hel").Select(c => c.Name));
        Assert.Equal(["mca_emote_Hello"], Search("hello emote").Select(c => c.Name));
        // the head-back deaths, and nothing the "regular" meaning's "in deaths" would bring
        var back = Search("death head back");
        Assert.Equal(8, back.Count);
        Assert.All(back, c => Assert.True(E(c).In(CategoryKind.What, Death) && c.Name.Contains("_head_back_", StringComparison.Ordinal), c.Name));
    }

    [GameDataFact]
    public void HolsteredWalkFindsTheHolsteredWalk()
    {
        // exactly the holstered walk's own clips, by their readable names (no name holds "holstered", and nwp's meaning is "no weapon")
        var r = Search("holstered walk");
        Assert.Equal(17, r.Count);
        Assert.All(r, c => Assert.StartsWith("Holstered walk", E(c).ReadableName));
        Assert.All(r, c => Assert.True(E(c).In(CategoryKind.What, Locomotion), c.Name));
        Assert.Contains("mcm_nwp_fm_std_V1_cycle", r.Take(2).Select(c => c.Name));
        Assert.Equal("Holstered walk, standing", E(f.Install.FindClip("mcm_nwp_fm_std_V1_cycle")!).ReadableName);
        // the readable name's two words typed together, or the walk as "walking": the same clips
        Assert.Equal(r.Select(c => c.Name).Order(), Search("holsteredwalk").Select(c => c.Name).Order());
        Assert.Equal(r.Select(c => c.Name).Order(), Search("holstered walking").Select(c => c.Name).Order());
    }

    [GameDataTheory]
    [InlineData("death", Death)]
    [InlineData("hit", HitReaction)]
    [InlineData("idle", Idle)]
    [InlineData("deth", Death)]    // a typo: the clips whose category also holds the word meant first
    [InlineData("idel", Idle)]
    public void AWordsOwnCategoryComesFirst(string query, string category)
    {
        // among the clips whose names say the word alike, those whose category is the word come first: no death-threat emote, face
        // reaction, vent idle or chance run across a name's words (grena-de_th-row, outs-ide_l) before them
        var r = Search(query);
        Assert.All(r.Take(20), c => Assert.True(E(c).In(CategoryKind.What, category), c.Name));
        int firstOff = r.FindIndex(c => !E(c).In(CategoryKind.What, category));
        output.WriteLine($"   first outside {category} at rank {firstOff + 1}: {r[firstOff].Name}");
        Assert.True(firstOff >= 100, $"{firstOff}");
        if (query is "death" or "deth")
            Assert.True(r.FindIndex(c => c.Name == "mca_emote_Death_Treat") >= firstOff);
    }

    /// <summary>The clips a glossary entry explains, by a direct scan of the index.</summary>
    HashSet<ulong> Explained(string abbr) => X.Entries.Where(e => e.Explained.Any(g => g.Abbreviation == abbr)).Select(e => e.ClipId).ToHashSet();

    [GameDataFact]
    public void AnAbbreviationsSearchWordsReachItsClips()
    {
        // entries whose meaning's head is too long to search give their own words: each finds every clip the entry explains
        foreach (var (abbr, words) in new[] { ("get", "draw"), ("stow", "put away"), ("LHS", "left shoulder"), ("RHS", "right shoulder"), ("NE", "north-east"),
            ("NW", "north west"), ("SE", "south-east"), ("SW", "south west"), ("grab", "seize"), ("Vent", "crawl"), ("pmc", "soldiers"), ("foregrip", "front grip") })
        {
            var scan = Explained(abbr);
            var found = Search(words).Select(c => c.Id).ToHashSet();
            Assert.True(scan.Count > 0, abbr);
            Assert.Subset(found, scan);
        }
        // whole results: draw and put away are the draw and stow clips exactly; shoulder holds both shoulders' clips
        Assert.Equal(Explained("get"), Search("draw").Select(c => c.Id).ToHashSet());
        Assert.Equal(137, Explained("get").Count);
        Assert.Equal(Explained("stow"), Search("put away").Select(c => c.Id).ToHashSet());
        Assert.Equal(107, Explained("stow").Count);
        // left shoulder: the left-shoulder clips exactly, as the clip info's "weapon on the left shoulder" and the readable names say
        Assert.Equal(Explained("LHS"), Search("left shoulder").Select(c => c.Id).ToHashSet());
        Assert.Equal(368, Explained("LHS").Count);
        Assert.Subset(Search("right shoulder").Select(c => c.Id).ToHashSet(), Explained("RHS"));
        Assert.Subset(Search("shoulder").Select(c => c.Id).ToHashSet(), Explained("LHS").Union(Explained("RHS")).ToHashSet());
        Assert.Equal(Explained("NE"), Search("northeast").Select(c => c.Id).ToHashSet());   // the compass word typed whole
        // and no flood back: the example searches keep their results
        Assert.Equal(234, Search("enemy death").Count);
        Assert.Equal(["mca_emote_Hello"], Search("emo hel").Select(c => c.Name));
    }

    [GameDataFact]
    public void TheShouldersSearchWordsBringNoSideIn()
    {
        // LHS marks a weapon on the left shoulder, not a move to the left: its search words ("left shoulder") are a phrase whose side
        // word counts only beside shoulder, so the direction words keep the counts they had before the glossary's search words
        bool Lhs(ClipEntry c) => NameRules.Words(c.Name).Contains("lhs");
        bool NameSays(ClipEntry c, string w) => ClipSearch.Words(c.Name + " " + E(c).ReadableName).Contains(w);
        Assert.Equal(869, Search("left").Count);
        Assert.Equal(54, Search("aim left").Count);
        Assert.Equal(15, Search("lean left").Count);
        Assert.Empty(Search("peek left"));
        // a left-shoulder clip is found by "left" only where its own name, its readable name or another part's meaning (TurnL's "turn
        // left") says left
        bool SaysLeft(ClipEntry c) => ClipSearch.Words(string.Join(" ", new[] { c.Name, E(c).ReadableName }
            .Concat(E(c).Explained.Where(g => g.Abbreviation != "LHS").Select(ClipIndexEntry.SearchText)))).Any(w => w.StartsWith("left", StringComparison.Ordinal));
        Assert.All(Search("left").Where(Lhs), c => Assert.True(SaysLeft(c), c.Name));
        // no right turn in "turn left" but those whose own names say left (a slope's side, a facing): none by a shoulder
        var turnLeft = Search("turn left");
        Assert.Equal(361, turnLeft.Count);
        Assert.All(turnLeft.Where(c => c.Name.Contains("TurnR", StringComparison.OrdinalIgnoreCase)), c => Assert.True(NameSays(c, "left"), c.Name));
        // "right shoulder" holds no left-shoulder clip: "right" is the leading word of RHS's phrase, not of LHS's, so a name's
        // "LookRight" and LHS's "shoulder" make no match; the right-shoulder clips, and those whose readable names say so, stay
        var right = Search("right shoulder");
        Assert.DoesNotContain(right, Lhs);
        Assert.Equal(31, right.Count);
        Assert.All(right.Where(c => !Explained("RHS").Contains(c.Id)), c => Assert.EndsWith("right shoulder", E(c).ReadableName));
        // and "left shoulder" no right-shoulder clip
        bool Rhs(ClipEntry c) => NameRules.Words(c.Name).Contains("rhs");
        Assert.DoesNotContain(Search("left shoulder"), Rhs);
        // the other side stands against a shoulder only as a whole word: typed so far, it keeps both shoulders' clips, so the list
        // doesn't shrink and regrow on the way to "shoulder rifle"
        HashSet<ulong> Ids(string q) => Search(q).Select(c => c.Id).ToHashSet();
        var lhs = Explained("LHS"); var rhs = Explained("RHS");
        Assert.Equal(210, Ids("shoulder ri").Intersect(lhs).Count());
        Assert.Subset(Ids("shoulder ri"), Ids("shoulder rif"));
        Assert.Equal(Ids("shoulder rif"), Ids("shoulder rifle"));
        Assert.Empty(Ids("shoulder right").Intersect(lhs));
        Assert.Equal(14, Ids("shoulder le").Intersect(rhs).Count());
        Assert.Empty(Ids("shoulder left").Intersect(rhs));
        // one word still reaches both shoulders' clips
        Assert.Subset(Search("shoulder").Select(c => c.Id).ToHashSet(), Explained("LHS").Union(Explained("RHS")).ToHashSet());
    }

    [GameDataFact]
    public void AnAbbreviationsLastSearchWordFindsItBesideAnyWord()
    {
        HashSet<ulong> Ids(string q) => Search(q).Select(c => c.Id).ToHashSet();
        // the last word of a phrase counts alone: beside a word of the clip's own name it finds what the abbreviation does
        Assert.Equal(119, Ids("lhs aim").Count);
        Assert.Equal(Ids("lhs aim"), Ids("shoulder aim"));
        Assert.Equal(Ids("lhs aim"), Ids("aim shoulder"));
        Assert.Equal(Ids("lhs aim"), Ids("left shoulder aim"));
        Assert.Equal(Ids("lhs walk"), Ids("shoulder walk"));
        // the vent crawl's idle, enter and exit by its "crawl"
        Assert.Equal(["MCA_Vent_Idle"], Search("vent idle").Select(c => c.Name));
        Assert.Equal(Ids("vent idle"), Ids("crawl idle"));
        Assert.Equal(Ids("vent exit"), Ids("crawl exit"));
        // the four entries into a vent, by the search again with typos (enter for entry; "vent enter" finds the conversations' enters
        // as written besides)
        Assert.Equal(4, Search("crawl enter").Count);
        Assert.All(Search("crawl enter"), c => Assert.Contains("_Vent_", c.Name));
        Assert.Subset(Ids("vent enter"), Ids("crawl enter"));
        // a phrase's two words typed together, beside another word
        Assert.Equal(75, Ids("walk north-east").Count);
        Assert.Equal(Ids("walk north-east"), Ids("walk northeast"));
        Assert.Equal(Ids("walk north-east"), Ids("northeast walk"));
        Assert.Equal(Ids("lhs aim"), Ids("leftshoulder aim"));
        // the compass: each word a direction, alone or beside another word: north finds the diagonals ahead (NE, NW), south those
        // behind (SE, SW); the straight N and S aren't settled, so besides them only the clips whose own words say north
        var ne = Explained("NE"); var nw = Explained("NW"); var se = Explained("SE"); var sw = Explained("SW");
        Assert.Superset(ne.Union(nw).ToHashSet(), Ids("north"));
        Assert.All(Search("north").Where(c => !ne.Contains(c.Id) && !nw.Contains(c.Id)),
            c => Assert.Contains("north", ClipSearch.Words(c.Name + " " + E(c).ReadableName)));
        Assert.Superset(se.Union(sw).ToHashSet(), Ids("south"));
        Assert.Equal(156, Ids("walk north").Count);
        Assert.Equal(Ids("walk north"), Ids("north walk"));
        Assert.Subset(Ids("north"), Ids("walk north"));
        Assert.Equal(Ids("walk north"), Ids("walk nort"));   // as typed so far
        Assert.NotEmpty(Search("run north"));
        Assert.NotEmpty(Search("walk south"));
        // two compass words find their diagonal only, the diagonals that share a word standing against each other: "north east" no
        // clip of NW or SE alone, "south east" no clip of NE alone
        Assert.Equal(ne, Ids("north east"));
        Assert.Equal(ne, Ids("north-east"));
        Assert.Equal(ne, Ids("east north"));
        Assert.Equal(se, Ids("south east"));
        Assert.DoesNotContain(Search("south east"), c => ne.Contains(c.Id) && !se.Contains(c.Id));
        Assert.DoesNotContain(Search("north east"), c => se.Contains(c.Id) && !ne.Contains(c.Id));
        Assert.Equal(nw, Ids("north west"));
        // east and west find the diagonals alike, alone and beside a word
        Assert.Subset(Ids("east"), Explained("NE").Union(Explained("SE")).ToHashSet());
        Assert.Subset(Ids("walk east"), Ids("walk north-east"));
        Assert.Subset(Ids("east"), Ids("walk east"));
    }

    [GameDataFact]
    public void TheClipInfoOfKnownClips()
    {
        IReadOnlyList<string> Lines(string clip, CategoryKind kind) => ClipIndexView.CategoryLines(E(f.Install.FindClip(clip)!), kind).Select(l => l.Text).ToList();
        // the graph's, the tool's list's and the name's, each labelled as the README says
        // a who of the graph names the part of the graph that decided it: the layer, in plain words with its root's name
        const string PlayerLayer = "the player's layer (MCH-LayerGraph)", MenuLayer = "the menus' character's layer (Avatar-LayerGraph)",
            TeammatesLayer = "the AI teammates' layer (SPE_TM8-LayerGraph)", NpcLayer = "the generic NPC layer (GEN_NPC-LayerGraph)";
        Assert.Equal([$"Player, from the game's graph: {PlayerLayer}"], Lines("mcm_nwp_fm_std_V1_cycle", CategoryKind.Who));
        // a lobby pose: only the menus' character's layer names it, so its who says so and its what is Menu and lobby
        Assert.Equal([$"Player, from the game's graph: {MenuLayer}"], Lines("Lobby_Front_AR_02", CategoryKind.Who));
        Assert.Equal(["Menu and lobby (from the game's graph)"], Lines("Lobby_Front_AR_02", CategoryKind.What));
        // a character-screen idle keeps the list's Idle, with Menu and lobby beside it; a menu-only emote keeps its name's Emote
        Assert.Equal(["Idle (from the tool's list)", "Menu and lobby (from the game's graph)"], Lines("mca_CharacterSmith_Idle_AR01", CategoryKind.What));
        Assert.Equal(["Emote (from the name)", "Menu and lobby (from the game's graph)"], Lines("mca_emote_ClearedHot", CategoryKind.What));
        // the wheel's greeting, which the menus play too: the layer played in the world first, and still an emote alone
        Assert.Equal([$"Player, from the game's graph: {PlayerLayer}; {MenuLayer}"], Lines("mca_emote_Hello", CategoryKind.Who));
        Assert.Equal(["Emote (from the game's graph)"], Lines("mca_emote_Hello", CategoryKind.What));
        // the face's idle: the shared face machine reaches it from the player's, the teammates' and the generic NPC layers (each "by a
        // shared machine"), the menus' layer names it itself; kinds with the same part on one line
        Assert.Equal([$"Player, from the game's graph: {PlayerLayer}, by a shared machine; {MenuLayer}",
            $"probably Teammates, from the game's graph: {TeammatesLayer}, by a shared machine",
            $"probably Enemy and NPC, from the game's graph: {NpcLayer}, by a shared machine"], Lines("Mch_Face_Idle_Neutral01", CategoryKind.Who));
        Assert.Equal(["Locomotion (from the game's graph)"], Lines("mcm_nwp_fm_std_V1_cycle", CategoryKind.What));
        Assert.Equal(["Ground-StateMachine"], ClipIndexView.DocumentLines(E(f.Install.FindClip("mcm_nwp_fm_std_V1_cycle")!)));
        Assert.Equal(["Player (from the tool's list)"], Lines("Mca_Std_Healthy_VictoryPose_MVP", CategoryKind.Who));
        Assert.Equal(["Pose (from the tool's list)", "Menu and lobby (from the tool's list)"], Lines("Mca_Std_Healthy_VictoryPose_MVP", CategoryKind.What));
        Assert.Equal(["probably Player (from the name)"], Lines("mch_flymode_superman", CategoryKind.Who));
        Assert.Equal([Uncategorised], Lines("mch_flymode_superman", CategoryKind.What));
        Assert.Equal([$"Teammates, from the game's graph: {TeammatesLayer}", $"Enemy and NPC, from the game's graph: {NpcLayer}"],
            Lines("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01", CategoryKind.Who));
        Assert.Equal("Teammates, Enemy and NPC", ClipIndexView.PlayedBy(E(f.Install.FindClip("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01")!)));
        // the face's idle: the menu character's layer (the player's, sure) and the shared custom-face machine (the rest, probably)
        Assert.Equal("Player; probably also Teammates, Enemy and NPC", ClipIndexView.PlayedBy(E(f.Install.FindClip("Mch_Face_Idle_Neutral01")!)));
        var most = X.Entries.MaxBy(e => e.Documents.Select(d => d.Name).Distinct().Count())!;
        output.WriteLine($"the most documents naming one clip: {most.ClipName}, {string.Join("; ", ClipIndexView.DocumentLines(most))}");
        // every readable name shows in its column, and only those
        Assert.Equal(X.Entries.Count(e => e.ReadableName is not null), f.Install.Clips.Count(c => ClipIndexView.Cell(X, c.Id, ClipSort.ReadableName).Length > 0));
    }

    [GameDataFact]
    public void TypingStaysInstantWithTheIndexsFields()
    {
        var g = f.Install;
        long t0 = Stopwatch.GetTimestamp();
        var built = new ClipSearch(g.Clips, X.SearchSource);
        double buildMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        output.WriteLine(FormattableString.Invariant(
            $"built with the index's fields over {built.Clips.Count:N0} clips ({built.VocabularySize:N0} distinct words; {new ClipSearch(g.Clips).VocabularySize:N0} by the names alone) in {buildMs:F1} ms"));
        string[] queries = ["greting", "enemy death", "holstered walk", "hello emote", "emo hel", "death head back", "0x000001A625068E05", "sprint fwd",
            "reload rifle", "cover left peek", "hlelo"];
        var times = SearchTests.Keystrokes(g.Clips, queries, X.SearchSource);
        output.WriteLine($"vanilla with the index's fields, best match first: {SearchTests.Summary(times)}");
        // with both category filters, and sorted by an index column, as the Library runs them when chosen
        var filtered = new List<double>();
        foreach (var q in new[] { "death head back", "greting" })
            for (int n = 1; n <= q.Length; n++)
            {
                long s0 = Stopwatch.GetTimestamp();
                new ClipQuery { Text = q[..n], Index = X, Fields = X.SearchSource, Who = Enemy, What = Death }.Apply(g.Clips, ClipSort.Relevance);
                new ClipQuery { Text = q[..n], Index = X, Fields = X.SearchSource, Status = StatusFilter.Vanilla }.Apply(g.Clips, ClipSort.What);
                filtered.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds / 2);
            }
        output.WriteLine($"vanilla with the index's fields, filtered by category or sorted by what: {SearchTests.Summary(filtered)}");
        Assert.Same(ClipSearch.For(g.Clips, X.SearchSource), ClipSearch.For(g.Clips, X.SearchSource));
        Assert.True(times.Average() < 20, SearchTests.Summary(times));
        Assert.True(filtered.Average() < 20, SearchTests.Summary(filtered));
        Assert.True(times.Concat(filtered).Max() < 250, "worst keystroke: " + SearchTests.Summary(times.Concat(filtered).ToList()));
    }
}
