using System.Diagnostics;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Library;
using static GrbAnimTool.Core.Index.ClipCategories;

namespace GrbAnimTool.Tests;

/// <summary>The clip index on the vanilla install: the graph's facts, the census, known clips, and the cache (in a folder under the
/// test's output, never the user's own).</summary>
[Collection("install")]
public sealed class IndexGameTests(InstallFixture f)
{
    static GraphFacts? _facts;
    static ClipIndex? _index;
    static readonly object Gate = new();

    GraphFacts Facts { get { lock (Gate) return _facts ??= GraphFacts.Read(f.Install.Records, f.Install.Clips.Select(c => c.Id)); } }
    ClipIndex X { get { lock (Gate) return _index ??= ClipIndex.Build(f.Install, Facts, ClipIndexList.Embedded()); } }

    const ulong GenericDeaths = 0x000001A2B8D2752A, TeammatesDeaths = 0x000001A2B8DBF324, Hub = 0x000001544EA94BB9, Emotes = 0x000001A62503D366,
        MenuLayer = 0x000001555E68546B;

    ClipIndexEntry Clip(string name) => X.Find(f.Install.FindClip(name)!.Id)!;

    static void Is(ClipIndexEntry e, CategoryKind kind, CategorySource? source, params string[] names)
    {
        Assert.Equal(names.Length == 0 ? [Uncategorised] : names, e.Names(kind));
        Assert.Equal(source, e.SourceOf(kind));
    }

    /// <summary>A progress that keeps every report, on the reporting thread.</summary>
    sealed class Reports : IProgress<double>
    {
        public readonly List<double> Values = [];
        public void Report(double value) { lock (Values) Values.Add(value); }
    }

    [GameDataFact]
    public void TheGraphFacts()
    {
        var reports = new Reports();
        var sw = Stopwatch.StartNew();
        var facts = Within.Seconds(60, () => GraphFacts.Read(f.Install.Records, f.Install.Clips.Select(c => c.Id), reports));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the graph's facts took {sw.Elapsed.TotalSeconds:F1} s");
        Assert.True(facts.Complete);
        Assert.Equal(312, facts.Documents.Count);
        Assert.Equal(9_938, facts.ClipsLookedFor);
        Assert.Equal(9_938, facts.Clips.Count);                   // every clip is named by a document
        Assert.Equal(1_044, facts.Clips.Values.Count(c => c.MotionFieldDocuments.Count > 0));
        Assert.Equal(312 - 1, facts.Documents.Sum(d => d.Children.Count));   // a tree by the tails: one parent each but GR_Graph
        var parents = facts.Documents.SelectMany(d => d.Children).GroupBy(c => c).ToList();
        Assert.Equal(311, parents.Count);
        Assert.DoesNotContain(parents, g => g.Key == facts.IndexOf(0x000001555E68541F));
        // the shared machines are reached by reference: the player's layer plays the shared vehicle machine, the generic NPC layer the CQC
        int Doc(string name) => facts.Documents.Select((d, i) => (d, i)).Single(x => x.d.Name == name).i;
        Assert.Contains(Doc("ReferenceStateMachines-TGT_Shrd_Vehicle_State"), facts.Documents[facts.IndexOf(0x000001544EA877D5)].References);
        Assert.Contains(Doc("ReferenceStateMachines-CQC"), facts.Documents[facts.IndexOf(0x000001544EA94BB9)].References);
        // the progress rises to the whole
        Assert.NotEmpty(reports.Values);
        Assert.Equal(1.0, reports.Values[^1], 6);
        for (int i = 1; i < reports.Values.Count; i++) Assert.True(reports.Values[i] >= reports.Values[i - 1]);
        Assert.All(reports.Values, v => Assert.InRange(v, 0, 1));
    }

