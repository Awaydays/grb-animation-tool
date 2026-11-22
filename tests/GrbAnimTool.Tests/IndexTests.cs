using System.Text;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Library;
using static GrbAnimTool.Core.Index.ClipCategories;

namespace GrbAnimTool.Tests;

/// <summary>The clip index on made-up graph facts, lists and names: the rule table, the precedence of the sources, the list's reader,
/// the name rules, the search fields and the cache file's round trip.</summary>
public sealed class IndexUnitTests
{
    const ulong Player1 = 0x000001544EA877D5, Menu = 0x000001555E68546B, Generic = 0x000001544EA9A050, TeammatesLayer = 0x000001A2B8D96B35;

    /// <summary>A small graph in the game's shape: GR_Graph, the general machine with the player's, the menu's and the NPC machine's
    /// layers, the generic layer's Military and Civilian split, the teammates' layer, two reference machines (one played by reference
    /// from the player's and the generic layer, one by none) and a document no rule knows.</summary>
    static readonly (ulong Id, string Name, int[] Children, int[] References)[] Docs =
    [
        (0x000001555E68541F, "GR_Graph", [1, 2], []),                        // 0
        (0x10, "General-StateMachine", [3, 4, 5], []),                       // 1
        (0x11, "AnimationComponent-ReferenceStateMachines", [12, 13], []),   // 2
        (Player1, "MCH-LayerGraph", [6], [12]),                              // 3
        (GraphRules.NpcMachineId, "NPC-StateMachine", [7, 8, 14], []),       // 4
        (Menu, "Avatar-LayerGraph", [], []),                                 // 5
        (0x13, "Ground-StateMachine", [9, 22], []),                          // 6
        (Generic, "GEN_NPC-LayerGraph", [10], [12]),                         // 7
        (TeammatesLayer, "SPE_TM8-LayerGraph", [11], []),                    // 8
        (0x14, "Emotes-LayerGraph", [], []),                                 // 9
        (0x15, "Ground-StateMachine", [15, 16, 17, 18], []),                 // 10
        (0x16, "Death-StateMachine", [], []),                                // 11 the teammates' deaths
        (0x17, "ReferenceStateMachines-CQC", [], []),                        // 12
        (0x18, "ReferenceStateMachines-Swimming", [], []),                   // 13 no layer plays it
        (0x000001C6F837BC51, "SPE_NPC-LayerGraph", [], []),                  // 14 no branch
        (0x19, "Death-StateMachine", [], []),                                // 15 the generic layer's deaths
        (0x1A, "Military-StateMachine", [19, 20], []),                       // 16
        (0x1B, "Civilian-StateMachine", [], []),                             // 17
        (0x1C, "Cover-LayerGraph", [21], []),                                // 18
        (0x1D, "Rifleman_FullBody-StateMachine", [], []),                    // 19
        (0x1E, "SamFisher_Fullbody-StateMachine", [], []),                   // 20
        (0x1F, "Idle-LayerGraph", [], []),                                   // 21 an idle machine in a cover layer
        (0x20, "Mystery-StateMachine", [], []),                              // 22 no rule
    ];

    static GraphFacts Facts(Dictionary<ulong, (int[] Docs, int[] Fields)> clips, (ulong, string, int[], int[])[]? docs = null) => new()
    {
        Documents = (docs ?? Docs).Select(d => new IndexDocument(d.Item1, d.Item2, d.Item3, d.Item4, 0)).ToList(),
        Clips = clips.ToDictionary(k => k.Key, k => new ClipGraphFacts(k.Value.Docs, k.Value.Fields)),
        Failures = [], ClipsLookedFor = clips.Count,
    };

    static readonly Dictionary<ulong, (int[], int[])> Uses = new()
    {
        [0xA] = ([9], []), [0xB] = ([11, 15], []), [0xC] = ([19], []), [0xD] = ([20], []), [0xE] = ([17], []), [0xF] = ([21], []),
        [0x10] = ([12], []), [0x11] = ([13], []), [0x12] = ([22], [22]), [0x13] = ([14], []), [0x14] = ([5], []),
    };

    static ClipIndex Index(ClipIndexList? list = null, params (ulong, string)[] extra) =>
        ClipIndex.Build(Uses.Keys.Select(id => (id, $"clip_{id:X}")).Concat(extra), Facts(Uses), list);

    static void Is(ClipIndexEntry e, CategoryKind kind, CategorySource? source, params string[] names)
    {
        Assert.Equal(names.Length == 0 ? [Uncategorised] : names, e.Names(kind));
        Assert.Equal(source, e.SourceOf(kind));
        Assert.All(e.Of(kind), c => Assert.Equal(source, c.Source));
    }

    [Fact]
    public void TheBranchesPlaceWho()
    {
        var x = Index();
        Is(x.Find(0xA)!, CategoryKind.Who, CategorySource.Graph, Player);
        Is(x.Find(0xA)!, CategoryKind.What, CategorySource.Graph, Emote);
        // the generic layer's deaths and the teammates' copy: the generic layer is shared by soldiers and civilians, the teammates'
        // copy gives the teammates (never NPC); each from a document of one layer alone, so none is only probable
        Is(x.Find(0xB)!, CategoryKind.Who, CategorySource.Graph, Teammates, Enemy, Npc);
        Is(x.Find(0xB)!, CategoryKind.What, CategorySource.Graph, Death);
        Assert.Equal(2, x.Find(0xB)!.Documents.Count);
        Assert.Empty(x.Find(0xB)!.WhoProbably);
        Assert.Equal("Teammates, Enemy and NPC", ClipUsers.Of(x, 0xB).Text);
        // the teammates' deaths alone are the teammates' alone
        var tm = ClipIndex.Build([(0x40, "clip_40")], Facts(new() { [0x40] = ([11], []) }), null).Find(0x40)!;
        Is(tm, CategoryKind.Who, CategorySource.Graph, Teammates);
        Assert.Contains("the AI teammates' layer (SPE_TM8-LayerGraph)", tm.Who[0].Reason);
        // the Military machine keeps both (allied soldiers share its clips), the Civilian one narrows to the NPCs, and the machine named
        // for the ally within Military to the NPCs again
        Is(x.Find(0xC)!, CategoryKind.Who, CategorySource.Graph, Enemy, Npc);
        Is(x.Find(0xD)!, CategoryKind.Who, CategorySource.Graph, Npc);
        Is(x.Find(0xE)!, CategoryKind.Who, CategorySource.Graph, Npc);
        Assert.Contains("Military-StateMachine", x.Find(0xC)!.Who[0].Reason);
        Assert.Contains("GEN_NPC-LayerGraph", x.Find(0xC)!.Who[0].Reason);
        // the menu's character is the player's own
        Is(x.Find(0x14)!, CategoryKind.Who, CategorySource.Graph, Player);
    }

    [Fact]
    public void AReferenceMachineTakesWhoFromEveryLayerThatPlaysIt()
    {
        var x = Index();
        Is(x.Find(0x10)!, CategoryKind.Who, CategorySource.Graph, Player, Enemy, Npc);
        Is(x.Find(0x10)!, CategoryKind.What, CategorySource.Graph, MeleeAndTakedown);
        // which of those layers plays which of its states isn't read: each only probably plays it
        Assert.Equal([Player, Enemy, Npc], x.Find(0x10)!.WhoProbably);
        Assert.Equal("probably Player, Enemy and NPC", ClipUsers.Of(x, 0x10).Text);
        Assert.True(x.DocumentPlacements[12].Shared);
        Assert.False(x.DocumentPlacements[11].Shared);
        // a reference machine no layer plays says nothing of who; its name still says what
        Is(x.Find(0x11)!, CategoryKind.Who, null);
        Is(x.Find(0x11)!, CategoryKind.What, CategorySource.Graph, Swimming);
        // the special NPCs' layer is no branch, and a document no rule knows gives nothing
        Is(x.Find(0x13)!, CategoryKind.Who, null);
        Is(x.Find(0x13)!, CategoryKind.What, null);
    }