    [GameDataFact]
    public void TheCensus()
    {
        var c = X.Census();
        Assert.Equal(9_938, c.Clips);
        Assert.Equal(312, c.Documents);
        Assert.Equal(0, c.DocumentFailures);
        Assert.Equal(271, c.DocumentsPlacingWho);
        Assert.Equal(230, c.DocumentsPlacingWhat);
        int Who(string n) => c.PerCategory[CategoryKind.Who][n];
        int What(string n) => c.PerCategory[CategoryKind.What][n];
        // the civilians' 67 clips the generic NPC layer's own documents name are NPC alone, and the heavy archetype's 14 there Enemy alone.
        // The teammates are apart from the NPCs: NPC is 864 fewer than when it held them (5,430): the clips whose NPC only the teammates'
        // documents or family gave, and 19 heavy-archetype deaths the teammates' death machine names, whose family now narrows the
        // generic layer's share to the enemies (it no longer equals the union)
        Assert.Equal((5_966, 3_677, 4_182, 4_566, 0, 0, 0, 254),
            (Who(Player), Who(Teammates), Who(Enemy), Who(Npc), Who(Animal), Who(VehicleItself), Who(Cinematic), Who(Uncategorised)));
        // (of the 465 clips whose who the name's family narrowed, 141 keep a kind a document of one layer gives: the graph decided them)
        Assert.Equal((9_355, 4, 325, 254), (c.Decided(CategoryKind.Who, CategorySource.Graph), c.Decided(CategoryKind.Who, CategorySource.ToolList),
            c.Decided(CategoryKind.Who, CategorySource.Name), c.Uncategorised(CategoryKind.Who)));
        Assert.Equal(465, c.WhoNarrowedByName);
        // who a replacement changes: more than one kind of character, and of those a kind only probably (the 35 the heavies' and
        // civilians' families rule the teammates out of among them); one kind, only probably, 95
        Assert.Equal((4_391, 1_981, 2_076), (c.SeveralUsers, c.SeveralUsersProbably, c.WhoProbably));
        Assert.Equal(95, X.Entries.Count(e => ClipUsers.Of(e).OneProbable));
        // the tool's list says of who exactly the graph's sure users, wherever the graph decides: nothing beyond the graph, none of its
        // sure users left out (the generic NPC layer's deaths and hits the teammates' own machines name too are Teammates, Enemy and
        // NPC), and nothing sure the graph has only as probable (a clip only a shared machine names gives no who in the list)
        Assert.Equal((0, 0, 0), (c.ListBeyondGraph.GetValueOrDefault(CategoryKind.Who), c.ListFewerThanGraphSure, c.ListSureWhereGraphProbable));
        // the list against the graph for what: as before the menu rule, its Menu and lobby beside the list's what changing neither count
        Assert.Equal((531, 31), (c.ListWithinGraph.GetValueOrDefault(CategoryKind.What), c.ListBeyondGraph.GetValueOrDefault(CategoryKind.What)));
        var list = ClipIndexList.Embedded()!;
        int compared = 0;
        foreach (var (id, l) in list.Clips)
        {
            if (l.Who.Count == 0 || X.Find(id) is not { } e || e.SourceOf(CategoryKind.Who) != CategorySource.Graph) continue;
            var sure = e.Who.Where(u => !e.WhoProbably.Contains(u.Name)).Select(u => u.Name).Order(StringComparer.Ordinal).ToList();
            Assert.True(sure.SequenceEqual(l.Who.Order(StringComparer.Ordinal)), $"{e.ClipName}: the list says {string.Join(", ", l.Who)}, the graph's sure users {string.Join(", ", sure)}");
            compared++;
        }
        Assert.Equal(587, compared);
        // the 19 heavies' deaths are Enemy alone (every heavy's record is hostile; the teammates' death machine's share stays the
        // graph's, as probable)
        var heavies = X.Entries.Where(e => e.ClipName.StartsWith("pmc_hvy_", StringComparison.OrdinalIgnoreCase) && list.Clips.TryGetValue(e.ClipId, out var l)
            && l.Who.Count > 0).ToList();
        Assert.Equal(19, heavies.Count);
        Assert.All(heavies, e => Assert.Equal([Enemy], list.Clips[e.ClipId].Who));
        // Vehicle and Carry and drag hold the grab machines' 32 carry clips (Carry and drag: 29 whose names say carry, 3 carried) and
        // their 22 carry exits (Vehicle) besides
        // Menu and lobby: the 37 clips no layer played in the world names, only the menus' character's layer (the lobby poses, the
        // character screen's idles, draws and stows, two emotes and three whole launcher reloads), by the graph, beside the what the best
        // source gives (16 of them keep one: an idle, a stow, an emote, a reload); and the list's 4 victory poses of the squad's end screen
        Assert.Equal([2_981, 488, 1_291, 33, 237, 265, 557, 851, 999, 450, 381, 98, 783, 177, 368, 67, 41, 907], ClipCategories.What.Select(What));
        Assert.Equal(37, X.Entries.Count(e => e.What.Any(w => w.Name == MenuAndLobby && w.Source == CategorySource.Graph)));
        Assert.Equal(4, X.Entries.Count(e => e.What.Any(w => w.Name == MenuAndLobby && w.Source == CategorySource.ToolList)));
        Assert.Equal(16, X.Entries.Count(e => e.What.Count > 1 && e.What.Any(w => w.Name == MenuAndLobby && w.Source == CategorySource.Graph)));
        Assert.All(X.Entries.Where(e => e.In(CategoryKind.What, MenuAndLobby)), e => Assert.Equal([Player], e.Names(CategoryKind.Who)));
        // every who of the graph names its part of the graph, through a narrowing machine where one narrowed it
        Assert.All(X.Entries.SelectMany(e => e.Who).Where(w => w.Source == CategorySource.Graph), w => Assert.False(string.IsNullOrEmpty(w.Part)));
        Assert.Contains(X.Entries.SelectMany(e => e.Who), w => w.Part?.Contains("(GEN_NPC-LayerGraph), through Military-", StringComparison.Ordinal) == true);
        Assert.Contains(X.Entries.SelectMany(e => e.Who), w => w.Part?.Contains("(GEN_NPC-LayerGraph), through SamFisher_", StringComparison.Ordinal) == true);
        Assert.Contains(X.Entries.Where(e => e.WhoNarrowedByName).SelectMany(e => e.Who), w => w.Source == CategorySource.Graph && w.Part is not null);
        // the menu rule's Menu and lobby beside another what doesn't take the decision: the list and the name keep theirs (as without the rule),
        // the graph gains only the 21 clips nothing placed before
        Assert.Equal((6_533, 74, 2_424, 907),(c.Decided(CategoryKind.What, CategorySource.Graph), c.Decided(CategoryKind.What, CategorySource.ToolList),
            c.Decided(CategoryKind.What, CategorySource.Name), c.Uncategorised(CategoryKind.What)));
        // no civilian is hostile
        Assert.DoesNotContain(X.Entries, e => e.ClipName.StartsWith("civ_", StringComparison.OrdinalIgnoreCase) && e.In(CategoryKind.Who, Enemy));
        // of the 412 civilians' clips, the 16 the teammates' own dodge layer names too are Teammates and NPC
        Assert.Equal(412, X.Entries.Count(e => e.ClipName.StartsWith("civ_", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(396, X.Entries.Count(e => e.ClipName.StartsWith("civ_", StringComparison.OrdinalIgnoreCase) && e.Names(CategoryKind.Who).SequenceEqual([Npc])));
        Assert.Equal(131, c.WhatByContainer);
        Assert.Equal(642, c.ReadableNames);
        // each kind: every clip decided by one source or uncategorised
        foreach (var kind in new[] { CategoryKind.Who, CategoryKind.What })
            Assert.Equal(c.Clips, Enum.GetValues<CategorySource>().Sum(s => c.Decided(kind, s)) + c.Uncategorised(kind));
    }

    /// <summary>A sure who of the graph is sure because a document of one layer alone gives it: its reason (the clip info's hover)
    /// names such a document, never a machine several layers share that also gives it.</summary>
    [GameDataFact]
    public void ASureUsersReasonNamesTheDocumentOfOneLayerThatMakesItSure()
    {
        int sure = 0;
        foreach (var e in X.Entries)
        {
            if (!X.Facts.Clips.TryGetValue(e.ClipId, out var cf)) continue;
            var single = cf.Documents.Where(d => !X.DocumentPlacements[d].Shared).Select(d => X.Facts.Documents[d].Name).ToHashSet(StringComparer.Ordinal);
            foreach (var c in e.Who.Where(c => c.Source == CategorySource.Graph && !e.WhoProbably.Contains(c.Name)))
            {
                Assert.True(single.Any(n => c.Reason.StartsWith(n + " names it", StringComparison.Ordinal)), $"{e.ClipName}: {c.Name}: {c.Reason}");
                sure++;
            }
        }
        Assert.True(sure > 5_000, $"{sure} sure users of the graph");
        // the face idle: Player is sure by the menu character's own layer, though the shared custom-face machine names it first
        var face = X.Entries.Single(e => e.ClipName == "Mch_Face_Idle_Neutral01");
        Assert.DoesNotContain("CustomFacial", face.Who.Single(c => c.Name == Player).Reason);
    }

    [GameDataFact]
    public void TheGrabMachinesCarryClipsAreCarryAndDrag()
    {
        // the clips whose names say carry that the shared grab machines name as Melee and takedown: the 22 carry exits from vehicles,
        // the 4 handgun aims while carrying and the 3 releases of a carry. Each also gets Carry and drag, from the name (the 2 others
        // the grab machine names, the standing and running carries, the carry machine places as ever)
        var carryAndMelee = X.Entries.Where(e => NameRules.Words(e.ClipName).Contains("carry") && e.In(CategoryKind.What, MeleeAndTakedown)).ToList();
        var byName = carryAndMelee.Where(e => e.What.Any(c => c.Name == CarryAndDrag && c.Source == CategorySource.Name)).ToList();
        Assert.Equal(29, byName.Count);
        Assert.Equal(["mca_fm_std_V0_N_carry", "mca_fm_std_V2_N_carry"], carryAndMelee.Except(byName).Select(e => e.ClipName).Order(StringComparer.Ordinal));
        Assert.All(carryAndMelee.Except(byName), e => Assert.All(e.What, c => Assert.Equal(CategorySource.Graph, c.Source)));
        Assert.Equal(22, byName.Count(e => e.ClipName.Contains("_carry_exit_", StringComparison.Ordinal)));
        Assert.Equal(4, byName.Count(e => e.ClipName.StartsWith("mca_ale_hg_carry_V0_aim_pan", StringComparison.Ordinal)));
        Assert.Equal(["mca_release_carry_npc", "mca_release_carry_throw", "mca_release_carry_tm8"],
            byName.Where(e => e.ClipName.Contains("release", StringComparison.Ordinal)).Select(e => e.ClipName).Order(StringComparer.Ordinal));
        Assert.All(byName, e =>
        {
            var carry = Assert.Single(e.What, c => c.Name == CarryAndDrag);
            Assert.Equal(CategorySource.Name, carry.Source);
            Assert.Equal(CategorySource.Graph, e.What.Single(c => c.Name == MeleeAndTakedown).Source);
            Assert.Equal(CategorySource.Graph, e.SourceOf(CategoryKind.What));
        });
        // no clip whose name says carry or carried lacks Carry and drag now
        Assert.DoesNotContain(X.Entries, e => NameRules.Words(e.ClipName).Intersect(["carry", "carried"]).Any() && !e.In(CategoryKind.What, CarryAndDrag));
        // the reason quotes the word the name holds
        Assert.All(byName, e => Assert.StartsWith("the name holds \"carry\"", e.What.Single(c => c.Name == CarryAndDrag).Reason));
        foreach (var name in new[] { "npc_release_carried", "npc_release_carried_throw", "tm8_release_carried" })
        {
            var carried = Clip(name).What.Single(c => c.Name == CarryAndDrag);
            Assert.Equal(CategorySource.Name, carried.Source);
            Assert.StartsWith("the name holds \"carried\"", carried.Reason);
        }
        // the carry exits are Vehicle as the carries into the same vehicles are by the graph (light, medium, heli), or by the name's
        // vehicle word (truck, which no carry entry names)
        var exits = X.Entries.Where(e => e.ClipName.Contains("_carry_exit_", StringComparison.Ordinal)).ToList();
        Assert.Equal(22, exits.Count);
        Assert.All(exits, e => Assert.Equal([Vehicle, MeleeAndTakedown, CarryAndDrag], e.Names(CategoryKind.What)));
        var heli = Clip("mca_carry_exit_heli_right");
        Assert.Contains("carry_entry_heli", heli.What[0].Reason);
        Assert.Contains("Put_in_Vehicle", heli.What[0].Reason);
        Assert.EndsWith("\"truck\" is a vehicle word of the names (a truck)", Clip("npc_carry_exit_truck_swing_door_left").What[0].Reason);
        // the matching carry entries, by the graph alone, as before
        var entries = X.Entries.Where(e => e.ClipName.Contains("_carry_entry_", StringComparison.Ordinal)).ToList();
        Assert.Equal(18, entries.Count);
        Assert.All(entries, e => Assert.All(e.What, c => Assert.Equal(CategorySource.Graph, c.Source)));
        Assert.All(entries, e => Assert.Equal([Vehicle, CarryAndDrag], e.Names(CategoryKind.What)));
    }

    [GameDataFact]
    public void KnownClips()
    {
        var list = ClipIndexList.Embedded()!;
        // the greeting emote: the player's emote layer and the menu character's layer, and no NPC's
        var hello = Clip("mca_emote_Hello");
        Is(hello, CategoryKind.Who, CategorySource.Graph, Player);
        Is(hello, CategoryKind.What, CategorySource.Graph, Emote);
        Assert.Equal([MenuLayer, Emotes], hello.Documents.Select(d => d.Id).Order());
        Assert.Equal(list.Clips[hello.ClipId].Name, hello.ReadableName);
        // the hub's holstered walk: a motion field of the player's hub
        var walk = Clip("mcm_nwp_fm_std_V1_cycle");
        Is(walk, CategoryKind.Who, CategorySource.Graph, Player);
        Is(walk, CategoryKind.What, CategorySource.Graph, Locomotion);
        Assert.Contains(GraphRules.MotionFieldReason, walk.What[0].Reason);
        Assert.Equal([Hub], walk.Documents.Select(d => d.Id));
        // a standing death: the generic NPC layer's death machine (soldiers and civilians) and the teammates' copy
        var death = Clip("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01");
        Is(death, CategoryKind.Who, CategorySource.Graph, Teammates, Enemy, Npc);
        Is(death, CategoryKind.What, CategorySource.Graph, Death);
        Assert.Equal([GenericDeaths, TeammatesDeaths], death.Documents.Select(d => d.Id).Order());
        // every clip of the generic death machine plays for the enemies, every one of the teammates' copy for the teammates, sure
        var generic = X.Entries.Where(e => e.Documents.Any(d => d.Id == GenericDeaths)).ToList();
        var teammates = X.Entries.Where(e => e.Documents.Any(d => d.Id == TeammatesDeaths)).ToList();
        Assert.Equal((228, 208), (generic.Count, teammates.Count));
        Assert.All(generic, e => Assert.True(e.In(CategoryKind.Who, Enemy) && e.In(CategoryKind.What, Death), e.ClipName));
        // (but the heavies' deaths: a teammate is never a heavy, so the teammates play those only probably)
        Assert.All(teammates, e => Assert.True(e.In(CategoryKind.Who, Teammates) && e.In(CategoryKind.What, Death)
            && e.WhoProbably.Contains(Teammates) == e.ClipName.StartsWith("pmc_hvy_", StringComparison.OrdinalIgnoreCase), e.ClipName));
        // the generic layer's Military (soldiers hostile and allied), Civilian and ally machines
        Is(Clip("pmc_rif_m_col_fm_std_V1_N_rp_TRC_col_fm_std_V1_S_TurnR"), CategoryKind.Who, CategorySource.Graph, Enemy, Npc);
        Is(Clip("vip_sam_col_fm_std_V1_N_rp_TRC_col_fm_std_V1_S_TurnR"), CategoryKind.Who, CategorySource.Graph, Npc);
        Is(Clip("civ_f_neutral_fm_std_V0_N_TO_neutral_fm_std_V0_SE"), CategoryKind.Who, CategorySource.Graph, Npc);
        Is(Clip("civ_f_neutral_fm_std_V0_N_TO_neutral_fm_std_V0_SE"), CategoryKind.What, CategorySource.Graph, Locomotion);
        // the victory poses stand in the general machine itself, which serves every character: the list says whose they are
        var pose = Clip("Mca_Std_Healthy_VictoryPose_MVP");
        Is(pose, CategoryKind.Who, CategorySource.ToolList, Player);
        Is(pose, CategoryKind.What, CategorySource.ToolList, Pose, MenuAndLobby);
        // the fly mode: nothing in the graph or the list says whose or what; the name's family says the player's
        var fly = Clip("mch_flymode_superman");
        Is(fly, CategoryKind.Who, CategorySource.Name, Player);
        Is(fly, CategoryKind.What, null);
        // the containers don't overrule what a clip is: a crouched heavy hit, an aim sweep and idles of the motion-field machines, a stun of
        // the very-low-health movement, a light hit the teammates' locomotion layer names
        Is(Clip("MCH_AR_Crh_V0_Hit_Heavy_Front_01"), CategoryKind.What, CategorySource.ToolList, HitReaction);
        Is(Clip("HG_Std_AimPan_Leaning_M_EW_LHS"), CategoryKind.What, CategorySource.Name, WeaponHandling);
        Assert.All(new[] { "fm_std_V0_idle_cool_01", "fm_std_V0_idle_cool_02", "fm_std_V0_idle_cool_04", "fm_std_V0_ale_idle_neutral_03" },
            n => Is(Clip(n), CategoryKind.What, Clip(n).SourceOf(CategoryKind.What), Idle));
        // the wounded band names healthy clips beside the wounded's own: an idle, suppressed holds and sun-protection fidgets aren't
        // injured; an idle beside its motion field is the field's locomotion; the wounded's own keep it
        Is(Clip("fm_std_V0_idle_cool_01"), CategoryKind.What, CategorySource.ToolList, Idle);
        Is(Clip("fm_std_V0_ale_idle_neutral_01"), CategoryKind.What, CategorySource.Graph, Locomotion);
        Assert.All(new[] { "MCH_AR_Std_V0_Suppressed_01", "MCH_AR_Std_V0_Suppressed_02", "MCH_AR_Std_V0_Suppressed_03" }, n => Is(Clip(n), CategoryKind.What, null));
        Assert.All(new[] { "In", "Loop", "Out" }, s => Is(Clip($"Mcm_Fm_Std_AR_V0_Fidget_Protection_Sun_Protection_{s}"), CategoryKind.What, CategorySource.Graph, Idle));
        Assert.All(new[] { "AR_Std_LowHealthFM_V0", "Crh_CoolFM_V0_WoundedFM", "MCH_AR_Std_WoundedFM_V0_ale_Cough_01", "HG_Crh_CoolFM_Stop_V1_RP_LowHealth" },
            n => Is(Clip(n), CategoryKind.What, CategorySource.Graph, InjuryAndDowned));
        // no speed's locomotion for a pose or a suppressed hold
        Is(Clip("std_cool_HG_Pose_V2"), CategoryKind.What, CategorySource.Name, Pose);
        Is(Clip("MCH_HG_Crh_V2_Suppressed_04"), CategoryKind.What, null);
        // the moves into low cover are cover, not idles
        var toLow = X.Entries.Where(e => e.Documents.Any(d => d.Name.StartsWith("TO_IdleLow-", StringComparison.Ordinal))).ToList();
        Assert.Equal(44, toLow.Count);
        Assert.All(toLow, e => Assert.True(e.In(CategoryKind.What, Cover) && !e.In(CategoryKind.What, Idle), e.ClipName));
        // civilians outside the Civilian machine, in the generic layer's own reactions and dodges: NPC, the generic layer's (a document of
        // one layer, so the graph's and sure), its Enemy taken away by the name's family
        foreach (var n in new[] { "civ_f_col_std_V0_N_Reaction_Hello_01", "civ_m_neutral_fm_std_V0_N_vehicle_CantDodge_N_01" })
        {
            Assert.True(Clip(n).WhoNarrowedByName);
            Assert.Contains("played by the generic NPC layer", Clip(n).Who.Single(c => c.Name == Npc).Reason);
            Assert.Equal(CategorySource.Graph, Clip(n).Who.Single(c => c.Name == Npc).Source);
        }
        Is(Clip("civ_f_col_std_V0_N_Reaction_Hello_01"), CategoryKind.Who, CategorySource.Graph, Npc);
        Assert.Equal("NPC", ClipUsers.Of(Clip("civ_f_col_std_V0_N_Reaction_Hello_01")).Text);
        // the teammates' own dodge layer names the civilians' dodge too: by the graph, but a teammate is never a civilian, so only probably
        var dodge = Clip("civ_m_neutral_fm_std_V0_N_vehicle_CantDodge_N_01");
        Is(dodge, CategoryKind.Who, CategorySource.Graph, Teammates, Npc);
        Assert.EndsWith("a teammate is never a civilian, so the teammates only probably play it", dodge.Who[0].Reason);
        Assert.Equal("NPC; probably also Teammates", ClipUsers.Of(dodge).Text);
        Assert.Equal(["probably Teammates, from the game's graph: the AI teammates' layer (SPE_TM8-LayerGraph)",
            "NPC, from the game's graph: the generic NPC layer (GEN_NPC-LayerGraph)"],
            ClipIndexView.CategoryLines(dodge, CategoryKind.Who).Select(l => l.Text));
        Is(Clip("Mcm_Fm_Std_AR_V0_SonicStun_In"), CategoryKind.What, CategorySource.Graph, InjuryAndDowned);
        var light = Clip("pmc_rif_m_ale_ros_std_V0_N_hit_regular_LegLeft_front");
        Is(light, CategoryKind.What, CategorySource.Graph, HitReaction);
        Is(light, CategoryKind.Who, CategorySource.Graph, Teammates, Enemy, Npc);
        // a civilian cowering under fire and a teammate's order gesture: nothing says what they are
        Is(Clip("civ_f_fear_prostrate_fm_crh_V0_N_var04_underfire_01"), CategoryKind.What, null);
        Is(Clip("tm8_rif_m_ale_fm_std_V0_Acceptal_01"), CategoryKind.What, null);
        // a civilian's turn on the spot: the locomotion layer's, as its name moves (TO)
        Assert.True(Clip("civ_f_neutral_fm_std_V0_N_TO_neutral_fm_std_V0_SE").WhatFromContainer);
        // in the conversation machine the player plays the main character's clips, the others the npc_ ones
        Is(Clip("npc_AR_Conversation_Enter_FRONT"), CategoryKind.Who, CategorySource.Name, Enemy, Npc);
        Is(Clip("mca_noweapon_Conversation_Calm_Wait_05"), CategoryKind.Who, CategorySource.Name, Player);
        // the heavy archetype's detection clip under the soldiers' machine: every heavy record is hostile (the machine is the generic
        // layer's alone, so its Enemy is the graph's)
        Is(Clip("pmc_hvy_m_ale_fm_std_V0_N_DETECTION_SURPRISED_ale_fm_std_V0_W"), CategoryKind.Who, CategorySource.Graph, Enemy);
        // only the special NPCs' layer names it: its main-character family says nothing
        Is(Clip("mca_ale_ros_std_V2_N_trav_100_IN_ClimbUp"), CategoryKind.Who, null);
        // every motion-field clip is locomotion, by the graph
        Assert.All(X.Entries.Where(e => Facts.Clips[e.ClipId].MotionFieldDocuments.Count > 0),
            e => Assert.True(e.In(CategoryKind.What, Locomotion) && e.SourceOf(CategoryKind.What) == CategorySource.Graph, e.ClipName));
        // no clip has a readable name the list doesn't give, and no unsure entry shows
        Assert.All(X.Entries.Where(e => e.ReadableName is not null), e => Assert.Equal(list.Clips[e.ClipId].Name, e.ReadableName));
    }

    [GameDataFact]
    public void TheTeammatesLayerTellsTheTeammatesApart()
    {
        const ulong TeammatesLayer = 0x000001A2B8D96B35, TeammatesLocomotion = 0x000001A2B8DBF326, TeammatesHits = 0x000001A2B8DBF323,
            RiflemanLightHits = 0x000001A2B8D4FA27;
        // the teammates' layer reaches 21 documents of its own (its copies of the death, hit, cover, weapon, traversal and locomotion
        // machines), each giving the teammates alone, never NPC
        var own = X.DocumentPlacements.Select((p, i) => (p, i)).Where(x => x.p.Branches.SetEquals(["branch.teammates"])).ToList();
        Assert.Equal(21, own.Count);
        Assert.Contains(own, x => X.Facts.Documents[x.i].Id == TeammatesLayer);
        Assert.All(own, x => Assert.Equal([Teammates], x.p.Who.Keys));
        // the teammates' death and hit machines name 238 clips, every one the teammates' by the graph, sure, but the 19 heavies' deaths
        // (a teammate is never a heavy): those only probably
        var deathsAndHits = X.Entries.Where(e => e.Documents.Any(d => d.Id is TeammatesDeaths or TeammatesHits)).ToList();
        Assert.Equal(238, deathsAndHits.Count);
        Assert.Equal(19, deathsAndHits.Count(e => ClipUsers.Of(e).Probably.Contains(Teammates)));
        Assert.All(deathsAndHits, e => Assert.Contains(Teammates, e.ClipName.StartsWith("pmc_hvy_", StringComparison.OrdinalIgnoreCase) ? ClipUsers.Of(e).Probably : ClipUsers.Of(e).Sure));
        // a heavy's death: Enemy by the generic layer's machine, sure; the teammates' copy names it too, probably
        var heavy = deathsAndHits.First(e => e.ClipName.StartsWith("pmc_hvy_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Enemy; probably also Teammates", ClipUsers.Of(heavy).Text);
        // their locomotion layer names 41, all 30 of the riflemen's light hits among them: each reads Teammates, Enemy and NPC, sure
        var loco = X.Entries.Where(e => e.Documents.Any(d => d.Id == TeammatesLocomotion)).ToList();
        Assert.Equal(41, loco.Count);
        var rifleman = X.Entries.Where(e => e.Documents.Any(d => d.Id == RiflemanLightHits)).ToList();
        Assert.Equal(30, rifleman.Count);
        Assert.All(rifleman, e =>
        {
            Assert.Contains(e, loco);
            Assert.Equal("Teammates, Enemy and NPC", ClipUsers.Of(e).Text);
            Assert.True(ClipUsers.Of(e).Several);
        });
        // a clip is NPC only where a document of the other NPCs places it, never through the teammates' alone
        Assert.All(X.Entries.Where(e => e.In(CategoryKind.Who, Npc) && e.SourceOf(CategoryKind.Who) == CategorySource.Graph), e =>
            Assert.Contains(Facts.Clips[e.ClipId].Documents, d => X.DocumentPlacements[d].Who.ContainsKey(Npc)));
        // the teammates' family and the clips their documents alone name: Teammates, never NPC
        Assert.All(X.Entries.Where(e => e.ClipName.StartsWith("tm8_", StringComparison.OrdinalIgnoreCase) && !e.IsUncategorised(CategoryKind.Who)),
            e => Assert.True(e.In(CategoryKind.Who, Teammates) && !e.In(CategoryKind.Who, Npc), e.ClipName));
        // a shared machine's characters are only probable where the name doesn't narrow them: the bike's mount (vehicles) all of them;
        // the face's idle the menu character's (the player's, sure) and the custom-face machine's others, probably
        Assert.Equal("probably Player, Teammates, Enemy and NPC", ClipUsers.Of(Clip("Mch_Bik_Driv_IntoR")).Text);
        Assert.Equal("Player; probably also Teammates, Enemy and NPC", ClipUsers.Of(Clip("Mch_Face_Idle_Neutral01")).Text);
        // a player clip the player's layer alone names has one user and no notice
        Assert.Equal("Player", ClipUsers.Of(Clip("mcm_nwp_fm_std_V0_idle_03")).Text);
        Assert.Null(ClipUsers.Of(Clip("mcm_nwp_fm_std_V0_idle_03")).Notice);
        // the teammates' weapon layer names many of the player's reloads: Player and Teammates, both sure
        Assert.Equal("Player and Teammates", ClipUsers.Of(Clip("Mch_AR_Std_ReloadNormal_Tar21")).Text);
        // the name alone: the player probably, and a notice, as the tool isn't sure nobody else plays it
        Assert.Equal("probably Player", ClipUsers.Of(Clip("mch_flymode_superman")).Text);
        Assert.StartsWith("The tool isn't sure who plays this clip (probably Player)", ClipUsers.Of(Clip("mch_flymode_superman")).Notice);
        // the teammates the families of heavies and civilians rule out: 35 clips the teammates' own documents name (19 heavy deaths, 16
        // civilian dodges), each Teammates only probably
        var ruledOut = X.Entries.Where(e => NameRules.FamilyOf(e.ClipName)?.NoTeammates is not null && e.In(CategoryKind.Who, Teammates)).ToList();
        Assert.Equal(35, ruledOut.Count);
        Assert.Equal((19, 16), (ruledOut.Count(e => e.ClipName.StartsWith("pmc_hvy_", StringComparison.OrdinalIgnoreCase)), ruledOut.Count(e => e.ClipName.StartsWith("civ_", StringComparison.OrdinalIgnoreCase))));
        Assert.All(ruledOut, e => Assert.Contains(Teammates, e.WhoProbably));
    }

    /// <summary>The clip info's Who lines and its Played by agree on every clip of the install: a user labelled as the name's is always
    /// "probably"; a sure one is the tool's list's, or the graph's with a document of one layer alone giving it; a "probably" of the
    /// graph's is only a shared machine's, or the teammates a family rules out. The lines mark "probably" exactly where Played by does.</summary>
    [GameDataFact]
    public void TheWhoLabelsAndPlayedByAgreeOnEveryClip()
    {
        Assert.Equal(9_938, X.Entries.Count);
        int named = 0;
        foreach (var e in X.Entries)
        {
            var users = ClipUsers.Of(e);
            var single = Facts.Clips.TryGetValue(e.ClipId, out var cf)
                ? cf.Documents.Where(d => !X.DocumentPlacements[d].Shared).SelectMany(d => X.DocumentPlacements[d].Who.Keys).ToHashSet() : [];
            bool ruledOut = NameRules.FamilyOf(e.ClipName)?.NoTeammates is not null;
            foreach (var c in e.Who)
            {
                bool probably = users.Probably.Contains(c.Name);
                Assert.True(probably != users.Sure.Contains(c.Name), $"{e.ClipName} {c.Name}");
                bool expected = c.Source switch
                {
                    CategorySource.Name => true,
                    CategorySource.ToolList => false,
                    _ => !single.Contains(c.Name) || ruledOut && c.Name == Teammates,
                };
                Assert.True(expected == probably, $"{e.ClipName}: {c.Name} ({c.SourceLabel}) is {(probably ? "probably" : "sure")} in Played by");
                if (c.Source == CategorySource.Name) named++;
            }
            // the clip info's lines say "probably" for exactly the users Played by says it of, and Played by names each line's user
            var lines = ClipIndexView.CategoryLines(e, CategoryKind.Who);
            if (e.Who.Count == 0) { Assert.Equal(ClipUsers.UncategorisedText, users.Text); continue; }
            // a line's kinds: before ", from the game's graph:" or " (", "A", "A and B" or "A, B and C"
            static IEnumerable<string> Kinds(string text)
            {
                int end = text.IndexOf(", from the game's graph:", StringComparison.Ordinal) is var g and >= 0 ? g : text.IndexOf(" (", StringComparison.Ordinal);
                return text[..end].Split([", ", " and "], StringSplitOptions.None);
            }
            Assert.Equal(users.Probably.ToHashSet(), lines.Where(l => l.Text.StartsWith("probably ", StringComparison.Ordinal)).SelectMany(l => Kinds(l.Text["probably ".Length..])).ToHashSet());
            Assert.Equal(users.Sure.ToHashSet(), lines.Where(l => !l.Text.StartsWith("probably ", StringComparison.Ordinal)).SelectMany(l => Kinds(l.Text)).ToHashSet());
            // a who of the graph names its part of the graph (one line for the kinds of the same part), any other its source in brackets
            Assert.Equal(e.Who.Count, lines.Sum(l => Kinds(l.Text.StartsWith("probably ", StringComparison.Ordinal) ? l.Text["probably ".Length..] : l.Text).Count()));
            Assert.All(e.Who, c => Assert.Contains(lines, l => l.Text.Contains(c.Source == CategorySource.Graph ? $"{c.Name}, from the game's graph: " : $"{c.Name} (", StringComparison.Ordinal)
                || c.Source == CategorySource.Graph && l.Text.Contains($"{c.Name} and ", StringComparison.Ordinal) || c.Source == CategorySource.Graph && l.Text.Contains($"{c.Name}, ", StringComparison.Ordinal)));
            string playedBy = ClipIndexView.PlayedBy(e);
            Assert.All(e.Who, c => Assert.Contains(c.Name, playedBy));
            if (users.Probably.Count > 0 && users.Sure.Count > 0) Assert.Contains("; probably also ", playedBy);
            else if (users.Probably.Count > 0) Assert.StartsWith("Probably ", playedBy);
            else Assert.DoesNotContain("probably", playedBy, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(named > 0);
    }

    [GameDataFact]
    public void TheCacheIsBuiltReusedAndBuiltAgain()
    {
        var g = f.Install;
        string dir = Path.Combine(AppContext.BaseDirectory, "index-cache-game", Guid.NewGuid().ToString("N"));
        var cache = new ClipIndexCache(dir);
        Assert.StartsWith(AppContext.BaseDirectory, cache.FileFor(g), StringComparison.OrdinalIgnoreCase);
        try
        {
            // first open: no cache, so built, with the progress to the whole, and saved
            var reports = new Reports();
            var built = cache.Open(g, reports);
            Assert.Equal(CacheOutcome.Built, built.Outcome);
            Assert.Null(built.SaveError);
            Assert.True(File.Exists(cache.FileFor(g)));
            Assert.Equal(1.0, reports.Values[^1]);
            for (int i = 1; i < reports.Values.Count; i++) Assert.True(reports.Values[i] >= reports.Values[i - 1]);
            Assert.True(built.GraphTime < TimeSpan.FromSeconds(30), $"built in {built.GraphTime.TotalSeconds:F1} s");
            // second: reused, the same index
            var reused = cache.Open(g, reports);
            Assert.Equal(CacheOutcome.Reused, reused.Outcome);
            Same(built.Index, reused.Index);
            Same(X, reused.Index);
            // a cache of other files (another forge length): built again, and written for these
            var fp = IndexFingerprint.Of(g);
            using (var fs = File.Create(cache.FileFor(g))) ClipIndexCache.Write(fs, fp with { ForgeLength = fp.ForgeLength + 1 }, built.Index.Facts);
            Assert.Equal(CacheOutcome.RebuiltChanged, cache.Open(g).Outcome);
            Assert.Equal(CacheOutcome.Reused, cache.Open(g).Outcome);
            // facts read another way: built again; the rules apply to the cached facts on every open, so other rules need no new read
            using (var fs = File.Create(cache.FileFor(g))) ClipIndexCache.Write(fs, fp with { FactsVersion = fp.FactsVersion - 1 }, built.Index.Facts);
            Assert.Equal(CacheOutcome.RebuiltChanged, cache.Open(g).Outcome);
            Assert.Equal(CacheOutcome.Reused, cache.Open(g).Outcome);
            // damaged: cut short, or not this tool's file at all; built again and the index the same
            var bytes = File.ReadAllBytes(cache.FileFor(g));
            File.WriteAllBytes(cache.FileFor(g), bytes[..(bytes.Length / 2)]);
            var again = cache.Open(g);
            Assert.Equal(CacheOutcome.RebuiltUnreadable, again.Outcome);
            Same(X, again.Index);
            File.WriteAllText(cache.FileFor(g), "not a cache");
            Assert.Equal(CacheOutcome.RebuiltUnreadable, cache.Open(g).Outcome);
            Assert.Equal(CacheOutcome.Reused, cache.Open(g).Outcome);
            // only the one file, and no temporary left behind
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    static void Same(ClipIndex a, ClipIndex b)
    {
        Assert.Equal(a.Entries.Count, b.Entries.Count);
        for (int i = 0; i < a.Entries.Count; i++)
        {
            var (x, y) = (a.Entries[i], b.Entries[i]);
            Assert.Equal(x.ClipId, y.ClipId);
            Assert.Equal(x.Who, y.Who);
            Assert.Equal(x.What, y.What);
            Assert.Equal(x.ReadableName, y.ReadableName);
            Assert.Equal(x.Documents.Select(d => d.Id), y.Documents.Select(d => d.Id));
        }
    }
}