    [Fact]
    public void InheritedRulesGoDownTheTailsAndAddToTheOwnRules()
    {
        var x = Index();
        // an idle machine inside a cover layer: its own Idle and the layer's Cover
        Is(x.Find(0xF)!, CategoryKind.What, CategorySource.Graph, Idle, Cover);
        Assert.Contains(x.Find(0xF)!.What, c => c.Name == Cover && c.Reason.Contains("Cover-LayerGraph holds it"));
        Is(x.Find(0xF)!, CategoryKind.Who, CategorySource.Graph, Enemy, Npc);
    }

    [Fact]
    public void AMotionFieldIsLocomotion()
    {
        var e = Index().Find(0x12)!;
        Is(e, CategoryKind.What, CategorySource.Graph, Locomotion);
        Assert.Equal($"{GraphRules.MotionFieldReason} (Mystery-StateMachine)", e.What[0].Reason);
        Is(e, CategoryKind.Who, CategorySource.Graph, Player);
    }

    [Fact]
    public void ABranchRootUnderAnotherNameFiresNothing()
    {
        var docs = Docs.ToArray();
        docs[8] = (TeammatesLayer, "Other-LayerGraph", [11], []);
        var x = ClipIndex.Build([(0xB, "clip_B"), (0x30, "clip_30")], Facts(new() { [0xB] = ([11], []), [0x30] = ([8], []) }, docs), null);
        Is(x.Find(0xB)!, CategoryKind.Who, null);
        Is(x.Find(0xB)!, CategoryKind.What, CategorySource.Graph, Death);
    }

    const string ListJson = """
        { "format": 1, "about": "test", "glossary": [ { "abbr": "swm", "meaning": "swimming", "sure": true, "note": "n" } ],
          "clips": [
            { "id": "0x000000000000000A", "name": "Wave", "who": ["Enemy"], "what": ["Death"], "sure": true },
            { "id": "0x0000000000000011", "name": "Swim", "who": ["Animal"], "what": ["Death"], "sure": true },
            { "id": "0x0000000000000013", "what": ["Interaction"], "sure": true },
            { "id": "0x0000000000000040", "name": "Unsure", "who": ["Enemy"], "what": ["Emote"], "sure": false }
          ] }
        """;

    static ClipIndexList List(string json) => ClipIndexList.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ABetterSourceWinsPerKindAndAWorseOneOnlyFillsAnEmptyKind()
    {
        var x = Index(List(ListJson), (0x40, "mca_wave_death_01"), (0x41, "nothing_at_all"));
        // the graph places both kinds: the list's categories aren't used, its readable name is
        var a = x.Find(0xA)!;
        Is(a, CategoryKind.Who, CategorySource.Graph, Player);
        Is(a, CategoryKind.What, CategorySource.Graph, Emote);
        Assert.Equal("Wave", a.ReadableName);
        // the graph places what only: the list fills who, each category with its own source
        var h = x.Find(0x11)!;
        Is(h, CategoryKind.Who, CategorySource.ToolList, Animal);
        Is(h, CategoryKind.What, CategorySource.Graph, Swimming);
        Assert.Equal(FromToolList, h.Who[0].SourceLabel);
        Assert.Equal(FromGraph, h.What[0].SourceLabel);
        // the graph places nothing: the list fills what, the name nothing, who stays uncategorised
        var j = x.Find(0x13)!;
        Is(j, CategoryKind.What, CategorySource.ToolList, Interaction);
        Is(j, CategoryKind.Who, null);
        Assert.True(j.IsUncategorised(CategoryKind.Who));
        Assert.True(j.In(CategoryKind.Who, Uncategorised));
        // an unsure entry is left out whole: its name and categories; the clip's name places it
        var u = x.Find(0x40)!;
        Assert.Null(u.ReadableName);
        Is(u, CategoryKind.Who, CategorySource.Name, Player);
        Is(u, CategoryKind.What, CategorySource.Name, Death);
        Assert.Equal(FromName, u.What[0].SourceLabel);
        Is(x.Find(0x41)!, CategoryKind.Who, null);
        Is(x.Find(0x41)!, CategoryKind.What, null);
        // the census
        var c = x.Census();
        Assert.Equal(Uses.Count + 2, c.Clips);
        Assert.Equal(2, c.ReadableNames);
        Assert.Equal(1, c.Decided(CategoryKind.Who, CategorySource.ToolList));
        Assert.Equal(1, c.Decided(CategoryKind.What, CategorySource.ToolList));
        Assert.Equal(1, c.Decided(CategoryKind.Who, CategorySource.Name));
        Assert.Equal(c.Clips, c.Decided(CategoryKind.Who, CategorySource.Graph) + c.Decided(CategoryKind.Who, CategorySource.ToolList)
            + c.Decided(CategoryKind.Who, CategorySource.Name) + c.Uncategorised(CategoryKind.Who));
        Assert.Equal(1, c.ListUnsure);
    }

    [Fact]
    public void TheListsReaderTakesTheFormAndLeavesOutWhatBreaksIt()
    {
        using var s = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Index", "clip-index-fixture.json"));
        var l = ClipIndexList.Read(s);
        Assert.Null(l.Error);
        Assert.Equal(["regular", "swm", "tm8"], l.Glossary.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, l.UnsureGlossary);
        Assert.True(l.Glossary.ContainsKey("SWM"));   // case ignored
        Assert.Equal([1ul, 2ul, 4ul], l.Clips.Keys.Order());
        Assert.Equal(1, l.UnsureClips);
        Assert.Equal("First clip", l.Clips[1].Name);   // the first of an ID twice is kept
        Assert.Empty(l.Clips[2].Who);
        Assert.Equal([Death, HitReaction], l.Clips[2].What);
        Assert.Null(l.Clips[4].Name);
        Assert.Equal([Npc], l.Clips[4].Who);           // "Pilot" isn't a category, and Uncategorised isn't a placement
        Assert.Contains(l.Problems, p => p.Contains("\"Pilot\""));
        Assert.Contains(l.Problems, p => p.Contains("\"Uncategorised\""));
        Assert.Contains(l.Problems, p => p.Contains("stands twice"));
        Assert.Contains(l.Problems, p => p.Contains("0x and 16 hex digits"));
        Assert.Contains(l.Problems, p => p.Contains("one word"));
        // a glossary entry's own search words, optional; ones that aren't a text are left out, the entry kept
        Assert.Equal("light hit", l.Glossary["regular"].Search);
        Assert.Null(l.Glossary["swm"].Search);
        Assert.Null(l.Glossary["tm8"].Search);
        Assert.Contains(l.Problems, p => p.Contains("\"search\" isn't a text"));
        Assert.Equal(6, l.Problems.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"format\": 2, \"clips\": [] }")]
    [InlineData("{ \"clips\": [] }")]
    [InlineData("{ \"format\": 1, \"clips\": {} }")]
    [InlineData("{ \"format\": 1, \"clips\": [ 3 ] }")]
    public void AListNotOfTheFormIsRefusedWhole(string json)
    {
        var l = List(json);
        Assert.NotNull(l.Error);
        Assert.Empty(l.Clips);
        Assert.Empty(l.Glossary);
    }

    [Fact]
    public void TheShippedListReads()
    {
        var l = ClipIndexList.Embedded();
        Assert.NotNull(l);
        Assert.Null(l.Error);
        Assert.Empty(l.Problems);
        Assert.Equal(642, l.Clips.Count);
        Assert.Equal(53, l.UnsureClips);
        Assert.Equal(90, l.Glossary.Count);
        Assert.Equal(48, l.UnsureGlossary);
        // a shown text never speaks of the research behind it
        Assert.DoesNotContain(l.Glossary.Values, g => g.Meaning.Contains("test", StringComparison.OrdinalIgnoreCase));
        // every sure entry gives the search words: its own, or its meaning's short head; but for those whose head speaks of several
        // things at once (hits and deaths) or of nothing the clips are (a working name; the class's skill, which the name says itself)
        Assert.Equal(["heavy", "PantherSkill", "regular", "special", "TomTest"],
            l.Glossary.Values.Where(g => ClipIndexEntry.SearchText(g) is null).Select(g => g.Abbreviation).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("draw", l.Glossary["get"].Search);
        Assert.Equal("put away", l.Glossary["stow"].Search);
        // the shoulders' search words are phrases: their side word counts only beside shoulder (left and right there aren't a move's
        // direction)
        Assert.Equal("left shoulder", l.Glossary["LHS"].Search);
        Assert.Equal("right shoulder", l.Glossary["RHS"].Search);
        Assert.Equal("north-east", l.Glossary["NE"].Search);
        // a readable name uses no part the glossary marks unsure
        Assert.DoesNotContain(l.Clips.Values, c => c.Name!.Contains("medium", StringComparison.OrdinalIgnoreCase)
            || c.Name.Contains("vrille", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.All(l.Clips.Values, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public void TheNamesWords()
    {
        Assert.Equal(["mch", "crh", "inj", "v0", "trav", "100", "in", "climb", "up"], NameRules.Words("Mch_CRHInj_V0_trav_100_IN_ClimbUp"));
        Assert.Equal(["mch", "hg", "std", "very", "low", "health", "fm", "v0"], NameRules.Words("MCH_HG_Std_VeryLowHealthFM_V0"));
        Assert.Equal(["tm8", "rif"], NameRules.Words("tm8_rif"));
    }

    static List<ClipCategory> What(string name, IReadOnlyDictionary<string, GlossaryEntry>? g = null) => NameRules.Place(name, g).What;
    static List<ClipCategory> Who(string name, IReadOnlyDictionary<string, GlossaryEntry>? g = null) => NameRules.Place(name, g).Who;

    [Fact]
    public void TheNameRules()
    {
        Assert.Equal([Death], What("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01").Select(c => c.Name));
        // soldiers aren't all enemies: the soldiers' and the archetypes' families are both, the heavy archetype's the enemies' alone
        Assert.Equal([Enemy, Npc], Who("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01").Select(c => c.Name));
        Assert.Equal([Enemy, Npc], Who("pmc_rif_m_ale_fm_std_V2_N_fast").Select(c => c.Name));
        Assert.Equal([Enemy], Who("pmc_hvy_m_ale_fm_std_V0_N_death_01").Select(c => c.Name));
        Assert.Equal([Player], Who("Mcm_Fm_Std_AR_V0_Fidget_CheckHeadset").Select(c => c.Name));
        Assert.Equal([Idle], What("Mcm_Fm_Std_AR_V0_Fidget_CheckHeadset").Select(c => c.Name));
        // the teammates' family is the teammates', apart from the other NPCs; the soldiers' families by the name alone never are
        Assert.Equal([Teammates], Who("tm8_rif_m_ale_fm_crh_V0_Finka").Select(c => c.Name));
        Assert.Equal([Npc], Who("civ_m_neutral_fm_std_V1_N").Select(c => c.Name));
        Assert.Equal([Enemy, Npc], Who("npc_new_clip_01").Select(c => c.Name));
        Assert.True(NameRules.FamilyOf("npc_x")!.KeepsTeammates && NameRules.FamilyOf("mil_x")!.KeepsTeammates && NameRules.FamilyOf("pmc_rif_x")!.KeepsTeammates);
        Assert.False(NameRules.FamilyOf("pmc_hvy_x")!.KeepsTeammates || NameRules.FamilyOf("civ_x")!.KeepsTeammates || NameRules.FamilyOf("mca_x")!.KeepsTeammates);
        // a family no record settles places no who
        Assert.Empty(Who("vip_sam_col_fm_std_V1_N"));
        Assert.Empty(Who("nme_m_ale_ros_std_V0_N_04"));
        // the look-at sweeps are poses
        Assert.Equal([Pose], What("Look_at_Std_pan_U").Select(c => c.Name));
        // two words a name spells as one part, and a turret's word with its number
        Assert.Equal([InjuryAndDowned], What("mca_std_lowhealth_igs").Select(c => c.Name));
        Assert.Equal([InjuryAndDowned], What("Mcm_Fm_Std_AR_V0_LowHealth_SonicStun").Select(c => c.Name));
        Assert.Equal([Vehicle], What("Mch_Hvy_Turr01_ExitR_Hatch").Select(c => c.Name));
        // "face up" is a body lying face up, not a face
        Assert.DoesNotContain(What("mca_crh_reviver_FaceUp_R_bg"), c => c.Name == FaceAndDialogue);
        Assert.Contains(What("Mch_Face_Idle_Smile02"), c => c.Name == FaceAndDialogue);
        Assert.Contains("\"death\"", What("x_death")[0].Reason);
        Assert.All(What("Mch_Face_Idle_Smile02"), c => Assert.Equal(CategorySource.Name, c.Source));
        Assert.Empty(What("nothing_known_here"));
        Assert.Empty(Who("nothing_known_here"));
    }

    [Fact]
    public void TheGlossaryExplainsAWordOnlyByAShortHead()
    {
        var g = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sss"] = new("sss", "swimming"),
            ["regular"] = new("regular", "in hits, a light hit; in deaths, the plain kind of bullet death"),
            ["mil"] = new("mil", "soldiers (military NPCs): their shared deaths, hits and cover moves"),
            ["tm8"] = new("tm8", "AI teammates"),
            ["xyz"] = new("xyz", "low health: hurt badly"),
        };
        // the long heads ("in hits, a light hit", a sentence) and the explanations after them place nothing
        var (who, what, explained) = NameRules.Place("mil_xyz_regular_sss_01", g);
        Assert.Equal([InjuryAndDowned, Swimming], what.Select(c => c.Name));
        Assert.Contains("\"sss\" means \"swimming\"", what.Single(c => c.Name == Swimming).Reason);
        // who from the family alone (mil), not from the meaning "soldiers (military NPCs)"
        Assert.Equal([Enemy, Npc], who.Select(c => c.Name));
        Assert.All(who, c => Assert.StartsWith("the name's family", c.Reason));
        Assert.Equal(["mil", "xyz", "regular", "sss"], explained.Select(e => e.Abbreviation));
        // a meaning never places who, even one that names characters ("AI teammates"); only a family does
        Assert.Empty(NameRules.Place("zz_tm8", g).Who);
        Assert.Equal("low health", NameRules.MeaningHead("low health: hurt badly"));
        Assert.Equal("soldiers", NameRules.MeaningHead("soldiers (military NPCs): their deaths"));
    }

    [Fact]
    public void TheSearchFields()
    {
        var x = ClipIndex.Build([(0xA, "clip_swm_A"), (0x13, "clip_13")], Facts(Uses), List(ListJson));
        var a = x.SearchFields(0xA);
        Assert.Equal(("Wave", ClipIndexEntry.ReadableNameWeight), (a[0].Text, a[0].Weight));
        Assert.Contains(new IndexSearchField("swimming", ClipIndexEntry.GlossaryWeight), a);
        // a category adds more than another field when it holds a word another field matches best
        Assert.Contains(new IndexSearchField(Player, ClipIndexEntry.CategoryWeight, ClipIndexEntry.CategoryBonus), a);
        Assert.Contains(new IndexSearchField(Emote, ClipIndexEntry.CategoryWeight, ClipIndexEntry.CategoryBonus), a);
        Assert.Equal(ClipSearch.FurtherField, a[0].Bonus);
        Assert.True(ClipIndexEntry.CategoryBonus > ClipSearch.FurtherField && ClipIndexEntry.CategoryBonus <= ClipSearch.MostBonus);
        // an uncategorised kind is searchable by that word; a clip the index doesn't hold has none
        Assert.Contains(new IndexSearchField(Uncategorised, ClipIndexEntry.CategoryWeight, ClipIndexEntry.CategoryBonus), x.SearchFields(0x13));
        Assert.Empty(x.SearchFields(0x999));
        Assert.All(a, f => Assert.True(f.Weight > 0));
        // a meaning's short head only: "soldiers", never the explanation of the family's deaths and hits; a head too long to be an
        // expansion ("in hits, a light hit", "enemy soldiers of the several archetypes") gives no field at all, unless the entry gives
        // its own search words ("draw" for get), which the search takes before the head
        var withHeads = ClipIndex.Build([(0x50, "mil_regular_pmc_npc_get_LHS_x")], Facts(new()), List("""
            { "format": 1, "glossary": [
                { "abbr": "mil", "meaning": "soldiers (military NPCs): their shared deaths, hits and cover moves", "sure": true },
                { "abbr": "regular", "meaning": "in hits, a light hit; in deaths, the plain kind of bullet death", "sure": true },
                { "abbr": "pmc", "meaning": "enemy soldiers of the several archetypes (rifleman, rusher, heavy and others)", "sure": true },
                { "abbr": "npc", "meaning": "non-player characters: clips shared by NPCs, such as takedown victims and vehicle hijacks", "sure": true },
                { "abbr": "get", "meaning": "draw the weapon or take the item out", "search": "draw", "sure": true },
                { "abbr": "LHS", "meaning": "on the left", "search": "left shoulder", "sure": true } ],
              "clips": [] }
            """));
        var e = withHeads.Find(0x50)!;
        Assert.Equal(["mil", "regular", "pmc", "npc", "get", "LHS"], e.Explained.Select(g => g.Abbreviation));
        Assert.Equal(["soldiers", "non-player characters", "draw", "left shoulder"], e.SearchFields.Where(f => f.Weight == ClipIndexEntry.GlossaryWeight).Select(f => f.Text));
        Assert.Equal("draw", e.Explained.Single(g => g.Abbreviation == "get").Search);
        Assert.Null(e.Explained.Single(g => g.Abbreviation == "mil").Search);
        // the search words are the search's alone: the name rules read the meaning's head as ever
        Assert.Equal("on the left", NameRules.ShortHead(e.Explained.Single(g => g.Abbreviation == "LHS").Meaning));
        Assert.Equal("left shoulder", ClipIndexEntry.SearchText(e.Explained.Single(g => g.Abbreviation == "LHS")));
        // an entry's own search words are a phrase, a meaning's head is plain prose
        Assert.Equal([false, false, true, true], e.SearchFields.Where(f => f.Weight == ClipIndexEntry.GlossaryWeight).Select(f => f.Phrase));
        Assert.Equal(SearchFieldKind.Phrase, ClipIndex.ToSearchField(e.SearchFields.Single(f => f.Text == "left shoulder")).Kind);
        // the compass's are a compound: each word a direction of its own
        Assert.Equal(SearchFieldKind.Compound, ClipIndex.ToSearchField(new IndexSearchField("north-east", ClipIndexEntry.GlossaryWeight, Phrase: true)).Kind);
        Assert.Equal(SearchFieldKind.Compound, ClipIndex.ToSearchField(new IndexSearchField("South West", ClipIndexEntry.GlossaryWeight, Phrase: true)).Kind);
        Assert.Equal(SearchFieldKind.Phrase, ClipIndex.ToSearchField(new IndexSearchField("north wall", ClipIndexEntry.GlossaryWeight, Phrase: true)).Kind);
        Assert.Equal(SearchFieldKind.Prose, ClipIndex.ToSearchField(new IndexSearchField("north east", ClipIndexEntry.GlossaryWeight)).Kind);
        Assert.Null(ClipIndexEntry.SearchText(e.Explained.Single(g => g.Abbreviation == "regular")));
        Assert.DoesNotContain(e.SearchFields, f => f.Weight == ClipIndexEntry.GlossaryWeight
            && ClipSearch.Words(f.Text).Intersect(["death", "deaths", "hit", "hits", "cover", "takedown", "vehicle", "enemy"]).Any());
        Assert.Equal("soldiers", NameRules.ShortHead("soldiers (military NPCs): their shared deaths"));
        Assert.Equal("low health", NameRules.ShortHead("low health"));
        Assert.Null(NameRules.ShortHead("in hits, a light hit; in deaths, the plain kind"));
        Assert.Null(NameRules.ShortHead("(nothing before the bracket)"));
    }

    [Fact]
    public void ThePartsOfTheGraphAreLayersWorldFirst()
    {
        // each layer once; the menus' character's layer after the layers played in the world; at most three, then how many more
        Assert.Equal("the player's layer (MCH-LayerGraph); the menus' character's layer (Avatar-LayerGraph)",
            GraphRules.Parts([("the menus' character's layer (Avatar-LayerGraph)", false), ("the player's layer (MCH-LayerGraph)", false),
                ("the player's layer (MCH-LayerGraph)", true)]));
        Assert.Equal("L1; L2; L3; and 1 more", GraphRules.Parts([("L1", false), ("L2", false), ("L3", false), ("L4", false)]));
        // a layer named once, its narrowing machines after it; "by a shared machine" only where shared machines alone reach the clip
        Assert.Equal("the generic NPC layer (GEN_NPC-LayerGraph), partly through Military-StateMachine, SamFisher_Fullbody-StateMachine",
            GraphRules.Parts([("the generic NPC layer (GEN_NPC-LayerGraph)", false), ("the generic NPC layer (GEN_NPC-LayerGraph), through Military-StateMachine", false),
                ("the generic NPC layer (GEN_NPC-LayerGraph), through SamFisher_Fullbody-StateMachine", true)]));
        Assert.Equal("L (R), through M, by a shared machine", GraphRules.Parts([("L (R), through M", true)]));
        // every branch has its plain words
        Assert.All(GraphRules.Branches, b => Assert.False(string.IsNullOrWhiteSpace(b.Label)));
    }

    [Fact]
    public void TheMenuRuleAddsMenuAndLobbyBesideTheOtherWhat()
    {
        // GR_Graph 0, its general machine 1 holding the menus' character's layer 2 and the player's layer 3; clip 0x50 named by the menus'
        // layer alone (the list says Idle), 0x51 by both layers (the name says emote), 0x52 by the menus' layer alone with nothing else
        var menu = GraphRules.Branches.Single(b => b.Id == GraphRules.MenuBranch);
        var player = GraphRules.Branches.Single(b => b.Id == "branch.player");
        var facts = new GraphFacts
        {
            Documents = [new(1, "GR_Graph", [1], [], 0), new(2, "General-StateMachine", [2, 3], [], 0), new(menu.RootId, menu.RootName, [], [], 3),
                new(player.RootId, player.RootName, [], [], 1), new(5, "Unreached-StateMachine", [], [], 1)],
            Clips = new Dictionary<ulong, ClipGraphFacts> { [0x50] = new([2], []), [0x51] = new([2, 3], []), [0x52] = new([2], []), [0x53] = new([2, 4], []) },
            Failures = [], ClipsLookedFor = 4,
        };
        var list = ClipIndexList.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{ "format": 1, "glossary": [], "clips": [ { "id": "0x0000000000000050", "name": "A menu idle", "who": ["Player"], "what": ["Idle"], "sure": true } ] }""")));
        var x = ClipIndex.Build([(0x50, "menu_idle"), (0x51, "mca_emote_Wave"), (0x52, "Lobby_Thing"), (0x53, "FA_thing_01")], facts, list);
        Assert.Equal([(Idle, CategorySource.ToolList), (MenuAndLobby, CategorySource.Graph)], x.Find(0x50)!.What.Select(c => (c.Name, c.Source)));
        // beside the list's Idle, the menu rule doesn't take the decision from the list
        Assert.Equal(CategorySource.ToolList, x.Find(0x50)!.SourceOf(CategoryKind.What));
        Assert.Equal(CategorySource.Graph, x.Find(0x52)!.SourceOf(CategoryKind.What));
        // a clip the menus' layer and a document no branch reaches both name: not menu-only (as FA_cigar_01 in the game)
        Assert.DoesNotContain(x.Find(0x53)!.What, c => c.Name == MenuAndLobby);
        Assert.DoesNotContain(x.Find(0x51)!.What, c => c.Name == MenuAndLobby);
        Assert.Equal([MenuAndLobby], x.Find(0x52)!.Names(CategoryKind.What));
        Assert.StartsWith(GraphRules.MenuReason, x.Find(0x52)!.What[0].Reason);
        Assert.Equal("the menus' character's layer (Avatar-LayerGraph)", x.Find(0x52)!.Who.Single().Part);
        Assert.Equal("the player's layer (MCH-LayerGraph); the menus' character's layer (Avatar-LayerGraph)", x.Find(0x51)!.Who.Single().Part);
    }

    [Fact]
    public void TheVocabularyIsTheSpecs()
    {
        Assert.Equal(["Player", "Teammates", "Enemy", "NPC", "Animal", "Vehicle", "Cinematic", "Uncategorised"], ClipCategories.Who);
        // the teammates' layer gives Teammates alone, never NPC
        Assert.Equal([Teammates], GraphRules.Branches.Single(b => b.Id == "branch.teammates").Who);
        Assert.DoesNotContain(GraphRules.Branches.Where(b => b.Id != "branch.teammates"), b => b.Who.Contains(Teammates));
        Assert.Equal(["Locomotion", "Idle", "Weapon handling", "Emote", "Death", "Hit reaction", "Injury and downed", "Cover", "Vehicle",
            "Interaction", "Traversal", "Swimming", "Melee and takedown", "Carry and drag", "Face and dialogue", "Pose", "Menu and lobby", "Uncategorised"],
            ClipCategories.What);
        Assert.Equal("from the game's graph", ClipCategories.Label(CategorySource.Graph));
        Assert.Equal("from the tool's list", ClipCategories.Label(CategorySource.ToolList));
        Assert.Equal("from the name", ClipCategories.Label(CategorySource.Name));
        // every rule names categories of the vocabulary
        Assert.All(GraphRules.Documents, r => Assert.All(r.What, w => Assert.True(IsIn(CategoryKind.What, w) && w != Uncategorised, $"{r.Id}: {w}")));
        Assert.All(GraphRules.Branches.SelectMany(b => b.Who).Concat(GraphRules.Refinements.SelectMany(r => r.Who)), w => Assert.True(IsIn(CategoryKind.Who, w)));
        Assert.All(NameRules.Keywords, k => Assert.True(IsIn(k.Kind, k.Category) && k.Category != Uncategorised, string.Join(" ", k.Words)));
        Assert.Equal(GraphRules.Documents.Count, GraphRules.Documents.Select(r => r.Id).Distinct().Count());
        // who comes from a family only, never from a word
        Assert.DoesNotContain(NameRules.Keywords, k => k.Kind == CategoryKind.Who);
        Assert.All(NameRules.FamilyRules.SelectMany(f => f.Who), w => Assert.True(IsIn(CategoryKind.Who, w) && w != Uncategorised));
        // the containers are weak, the machines that say what each clip is strong
        Assert.All(["loco.layer", "loco.npc", "loco.mf", "loco.healthy", "loco.lowhealth", "loco.rose", "loco.prone", "loco.wading"],
            id => Assert.True(GraphRules.Documents.Single(r => r.Id == id).Weak, id));
        Assert.All(["loco.stance", "death", "hit", "emote", "cover", "traversal", "swimming", "vehicle", "interaction"],
            id => Assert.False(GraphRules.Documents.Single(r => r.Id == id).Weak, id));
    }

    [Fact]
    public void AWholeWordRuleGroupsItsAlternatives()
    {
        var traversal = GraphRules.Documents.Single(r => r.Id == "traversal").Pattern;
        Assert.Matches(traversal, "ReferenceStateMachines-Basejump");
        Assert.Matches(traversal, "Traversal-LayerGraph");
        // the middle alternative inside a longer word: only an ungrouped pattern would take it
        Assert.DoesNotMatch(traversal, "SlowBasejumper-StateMachine");
        var talk = GraphRules.Documents.Single(r => r.Id == "dialogue.conversation").Pattern;
        Assert.Matches(talk, "ReferenceStateMachines-LipSyncPhonemes");
        Assert.DoesNotMatch(talk, "NoLipSyncPhonemesHere-Layer");
    }

    /// <summary>A player's layer with a motion-field machine and a hit machine, and the generic layer's locomotion layer holding a
    /// civilians' machine.</summary>
    static readonly (ulong, string, int[], int[])[] ContainerDocs =
    [
        (0x000001555E68541F, "GR_Graph", [1], []),                           // 0
        (0x10, "General-StateMachine", [2, 3], []),                          // 1
        (Player1, "MCH-LayerGraph", [4], []),                                // 2
        (GraphRules.NpcMachineId, "NPC-StateMachine", [5], []),              // 3
        (0x13, "Ground-StateMachine", [6, 7], []),                           // 4
        (Generic, "GEN_NPC-LayerGraph", [8], []),                            // 5
        (0x14, "MF_Stand-LayerGraph", [], []),                               // 6 weak: a motion-field machine
        (0x15, "Hits-StateMachine", [], []),                                 // 7 strong: hits
        (0x16, "Locomotion-LayerGraph", [9], []),                            // 8 weak, inherited
        (0x17, "Civilian-StateMachine", [], []),                             // 9 under the locomotion layer
    ];

    [Fact]
    public void AContainersCategoryFillsOnlyWhatNothingElsePlaces()
    {
        var facts = Facts(new()
        {
            [0x1] = ([6, 7], []), [0x2] = ([6], []), [0x3] = ([6], []), [0x4] = ([9], []), [0x5] = ([9], []), [0x6] = ([6], [6]), [0x7] = ([9], []),
        }, ContainerDocs);
        var list = List("""{ "format": 1, "clips": [ { "id": "0x0000000000000007", "what": ["Idle"], "sure": true } ] }""");
        var x = ClipIndex.Build([(0x1, "MCH_AR_Std_V0_Hit_Heavy_Front_01"), (0x2, "AR_Std_AimPan_V0_M"), (0x3, "fm_std_V0_idle_cool_01"),
            (0x4, "civ_m_neutral_fm_std_V1_N"), (0x5, "civ_m_fear_prostrate_fm_crh_V0_N_underfire_01"), (0x6, "clip_6"), (0x7, "civ_m_neutral_fm_std_V1_N_02")], facts, list);
        // a strong rule wins alone: the hit machine's hit, not the motion-field machine's locomotion
        Is(x.Find(0x1)!, CategoryKind.What, CategorySource.Graph, HitReaction);
        Assert.False(x.Find(0x1)!.WhatFromContainer);
        // the name before a container: an aim sweep, an idle
        Is(x.Find(0x2)!, CategoryKind.What, CategorySource.Name, WeaponHandling);
        Is(x.Find(0x3)!, CategoryKind.What, CategorySource.Name, Idle);
        // the list before a container
        Is(x.Find(0x7)!, CategoryKind.What, CategorySource.ToolList, Idle);
        // nothing else places it and the name says it moves: the container's locomotion, as the graph's
        var walk = x.Find(0x4)!;
        Is(walk, CategoryKind.What, CategorySource.Graph, Locomotion);
        Assert.True(walk.WhatFromContainer);
        Assert.Contains("Locomotion-LayerGraph holds it", walk.What[0].Reason);
        Assert.Contains("\"V1\" says it moves", walk.What[0].Reason);
        Is(walk, CategoryKind.Who, CategorySource.Graph, Npc);
        // a civilian cowering in place: no container's guess
        Is(x.Find(0x5)!, CategoryKind.What, null);
        // a motion field is strong
        Is(x.Find(0x6)!, CategoryKind.What, CategorySource.Graph, Locomotion);
        Assert.False(x.Find(0x6)!.WhatFromContainer);
        var c = x.Census();
        Assert.Equal(1, c.WhatByContainer);
        Assert.Equal(1, c.Uncategorised(CategoryKind.What));
    }

    [Fact]
    public void TheFamilyNarrowsWhoInAMachineSeveralLayersPlay()
    {
        // CQC is played by reference from the player's and the generic layer: a non-player family leaves the player out, a player family
        // keeps the union (NPC layers play the main character's clips in shared machines), a family without evidence keeps it too
        var x = ClipIndex.Build([(0x50, "npc_victim_01"), (0x51, "mcm_attack_01"), (0x52, "vip_attack_01")],
            Facts(new() { [0x50] = ([12], []), [0x51] = ([12], []), [0x52] = ([12], []) }), null);
        var victim = x.Find(0x50)!;
        Is(victim, CategoryKind.Who, CategorySource.Name, Enemy, Npc);
        Assert.True(victim.WhoNarrowedByName);
        Assert.Equal(FromName, victim.Who[0].SourceLabel);
        Assert.Contains("ReferenceStateMachines-CQC names it, a machine several layers play (Player, Enemy, NPC)", victim.Who[0].Reason);
        Is(x.Find(0x51)!, CategoryKind.Who, CategorySource.Graph, Player, Enemy, Npc);
        Is(x.Find(0x52)!, CategoryKind.Who, CategorySource.Graph, Player, Enemy, Npc);
        Assert.Equal(1, x.Census().WhoNarrowedByName);
        // in the conversation machine the main character's clips are the player's alone
        var docs = Docs.ToArray();
        docs[12] = (0x17, "ReferenceStateMachines-Conversations", [], []);
        var talk = ClipIndex.Build([(0x60, "mca_talk_01"), (0x61, "npc_talk_01"), (0x62, "civ_talk_01")],
            Facts(new() { [0x60] = ([12], []), [0x61] = ([12], []), [0x62] = ([12], []) }, docs), null);
        Is(talk.Find(0x60)!, CategoryKind.Who, CategorySource.Name, Player);
        Assert.Contains("conversation machine", talk.Find(0x60)!.Who[0].Reason);
        Is(talk.Find(0x61)!, CategoryKind.Who, CategorySource.Name, Enemy, Npc);
        Is(talk.Find(0x62)!, CategoryKind.Who, CategorySource.Name, Npc);
        // under the soldiers' machine (both sides) the heavy archetype's family is the enemies', the riflemen's stays both; the
        // machine is the generic layer's alone, so what it keeps is the graph's, sure
        var soldiers = ClipIndex.Build([(0x90, "pmc_hvy_m_detection_01"), (0x91, "pmc_rif_m_detection_01")],
            Facts(new() { [0x90] = ([19], []), [0x91] = ([19], []) }), null);
        Is(soldiers.Find(0x90)!, CategoryKind.Who, CategorySource.Graph, Enemy);
        Assert.Empty(soldiers.Find(0x90)!.WhoProbably);
        Assert.Contains("under the soldiers' machine", soldiers.Find(0x90)!.Who[0].Reason);
        Is(soldiers.Find(0x91)!, CategoryKind.Who, CategorySource.Graph, Enemy, Npc);
        // the generic layer's own documents play for soldiers and civilians alike: a civilian's clip there is an NPC's, never an
        // enemy's; the heavy archetype's the enemies'; the soldiers' family keeps both. The generic layer alone plays them, so what the
        // family keeps is the graph's, sure; the reason says what the name took away
        var generic = ClipIndex.Build([(0x70, "civ_death_01"), (0x71, "pmc_hvy_death_01"), (0x72, "mil_death_01"), (0x73, "tm8_death_01"), (0x74, "vip_death_01")],
            Facts(new() { [0x70] = ([15], []), [0x71] = ([15], []), [0x72] = ([15], []), [0x73] = ([11], []), [0x74] = ([15], []) }), null);
        var civilian = generic.Find(0x70)!;
        Is(civilian, CategoryKind.Who, CategorySource.Graph, Npc);
        Assert.Empty(civilian.WhoProbably);
        Assert.True(civilian.WhoNarrowedByName);
        Assert.Contains("Death-StateMachine names it, played by the generic NPC layer, which serves soldiers and civilians alike (Enemy, NPC)", civilian.Who[0].Reason);
        Assert.Contains("the civilians' family", civilian.Who[0].Reason);
        Is(generic.Find(0x71)!, CategoryKind.Who, CategorySource.Graph, Enemy);
        Is(generic.Find(0x72)!, CategoryKind.Who, CategorySource.Graph, Enemy, Npc);
        // the teammates' layer gives one side: nothing to narrow; a family without evidence keeps the union
        Is(generic.Find(0x73)!, CategoryKind.Who, CategorySource.Graph, Teammates);
        Assert.False(generic.Find(0x73)!.WhoNarrowedByName);
        Is(generic.Find(0x74)!, CategoryKind.Who, CategorySource.Graph, Enemy, Npc);
        // the same under the Military machine's soldiers' machine and an idle in the generic layer's cover layer
        var more = ClipIndex.Build([(0x75, "civ_rifleman_01"), (0x76, "civ_idle_cover_01")], Facts(new() { [0x75] = ([19], []), [0x76] = ([21], []) }), null);
        Is(more.Find(0x75)!, CategoryKind.Who, CategorySource.Graph, Npc);
        Is(more.Find(0x76)!, CategoryKind.Who, CategorySource.Graph, Npc);
        // a document of one layer and a shared machine together: what the one layer keeps is the graph's and sure, what only the
        // shared machine's union narrowed by the name keeps is the name's and probable
        var docs2 = Docs.ToArray();
        docs2[8] = (TeammatesLayer, "SPE_TM8-LayerGraph", [11], [12]);
        var mixed = ClipIndex.Build([(0x77, "npc_death_01")], Facts(new() { [0x77] = ([15, 12], []) }, docs2), null).Find(0x77)!;
        Assert.Equal([(Teammates, CategorySource.Name), (Enemy, CategorySource.Graph), (Npc, CategorySource.Graph)], mixed.Who.Select(c => (c.Name, c.Source)));
        Assert.Equal([Teammates], mixed.WhoProbably);
        Assert.Equal("Enemy and NPC; probably also Teammates", ClipUsers.Of(mixed).Text);
        // the graph's lines name the part of the graph that decided them: the layer, in plain words with its root's name
        Assert.Equal(["probably Teammates (from the name)", "Enemy and NPC, from the game's graph: the generic NPC layer (GEN_NPC-LayerGraph)"],
            ClipIndexView.CategoryLines(mixed, CategoryKind.Who).Select(l => l.Text));
        // one line for both; their one reason once (different reasons would stand one under the other, each with its kind)
        Assert.Equal(mixed.Who[1].Reason, ClipIndexView.CategoryLines(mixed, CategoryKind.Who)[1].Reason);
        Assert.Equal(mixed.Who[1].Reason, mixed.Who[2].Reason);
    }

    [Fact]
    public void AMachineTheTeammatesShareGivesTeammatesOnlyProbably()
    {
        // the teammates' layer plays CQC by reference too: its union holds the teammates; the non-player characters' families at large
        // keep their share, the civilians' and the teammates' own families narrow it to theirs; a main-character family keeps the union
        var docs = Docs.ToArray();
        docs[8] = (TeammatesLayer, "SPE_TM8-LayerGraph", [11], [12]);
        var x = ClipIndex.Build([(0x80, "npc_victim_01"), (0x81, "civ_victim_01"), (0x82, "tm8_victim_01"), (0x83, "mcm_attack_01"), (0x84, "clip_84")],
            Facts(new() { [0x80] = ([12], []), [0x81] = ([12], []), [0x82] = ([12], []), [0x83] = ([12], []), [0x84] = ([12, 11], []) }, docs), null);
        var npc = x.Find(0x80)!;
        Is(npc, CategoryKind.Who, CategorySource.Name, Teammates, Enemy, Npc);
        Assert.Contains("a machine several layers play (Player, Teammates, Enemy, NPC)", npc.Who[0].Reason);
        Assert.Equal("probably Teammates, Enemy and NPC", ClipUsers.Of(npc).Text);
        Is(x.Find(0x81)!, CategoryKind.Who, CategorySource.Name, Npc);
        Is(x.Find(0x82)!, CategoryKind.Who, CategorySource.Name, Teammates);
        Is(x.Find(0x83)!, CategoryKind.Who, CategorySource.Graph, Player, Teammates, Enemy, Npc);
        Assert.Equal("probably Player, Teammates, Enemy and NPC", ClipUsers.Of(x.Find(0x83)!).Text);
        // the shared machine and the teammates' own deaths: Teammates is sure, the rest of the union probable
        var both = ClipUsers.Of(x.Find(0x84)!);
        Assert.Equal([Teammates], both.Sure);
        Assert.Equal([Player, Enemy, Npc], both.Probably);
        Assert.Equal("Teammates; probably also Player, Enemy and NPC", both.Text);
        Assert.StartsWith("More than one kind of character plays this clip: Teammates; probably also Player, Enemy and NPC.", both.Notice);
        // the census counts them
        var c = x.Census();
        Assert.Equal(3, c.SeveralUsers);
        Assert.Equal(3, c.SeveralUsersProbably);
        Assert.Equal(5, c.WhoProbably);
        Assert.Equal(4, c.PerCategory[CategoryKind.Who][Teammates]);
        // the census holds the tool's list to the graph's sure users: a list who that leaves one out, or names as sure one the graph
        // has only as probable, is counted, though it says nothing beyond the graph
        var listed = ClipIndex.Build([(0x83, "mcm_attack_01"), (0x84, "clip_84"), (0x85, "clip_85")],
            Facts(new() { [0x83] = ([12], []), [0x84] = ([12, 11], []), [0x85] = ([12, 11], []) }, docs), List("""
                { "format": 1, "glossary": [], "clips": [
                    { "id": "0x0000000000000083", "who": ["Player"], "sure": true },
                    { "id": "0x0000000000000084", "who": ["Enemy"], "sure": true },
                    { "id": "0x0000000000000085", "who": ["Teammates"], "sure": true } ] }
                """)).Census();
        Assert.Equal((0, 1, 2), (listed.ListBeyondGraph.GetValueOrDefault(CategoryKind.Who), listed.ListFewerThanGraphSure, listed.ListSureWhereGraphProbable));
        Assert.Contains(listed.Lines(), l => l.EndsWith("of who, fewer than the graph's sure users 1, sure where the graph has a user only as probable 2", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWoundedBandsInjuryDoesNotStandForAHealthyClipsName()
    {
        (ulong, string, int[], int[])[] docs =
        [
            (0x1, "ReferenceStateMachines-Wounded_STD_V0", [], []),     // 0 the wounded band
            (0x2, "Fidgets-StateMachine", [], []),                      // 1
            (0x3, "Down-StateMachine", [], []),                         // 2 downed: not the band
            (0x4, "Locomotion-VeryLowHealtrh", [], []),                 // 3 the band again
            (0x5, "MF_Stand-LayerGraph", [], []),                       // 4 weak
        ];
        var x = ClipIndex.Build([(0x1, "fm_std_V0_idle_cool_01"), (0x2, "MCH_AR_Std_V0_Suppressed_03"), (0x3, "Crh_CoolFM_V0_WoundedFM"),
                (0x4, "Mcm_Fm_Std_AR_V0_Fidget_Protection_Sun_In"), (0x5, "mca_idle_downed_01"), (0x6, "MCH_HG_Std_WoundedFM_V0_ale_Cough_01"),
                (0x7, "mca_lowhealth_idle_01"), (0x8, "mca_idle_01")],
            Facts(new() { [0x1] = ([0], []), [0x2] = ([0, 4], []), [0x3] = ([0], []), [0x4] = ([3, 1], []), [0x5] = ([2], []), [0x6] = ([3], []),
                [0x7] = ([0], []), [0x8] = ([0, 2], []) }, docs), null);
        // a healthy idle, a suppressed hold, a fidget: the band's Injury and downed goes, what else places them stands
        Is(x.Find(0x1)!, CategoryKind.What, CategorySource.Name, Idle);
        Is(x.Find(0x2)!, CategoryKind.What, null);
        Is(x.Find(0x4)!, CategoryKind.What, CategorySource.Graph, Idle);
        // the wounded's own clips keep it: a word of injury in the name, or no healthy word
        Is(x.Find(0x3)!, CategoryKind.What, CategorySource.Graph, InjuryAndDowned);
        Is(x.Find(0x6)!, CategoryKind.What, CategorySource.Graph, InjuryAndDowned);
        Is(x.Find(0x7)!, CategoryKind.What, CategorySource.Graph, InjuryAndDowned);
        // the downed machine's isn't the band's: it stands, alone or beside the band
        Is(x.Find(0x5)!, CategoryKind.What, CategorySource.Graph, InjuryAndDowned);
        Is(x.Find(0x8)!, CategoryKind.What, CategorySource.Graph, InjuryAndDowned);
        // a transition into low cover is no idle
        var idle = GraphRules.Documents.Single(r => r.Id == "idle.machine").Pattern;
        Assert.Matches(idle, "IdleLow-LayerGraph");
        Assert.Matches(idle, "Idle-LayerGraph");
        Assert.DoesNotMatch(idle, "TO_IdleLow-LayerGraph");
    }

    [Fact]
    public void AGrabMachinesCarryClipIsAlsoCarryAndDragAndACarryOutOfAVehicleVehicle()
    {
        (ulong, string, int[], int[])[] docs =
        [
            (0x1, "ReferenceStateMachines-TGT_Shrd_TO_Grab_State", [], []),   // 0 a grab machine
            (0x2, "ReferenceStateMachines-Put_in_Vehicle", [], []),           // 1 carry and Vehicle (its name says vehicle)
            (0x3, "CQC-StateMachine", [], []),                                // 2 close combat
        ];
        var x = ClipIndex.Build([(0x1, "mca_carry_exit_light_no_door_right"), (0x2, "mca_carry_entry_light_swing_door_left"),
                (0x3, "mca_carry_exit_truck_swing_door_left"), (0x4, "mca_carry_exit_boat_left"), (0x5, "mca_ale_hg_carry_V0_aim_panU"),
                (0x6, "mca_takedown_front"), (0x7, "npc_release_carried"), (0x8, "mca_carry_exit_medium_left")],
            Facts(new() { [0x1] = ([0], []), [0x2] = ([1], []), [0x3] = ([0], []), [0x4] = ([0], []), [0x5] = ([0], []), [0x6] = ([2], []),
                [0x7] = ([0], []), [0x8] = ([2], []) }, docs), null);
        ClipIndexEntry E(ulong id) => x.Find(id)!;
        // the grab machine's Melee and takedown stays, the graph's; the name's carry adds Carry and drag, labelled as the name's
        var aim = E(0x5);
        Assert.Equal([MeleeAndTakedown, CarryAndDrag], aim.Names(CategoryKind.What));
        Assert.Equal([CategorySource.Graph, CategorySource.Name], aim.What.Select(c => c.Source));
        Assert.Contains("\"carry\"", aim.What[1].Reason);
        Assert.Contains("TGT_Shrd_TO_Grab_State", aim.What[1].Reason);
        Assert.Equal(CategorySource.Graph, aim.SourceOf(CategoryKind.What));   // the graph still decided it
        // a carry out of a vehicle: Vehicle too, by the carry into the same vehicle (light), which the graph places as Vehicle, or
        // by the name's own vehicle word (truck, boat: vehicle keywords of the names), labelled as the name's
        Assert.Equal([Vehicle, MeleeAndTakedown, CarryAndDrag], E(0x1).Names(CategoryKind.What));
        Assert.Equal(CategorySource.Name, E(0x1).What[0].Source);
        Assert.Contains("mca_carry_entry_light_swing_door_left", E(0x1).What[0].Reason);
        Assert.Equal([Vehicle, MeleeAndTakedown, CarryAndDrag], E(0x3).Names(CategoryKind.What));
        Assert.Contains("\"truck\"", E(0x3).What[0].Reason);
        Assert.Equal([Vehicle, MeleeAndTakedown, CarryAndDrag], E(0x4).Names(CategoryKind.What));
        Assert.Equal(CategorySource.Graph, E(0x1).SourceOf(CategoryKind.What));
        Assert.Equal([Vehicle, CarryAndDrag], E(0x2).Names(CategoryKind.What));
        Assert.All(E(0x2).What, c => Assert.Equal(CategorySource.Graph, c.Source));
        // close combat's own clips stay as the graph has them; a release of a carried body ("carried") takes Carry and drag as "carry"
        // does; a carry exit named by the close-combat machine (the same melee rule) takes Carry and drag, but its "medium" no carry
        // entry and no keyword holds
        Assert.Equal([MeleeAndTakedown], E(0x6).Names(CategoryKind.What));
        Assert.Equal([MeleeAndTakedown, CarryAndDrag], E(0x7).Names(CategoryKind.What));
        Assert.Equal([MeleeAndTakedown, CarryAndDrag], E(0x8).Names(CategoryKind.What));
    }

    [Fact]
    public void ASpeedGivesNoLocomotionToAClipThatStandsStill()
    {
        var g = ClipIndexList.Embedded()!.Glossary;
        // the glossary's V1 walk, V2 run: Locomotion for a clip that moves, none for a pose, a suppressed hold or an offset
        Assert.Contains(What("pmc_rif_m_col_fm_std_V1_N", g), c => c.Name == Locomotion);
        Assert.Contains(What("MCH_HG_Crh_V2_Aim", g), c => c.Name == Locomotion);
        Assert.Equal([Pose], What("std_cool_HG_Pose_V2", g).Select(c => c.Name));
        Assert.DoesNotContain(What("crh_ale_HG_Pose_V1_V2", g), c => c.Name == Locomotion);
        Assert.DoesNotContain(What("MCH_HG_Crh_V2_Suppressed_04", g), c => c.Name == Locomotion);
        Assert.DoesNotContain(What("tm8_slg_5_ac_m_ale_fm_crh_V1_N_weapon_offset", g), c => c.Name == Locomotion);
        Assert.DoesNotContain(What("pmc_SGL_cg_m_ale_ros_std_V2_N_PoseRef", g), c => c.Name == Locomotion);
        // a word that says the clip moves still does
        Assert.Contains(What("mca_Pose_walk_01", g), c => c.Name == Locomotion);
        // and a container's Locomotion takes no speed of a still clip either
        Assert.Equal("V1", NameRules.MoveWord("civ_m_neutral_fm_std_V1_N"));
        Assert.Null(NameRules.MoveWord("std_cool_HG_Pose_V2"));
        Assert.Null(NameRules.MoveWord("MCH_HG_Crh_V2_Suppressed_04"));
    }

    [Fact]
    public void AMainCharacterFamilySaysNothingOfAClipOnlyTheNpcMachinePlays()
    {
        // the special NPCs' layer, under the NPC machine, names it: no who, though its family is the main character's
        var x = ClipIndex.Build([(0x80, "mca_climb_01"), (0x81, "mca_fly_01")], Facts(new() { [0x80] = ([14], []) }), null);
        Is(x.Find(0x80)!, CategoryKind.Who, null);
        Is(x.Find(0x80)!, CategoryKind.What, CategorySource.Name, Traversal);
        // a clip the graph doesn't name keeps its family's who
        Is(x.Find(0x81)!, CategoryKind.Who, CategorySource.Name, Player);
    }

    [Fact]
    public void AGlossaryMeaningNeverPlacesWho()
    {
        var g = ClipIndexList.Embedded()!.Glossary;
        // "npc" means "non-player characters...": the family says Enemy and NPC, never the player
        var npc = NameRules.Place("npc_new_clip_01", g);
        Assert.Equal([Enemy, Npc], npc.Who.Select(c => c.Name));
        Assert.All(npc.Who, c => Assert.StartsWith("the name's family", c.Reason));
        // "PvP" means "player versus player": the glossary explains it, and it places no who
        var pvp = NameRules.Place("Nme_Ar_Std_CQC_PVP_HitBack", g);
        Assert.Empty(pvp.Who);
        Assert.Contains(pvp.Explained, e => e.Abbreviation == "PvP");
        Assert.Contains(pvp.What, c => c.Name == HitReaction);
    }

    [Fact]
    public void TheCacheFileReadsBackAndRefusesAnotherInstallsOrADamagedOne()
    {
        var facts = Facts(Uses);
        var fp = new IndexFingerprint(123, 456, 0x000001DE8F05D119, 789, "ab");
        string dir = Path.Combine(AppContext.BaseDirectory, "index-cache-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "graph.json");
            using (var fs = File.Create(file)) ClipIndexCache.Write(fs, fp, facts);
            var ids = Uses.Keys.ToHashSet();
            Assert.Equal(ClipIndexCache.ReadResult.Ok, ClipIndexCache.TryRead(file, fp, ids, out var back));
            Assert.Equal(facts.Documents.Select(d => (d.Id, d.Name, string.Join(",", d.Children), string.Join(",", d.References))),
                back!.Documents.Select(d => (d.Id, d.Name, string.Join(",", d.Children), string.Join(",", d.References))));
            Assert.Equal(facts.Clips.Count, back.Clips.Count);
            Assert.All(facts.Clips, k => Assert.Equal(k.Value.Documents, back.Clips[k.Key].Documents));
            Assert.Equal([22], back.Clips[0x12].MotionFieldDocuments);
            // another install's
            Assert.Equal(ClipIndexCache.ReadResult.OtherFingerprint, ClipIndexCache.TryRead(file, fp with { ForgeLength = 124 }, ids, out _));
            // facts read another way, and a file from before the facts' version was kept (none in its fingerprint)
            Assert.Equal(GraphFacts.FormatVersion, fp.FactsVersion);
            Assert.Equal(ClipIndexCache.ReadResult.OtherFingerprint, ClipIndexCache.TryRead(file, fp with { FactsVersion = fp.FactsVersion + 1 }, ids, out _));
            string text = File.ReadAllText(file);
            // the rules aren't in the fingerprint (only facts are kept): a file an earlier copy wrote with its rules' version reads
            Assert.DoesNotContain("rulesVersion", text);
            File.WriteAllText(file, text.Replace("\"factsVersion\"", "\"rulesVersion\":1,\"factsVersion\""));
            Assert.Equal(ClipIndexCache.ReadResult.Ok, ClipIndexCache.TryRead(file, fp, ids, out _));
            Assert.Contains($"\"factsVersion\":{GraphFacts.FormatVersion}", text);
            File.WriteAllText(file, text.Replace($",\"factsVersion\":{GraphFacts.FormatVersion}", ""));
            Assert.Equal(ClipIndexCache.ReadResult.OtherFingerprint, ClipIndexCache.TryRead(file, fp, ids, out _));
            Assert.Equal(ClipIndexCache.ReadResult.Ok, ClipIndexCache.TryRead(file, fp with { FactsVersion = 0 }, ids, out _));
            File.WriteAllText(file, text);
            // a clip this install doesn't hold, or one it holds missing from the count
            Assert.Equal(ClipIndexCache.ReadResult.Unreadable, ClipIndexCache.TryRead(file, fp, ids.Where(i => i != 0xA).ToHashSet(), out _));
            Assert.Equal(ClipIndexCache.ReadResult.Unreadable, ClipIndexCache.TryRead(file, fp, ids.Append(0x99ul).ToHashSet(), out _));
            // cut short, every way; garbage; JSON of another kind
            var bytes = File.ReadAllBytes(file);
            foreach (int cut in new[] { 0, 1, 10, bytes.Length / 3, bytes.Length / 2, bytes.Length - 2 })
            {
                File.WriteAllBytes(file, bytes[..cut]);
                Assert.Equal(ClipIndexCache.ReadResult.Unreadable, ClipIndexCache.TryRead(file, fp, ids, out _));
            }
            File.WriteAllBytes(file, [0xFF, 0xFE, 0, 1, 2]);
            Assert.Equal(ClipIndexCache.ReadResult.Unreadable, ClipIndexCache.TryRead(file, fp, ids, out _));
            File.WriteAllText(file, "{\"magic\": \"something else\", \"format\": 1}");
            Assert.Equal(ClipIndexCache.ReadResult.Unreadable, ClipIndexCache.TryRead(file, fp, ids, out _));
            // a document index out of range
            File.WriteAllText(file, Encoding.UTF8.GetString(bytes).Replace("\"children\":[1,2]", "\"children\":[1,999]"));
            Assert.Equal(ClipIndexCache.ReadResult.Unreadable, ClipIndexCache.TryRead(file, fp, ids, out _));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
