using System.Text.RegularExpressions;
using static GrbAnimTool.Core.Index.ClipCategories;

namespace GrbAnimTool.Core.Index;

/// <summary>A layer the graph's general machine picks by the character it animates: every document reached from it, by its children and
/// its references, plays for that character. The root is found by its record ID and checked by its name, so a graph of other game files
/// where the ID names another document fires nothing.</summary>
public sealed record BranchRule(string Id, ulong RootId, string RootName, IReadOnlyList<string> Who, string Reason)
{
    /// <summary>The layer in a few plain words, for the clip info's part of the graph ("the menus' character's layer").</summary>
    public string Label { get; init; } = "";
}

/// <summary>Within a branch, a machine that narrows who plays what it reaches: on the way down from <see cref="Branch"/>, a document whose
/// name matches takes <see cref="Who"/> in place of what the way down carried, for itself and all it reaches.</summary>
public sealed record RefineRule(string Id, string Branch, Regex Pattern, IReadOnlyList<string> Who, string Reason);

/// <summary>A document whose name says what its clips are: the categories for every clip it names, and with <see cref="Inherited"/> for
/// every clip a document below it by the tails names (its children, theirs, and so on: the machines it holds), never through a reference.</summary>
public sealed record DocumentRule(string Id, Regex Pattern, IReadOnlyList<string> What, string Reason, bool Inherited = false)
{
    /// <summary>A container's rule: the document (with <see cref="Inherited"/>, all it holds) names clips of many kinds, so its name
    /// doesn't say what any one clip is. Its categories are weak: they fill the what of a clip only where no other rule of the graph, the
    /// tool's list or the clip's name places it. A rule without it is strong: the machine's name says what each of its clips is.</summary>
    public bool Weak { get; init; }
}

/// <summary>Who and what one document's clips are, by the graph's rules: each category with the rule that gave it and, for a category
/// that came down from above, the document whose rule it is.</summary>
public sealed class DocumentPlacement
{
    /// <summary>Who, with the reason: the branch's (and a refining machine's) and the branch's root.</summary>
    public Dictionary<string, string> Who { get; } = new(StringComparer.Ordinal);
    /// <summary>For each who-category, the part of the graph that gave it: the layer in plain words with its root's name, and the refining
    /// machine the way down passed, if any ("the generic NPC layer (GEN_NPC-LayerGraph), through Military-StateMachine").</summary>
    public Dictionary<string, string> WhoPath { get; } = new(StringComparer.Ordinal);
    /// <summary>What by the strong rules, with the reason: the rule's, and the document whose rule it is when not this one.</summary>
    public Dictionary<string, string> What { get; } = new(StringComparer.Ordinal);
    /// <summary>What by the weak (container) rules (<see cref="DocumentRule.Weak"/>), with the reason, the same way.</summary>
    public Dictionary<string, string> WeakWhat { get; } = new(StringComparer.Ordinal);
    /// <summary>The IDs of the rules that fired on this document, for the census.</summary>
    public SortedSet<string> Rules { get; } = new(StringComparer.Ordinal);
    /// <summary>The IDs of the branches that reach the document: more than one for a machine several layers play by reference.</summary>
    public SortedSet<string> Branches { get; } = new(StringComparer.Ordinal);
    /// <summary>Whether several layers play the document by reference (<see cref="Branches"/>): its who is their union, as which of them
    /// plays which of its states isn't read, so each of its characters only probably plays a given clip of it.</summary>
    public bool Shared => Branches.Count > 1;
    /// <summary>The IDs of the refining machines the way down passed last before the document (<see cref="RefineRule"/>), if any.</summary>
    public SortedSet<string> Refinements { get; } = new(StringComparer.Ordinal);
    /// <summary>Whether the document stands under the NPC machine by the tails (<see cref="GraphRules.NpcMachineId"/>), so that only
    /// non-player characters play the clips it names.</summary>
    public bool UnderNpcMachine { get; internal set; }
}

/// <summary>The rules that read the graph's facts (<see cref="GraphFacts"/>) as categories, in the tool's own words. Each rule fires only on
/// a document's name or place, or on a clip's place in a motion field, and states why in its reason. A document whose role can't be tied
/// to a category by its name and place gives nothing: its clips are left to the tool's list and their names.
/// <para>Who: the general machine picks a layer by the character it animates. The player's layer, the menu character's layer, the
/// teammates' layer and the generic NPC layer are the branches (<see cref="Branches"/>); every document reached from one, through its
/// children and the machines it plays by reference, plays for that character. The teammates' layer gives Teammates, never NPC: NPC is
/// what the generic NPC layer places (the other NPCs, civilians and allies). The generic NPC layer serves soldiers and civilians alike
/// (its own machines split by them), so what it reaches outside that split plays for both; its Military and Civilian machines narrow it
/// (<see cref="Refinements"/>). The layer the NPC machine keeps for one archetype of NPC (<c>SPE_NPC</c>) and the dialogue machine,
/// which the general machine enters for any character in a dialogue, say nothing of who, nor does a document only GR_Graph's own list
/// reaches (some reference machines).</para>
/// <para>What: the machine names (<see cref="Documents"/>), and a clip a motion field names is locomotion (<see cref="MotionFieldReason"/>).
/// A container's rule (a layer whose clips are of many kinds, such as the NPCs' locomotion layer) is weak (<see cref="DocumentRule.Weak"/>):
/// the index uses it only for a clip that nothing else places.</para></summary>
public static class GraphRules
{
    /// <summary>The IDs of the downed and reviving rule and of the wounded bands' rule, which <see cref="NameRules.Unwounded"/> tells apart.</summary>
    public const string InjuryRule = "injury", WoundedBandRule = "injury.band";

    /// <summary>The close-combat and grab machines' rule, whose carry clips <see cref="NameRules.Carried"/> finds.</summary>
    public const string MeleeRule = "melee";

    /// <summary>The motion field rule's ID and reason. The motion fields are the game's motion-matched movement: every state that holds
    /// one is a standing or moving state of the locomotion, and only the locomotion machines hold them.</summary>
    public const string MotionFieldRule = "motion-field";
    public const string MotionFieldReason = "a motion field names it: the game's motion-matched locomotion";

    /// <summary>The NPC machine: the general machine enters it for every character but the player and the menus' (the layers of the
    /// teammates, of the generic NPCs and of the special NPCs stand under it). Found by its record ID and checked by its name.</summary>
    public const ulong NpcMachineId = 0x000001B151B953C4;
    public const string NpcMachineName = "NPC-StateMachine";

    static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>A whole word of a document's name: one of the '|'-separated alternatives, between the name's ends, '-' and '_'.</summary>
    static Regex Word(string alternatives) => R($"(?<![A-Za-z0-9])(?:{alternatives})(?![A-Za-z0-9])");

    public static readonly IReadOnlyList<BranchRule> Branches =
    [
        // the general machine's state MCH, entered for the player's own character
        new("branch.player", 0x000001544EA877D5, "MCH-LayerGraph", [Player], "the player's layer") { Label = "the player's layer" },
        // the general machine's state Avatar: the character the menus show, the player's own character
        new("branch.menu", 0x000001555E68546B, "Avatar-LayerGraph", [Player], "the layer of the character the menus show (the player's own)")
            { Label = "the menus' character's layer" },
        // the NPC machine's layer for the AI teammates; its machines and clips (tm8_) are the teammates'. It holds its own copies of the
        // NPCs' death, hit, cover, weapon, traversal and locomotion machines, so a clip they name plays for the teammates, apart from the
        // other NPCs (whose generic layer names its own copies); the machines it shares with other layers by reference give the union
        new("branch.teammates", 0x000001A2B8D96B35, "SPE_TM8-LayerGraph", [Teammates], "the AI teammates' layer") { Label = "the AI teammates' layer" },
        // the NPC machine's generic layer, for every NPC but the teammates: its own machines split into Military and Civilian ones, so
        // what it reaches outside them plays for soldiers and civilians alike
        new("branch.npc", 0x000001544EA9A050, "GEN_NPC-LayerGraph", [Enemy, Npc], "the generic NPC layer, shared by soldiers and civilians")
            { Label = "the generic NPC layer" },
    ];

    public static readonly IReadOnlyList<RefineRule> Refinements =
    [
        // the generic layer's split by kind of character. Its soldiers are not all enemies: the allied riflemen's records (relationship
        // ally: Rifleman_Ally, Rifleman_Homesteader, Rifleman_Outcast) have the same rifleman archetype as the hostile riflemen's, and
        // the Military machine holds the machine of an ally (Sam Fisher); nothing read ties the split to hostility, so Military keeps both
        new("refine.military", "branch.npc", R("^Military-"), [Enemy, Npc], "its machine for soldiers, hostile and allied alike"),
        new("refine.civilian", "branch.npc", R("^Civilian(_woman|_man)?-"), [Npc], "its machine for civilians"),
        // inside the Military machine: the machine named for Sam Fisher, who is on the player's side in the game
        new("refine.ally", "branch.npc", R("^SamFisher_"), [Npc], "its machine for Sam Fisher, an ally of the player"),
    ];

    public static readonly IReadOnlyList<DocumentRule> Documents =
    [
        // ---- locomotion
        // Weak, containers: the NPCs' locomotion layers hold their free-move, rose (moving while aiming) and prone machines, and with
        // them the generic layer's full-body machines (soldiers', Sam Fisher's, civilians', their cowering under fire among the rest);
        // the teammates' layer also names clips of its own: the riflemen's light hits, the teammates' order gestures and a weapon's
        // reparenting. The free-move and rose machines name aim sweeps, recoils and reference poses beside their moves.
        new("loco.layer", R("^Locomotion-LayerGraph$"), [Locomotion], "the NPCs' locomotion layer", Inherited: true) { Weak = true },
        new("loco.npc", R("^(FreeMove|Rosace)-|^ReferenceStateMachines-NPC_base_heavy_Loco$"), [Locomotion],
            "a locomotion machine (free movement, or the rose of directions a character moves in while aiming)", Inherited: true) { Weak = true },
        // MF: the motion-field movement machines, standing (V0) to sprinting (V3), and the healthy bands; only their own clips, as they
        // hold hit and fidget machines. Weak: beside the moves their motion fields name (strong, MotionFieldReason) they name aim and
        // look-at sweeps, idles, base holds and hits
        new("loco.mf", R("^(ReferenceStateMachines-)?MF_"), [Locomotion], "a motion-field movement machine, standing to sprinting") { Weak = true },
        new("loco.healthy", R("^ReferenceStateMachines-Healthy_"), [Locomotion], "a healthy character's movement band, standing to sprinting") { Weak = true },
        // weak too: the very-low-health movement names coughs, stuns, electric hits and fidgets beside its moves; the standing and
        // crouched rose (moving while aiming) the heavy hits and aim holds; the prone layers aim sweeps and idles; wading its aim sweeps
        new("loco.lowhealth", R("^Locomotion-VeryLowHealtrh$"), [Locomotion], "the very-low-health movement machine") { Weak = true },
        new("loco.rose", R("^StdCrhRos-"), [Locomotion], "moving while aiming, standing or crouched") { Weak = true },
        new("loco.prone", R("^(PRN_FM|PRN_Rosxx|Prone)-"), [Locomotion], "moving while prone") { Weak = true },
        new("loco.wading", R("^Wading-"), [Locomotion], "walking through water") { Weak = true },
        // strong: the machines that only move
        new("loco.machine", R("^(Locomotion-StateMachine|Default_Navigation-)"), [Locomotion], "a locomotion machine"),
        new("loco.stance", R("^(Std_To_Crouch|Crh_To_Std|StdCrhRos_TO_StdCrhFm|StdCrhFm_TO_StdCrhRos_DEV)-"), [Locomotion],
            "moving between standing and crouching, and between free movement and moving while aiming"),
        // ---- idle
        new("idle.fidget", Word("(TM8_)?Fidgets|NPC_layer_fidget"), [Idle], "fidgets: the small actions of a standing character"),
        new("idle.passive", Word("PIdle_pan|NPC_layer_pidle_cool"), [Idle], "passive idles"),
        // not TO_IdleLow: its clips are the moves into low cover (its cover layer gives them Cover), not idles
        new("idle.machine", R("^(Idle|IdleLow)-"), [Idle], "an idle machine"),
        // ---- weapon handling
        new("weapon.actions", R("^Weapon_Action-"), [WeaponHandling], "the weapon actions: silencer, reloads, stowing and drawing", Inherited: true),
        new("weapon.machine", R("^(Silencer|Reload_Normal|Reload_Vehicles|Stow|Get|ScopeMagnifierUpAndDown)-"), [WeaponHandling], "a weapon action"),
        new("weapon.npc", R("^ReferenceStateMachines-NPC_layer_(WeaponAction|get_stow_drop|grenade|aiming|reparenting)$"), [WeaponHandling],
            "an NPC weapon layer: weapon actions, drawing and stowing, grenades, aiming, the weapon between the hands", Inherited: true),
        new("weapon.layer", R("^Weapon-(Layer|StateMachine)$"), [WeaponHandling], "a weapon layer"),
        new("weapon.grip", Word("WPN_Foregrip_fingers"), [WeaponHandling], "the support hand on the weapon's foregrip"),
        new("weapon.aim", Word("AimPans"), [WeaponHandling], "aim pans: the poses that turn the aim"),
        new("weapon.shoot", R("^(Shoot|TO_Shoot|From_Shoot)-"), [WeaponHandling], "shooting from cover"),
        // ---- emotes, deaths, hits, injury
        new("emote", R("^Emotes-"), [Emote], "the emote layer"),
        new("death", R("^Death-"), [Death], "the death machine"),
        new("hit", R("^(Hit|Hits|Hit_regular(_\\w+)?)-|^ReferenceStateMachines-NPC_(layer|base)_hit_"), [HitReaction], "a hit reaction machine"),
        new("hit.flashbang", R("^Flashbang_reaction-"), [HitReaction], "the reactions to a flashbang"),
        new(InjuryRule, R("^(Down|Revive|Recover|InjuryEffects)-|^ReferenceStateMachines-(Down|Revive)$"),
            [InjuryAndDowned], "a downed character, or reviving one"),
        // the wounded and low-health bands: beside the wounded's own moves, coughs and stuns they name a few healthy clips (idles, a
        // fidget, suppressed holds), whose names say so; for those their Injury and downed doesn't stand (NameRules.Unwounded)
        new(WoundedBandRule, R("^VeryLowHealth-|^Locomotion-VeryLowHealtrh$|^ReferenceStateMachines-(LowHealth_\\w+|Wounded_\\w+)$"),
            [InjuryAndDowned], "a wounded or low-health character"),
        // ---- situations: everything under these layers plays in them
        new("cover", R("^(Cover|Cover_FreeMove|Cover_exit|To_cover)-"), [Cover], "a cover layer", Inherited: true),
        new("vehicle", Word("Vehicles?"), [Vehicle], "getting into, riding in and getting out of vehicles", Inherited: true),
        new("interaction", R("^(Interaction|GoToInteraction|PickUpObject|PickUp_Tidy)-|^ReferenceStateMachines-(PuppetInteraction|NPC_base_PickUp_tidy)$"),
            [Interaction], "interactions with objects in the world", Inherited: true),
        new("traversal", Word("Traversal|Basejump|BaseJump") , [Traversal], "climbing, vaulting, falling and base jumps", Inherited: true),
        new("traversal.slide", R("^(Slide|TO_Slide|Slide_and_Stumble)-"), [Traversal], "sliding and stumbling"),
        new("traversal.rail", R("^LocomotionRail-"), [Traversal], "ladders and vents"),
        new("swimming", Word("Swimming"), [Swimming], "swimming", Inherited: true),
        // only their own clips: the grab machines hold the carry machines (carrying the body after a grab), which are not grabs. The
        // shared grab machines also name carry clips themselves (taking a body out of a vehicle, aiming while carrying, the throw that
        // ends a carry): their names say carry, and NameRules.Carried adds Carry and drag beside this
        new(MeleeRule, Word("CQC|Grab"), [MeleeAndTakedown], "close-quarters combat: takedowns and grabs"),
        new("carry", R("^(Carry|To_Carry)-|^ReferenceStateMachines-(HideBody|Put_in_Vehicle)$"), [CarryAndDrag], "carrying and hiding bodies", Inherited: true),
        new("dialogue", R("^Dialogue-|^Phonemes-") , [FaceAndDialogue], "dialogue: lip sync and the face while talking"),
        new("dialogue.conversation", Word("Conversations?|LipSyncPhonemes|CustomFacial"), [FaceAndDialogue], "conversations, lip sync and faces"),
    ];

    /// <summary>The categories of every document of <paramref name="facts"/>, by the branches (who) and the document rules (what), each
    /// with its reason.</summary>
    public static DocumentPlacement[] Place(GraphFacts facts)
    {
        var docs = facts.Documents;
        var placed = new DocumentPlacement[docs.Count];
        for (int i = 0; i < placed.Length; i++) placed[i] = new DocumentPlacement();
        PlaceWho(facts, placed);
        PlaceWhat(facts, placed);
        MarkNpcSide(facts, placed);
        return placed;
    }

    /// <summary>Marks every document under the NPC machine by the tails. The tails make a tree, so none of them stands under the player's
    /// layers too; the shared machines stand under the reference machines' list, not here.</summary>
    static void MarkNpcSide(GraphFacts facts, DocumentPlacement[] placed)
    {
        int root = facts.IndexOf(NpcMachineId);
        if (root < 0 || !string.Equals(facts.Documents[root].Name, NpcMachineName, StringComparison.Ordinal)) return;
        var stack = new Stack<int>([root]);
        while (stack.Count > 0)
        {
            int d = stack.Pop();
            if (placed[d].UnderNpcMachine) continue;
            placed[d].UnderNpcMachine = true;
            foreach (int c in facts.Documents[d].Children) stack.Push(c);
        }
    }

    /// <summary>Who: from each branch's root, every document reached through children and references, each carrying the branch's
    /// characters or a refining machine's on the way. A document reached several ways takes all of them.</summary>
    static void PlaceWho(GraphFacts facts, DocumentPlacement[] placed)
    {
        var docs = facts.Documents;
        foreach (var b in Branches)
        {
            int root = facts.IndexOf(b.RootId);
            if (root < 0 || !string.Equals(docs[root].Name, b.RootName, StringComparison.Ordinal)) continue;
            var seen = new HashSet<(int, string, string?)>();
            var queue = new Queue<(int Doc, IReadOnlyList<string> Who, string Reason, string? Refined, string Path)>();
            queue.Enqueue((root, b.Who, $"{b.Reason} ({b.RootName})", null, $"{b.Label} ({b.RootName})"));
            while (queue.Count > 0)
            {
                var (d, who, reason, refined, path) = queue.Dequeue();
                if (Refinements.FirstOrDefault(r => r.Branch == b.Id && r.Pattern.IsMatch(docs[d].Name)) is { } refine && d != root)
                {
                    who = refine.Who;
                    reason = $"{b.Reason} ({b.RootName}), {refine.Reason} ({docs[d].Name})";
                    refined = refine.Id;
                    path = $"{b.Label} ({b.RootName}), through {docs[d].Name}";
                    placed[d].Rules.Add(refine.Id);
                }
                if (!seen.Add((d, string.Join(",", who), refined))) continue;
                placed[d].Rules.Add(b.Id);
                placed[d].Branches.Add(b.Id);
                if (refined is not null) placed[d].Refinements.Add(refined);
                foreach (var w in who)
                {
                    placed[d].Who.TryAdd(w, reason);
                    placed[d].WhoPath.TryAdd(w, path);
                }
                foreach (int c in docs[d].Children.Concat(docs[d].References)) queue.Enqueue((c, who, reason, refined, path));
            }
        }
    }

    /// <summary>What: each document's own rules, then the inherited ones down the tails from every document above it.</summary>
    static void PlaceWhat(GraphFacts facts, DocumentPlacement[] placed)
    {
        var docs = facts.Documents;
        var own = new List<DocumentRule>[docs.Count];
        for (int i = 0; i < docs.Count; i++)
        {
            own[i] = Documents.Where(r => r.Pattern.IsMatch(docs[i].Name)).ToList();
            foreach (var r in own[i])
            {
                placed[i].Rules.Add(r.Id);
                foreach (var w in r.What) (r.Weak ? placed[i].WeakWhat : placed[i].What).TryAdd(w, r.Weak ? Contained(r.Reason) : r.Reason);
            }
        }
        // down the tails from each document holding an inherited rule
        for (int i = 0; i < docs.Count; i++)
        {
            var inherited = own[i].Where(r => r.Inherited).ToList();
            if (inherited.Count == 0) continue;
            var seen = new HashSet<int> { i };
            var stack = new Stack<int>(docs[i].Children);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                if (!seen.Add(c)) continue;
                foreach (var r in inherited)
                    foreach (var w in r.What)
                    {
                        string reason = $"{r.Reason} ({docs[i].Name} holds it)";
                        (r.Weak ? placed[c].WeakWhat : placed[c].What).TryAdd(w, r.Weak ? Contained(reason) : reason);
                    }
                foreach (int g in docs[c].Children) stack.Push(g);
            }
        }
    }

    /// <summary>A weak rule's reason, saying why it counts only where nothing else places the clip.</summary>
    static string Contained(string reason) => $"{reason}; it holds clips of many kinds, so this stands only where nothing else places the clip";

    /// <summary>The weak (container) what-categories of one clip (<see cref="DocumentRule.Weak"/>): the union over the documents that name
    /// it, each reason naming the first document that gave it. The index uses them only for a clip whose what nothing else places.</summary>
    public static List<ClipCategory> WeakWhat(ClipGraphFacts clip, GraphFacts facts, DocumentPlacement[] placed) =>
        Sort(Collect(clip.Documents, d => placed[d].WeakWhat, facts), ClipCategories.What);

    /// <summary>The graph's categories for one clip by the branches and the strong rules: the union over the documents that name it, and
    /// locomotion for a clip a motion field names. Each category's reason names the first document that gave it, and how many more did;
    /// for who, the first document of one layer alone that gave it, before any machine several layers share (<see cref="DocumentPlacement.Shared"/>),
    /// as that document is what makes the category sure (<see cref="ClipIndex"/>'s rule for "probably").</summary>
    public static (List<ClipCategory> Who, List<ClipCategory> What) ForClip(ClipGraphFacts clip, GraphFacts facts, DocumentPlacement[] placed)
    {
        var who = Collect(clip.Documents, d => placed[d].Who, facts, d => placed[d].Shared, (d, w) => PartOf(d, w, facts, placed));
        var what = Collect(clip.Documents, d => placed[d].What, facts);
        if (clip.MotionFieldDocuments.Count > 0 && !what.Exists(c => c.Name == Locomotion))
            what.Add(new ClipCategory(Locomotion, CategorySource.Graph, $"{MotionFieldReason} ({facts.Documents[clip.MotionFieldDocuments[0]].Name})"));
        return (Sort(who, ClipCategories.Who), Sort(what, ClipCategories.What));
    }

    /// <summary>The menu rule's ID and reason: no layer played in the world names the clip, only the menus' character's layer
    /// (<c>branch.menu</c>) and what it reaches, so the menus play it, if the game plays it at all. It says no more than that: the layer
    /// names poses, idles, draws and stows, two emotes and whole launcher reloads alike, and the graph doesn't tell the lobby, the squad
    /// screen, the character screen or a photo pose apart (the layer names its clips without machines of their own for each screen).
    /// <see cref="ClipIndex"/> adds the category beside the what the best source gives, never in its place.</summary>
    public const string MenuRule = "menu", MenuBranch = "branch.menu";
    public const string MenuReason = "no layer played in the world names it, only the layer of the character the menus show: a clip of the "
        + "menus, the lobby or the squad screen, if the game plays it at all";

    /// <summary>Whether every document that names the clip is reached from the menus' character's layer alone (<see cref="MenuRule"/>).</summary>
    public static bool MenuOnly(ClipGraphFacts clip, IReadOnlyList<DocumentPlacement> placed) =>
        clip.Documents.Count > 0 && clip.Documents.All(d => placed[d].Branches.Count == 1 && placed[d].Branches.Contains(MenuBranch));

    /// <summary>The menu rule's category for a clip <see cref="MenuOnly"/> holds, its reason naming the documents.</summary>
    public static ClipCategory MenuCategory(ClipGraphFacts clip, GraphFacts facts) => new(MenuAndLobby, CategorySource.Graph,
        $"{MenuReason} ({string.Join(", ", clip.Documents.Select(d => facts.Documents[d].Name).Distinct(StringComparer.Ordinal))})");

    /// <summary>The part of the graph that gave document <paramref name="d"/> the who-category <paramref name="who"/>, for the clip info:
    /// the layer in plain words with its root's name, and the refining machine on the way, if any (<see cref="DocumentPlacement.WhoPath"/>);
    /// the document's own name where no branch gave it (never, for a who of the graph).</summary>
    public static string PartOf(int d, string who, GraphFacts facts, IReadOnlyList<DocumentPlacement> placed) =>
        placed[d].WhoPath.GetValueOrDefault(who) ?? facts.Documents[d].Name;

    /// <summary>Each category the documents give, with the reason of the document named for it and how many more gave it; with
    /// <paramref name="shared"/>, a document it says is shared is named only when no other document gives the category; with
    /// <paramref name="part"/>, each category's part of the graph, from the document named for it.</summary>
    static List<ClipCategory> Collect(IReadOnlyList<int> documents, Func<int, Dictionary<string, string>> of, GraphFacts facts, Func<int, bool>? shared = null,
        Func<int, string, string>? part = null)
    {
        var first = new Dictionary<string, (int Doc, string Reason, int More)>(StringComparer.Ordinal);
        var all = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (int d in documents)
            foreach (var (name, reason) in of(d))
            {
                first[name] = !first.TryGetValue(name, out var f) ? (d, reason, 0)
                    : shared is not null && shared(f.Doc) && !shared(d) ? (d, reason, f.More + 1)
                    : f with { More = f.More + 1 };
                (all.TryGetValue(name, out var l) ? l : all[name] = []).Add(d);
            }
        return first.Select(k => new ClipCategory(k.Key, CategorySource.Graph,
            $"{facts.Documents[k.Value.Doc].Name} names it: {k.Value.Reason}" + (k.Value.More > 0 ? $"; {k.Value.More} more document{(k.Value.More == 1 ? "" : "s")} too" : ""))
        { Part = part is null ? null : Parts(all[k.Key].Select(d => (part(d, k.Key), shared?.Invoke(d) ?? false))) }).ToList();
    }

    /// <summary>The most layers a who-category's line names; the rest are counted.</summary>
    public const int PartsShown = 3;

    /// <summary>What a part of the graph says when only machines several layers share reach the clip from that layer.</summary>
    public const string BySharedMachine = "by a shared machine";

    /// <summary>The parts of the graph, one per layer: the layer in plain words with its root's name, then the narrowing machines the way
    /// down passed ("through Military-StateMachine"; "partly through …" where some of the layer's documents that name the clip stand
    /// outside every narrowing machine), then
    /// <see cref="BySharedMachine"/> where only machines several layers share name the clip from it (<see cref="DocumentPlacement.Shared"/>:
    /// which of their states that layer plays isn't read). The layers played in the world come before the menus' character's layer, so a
    /// clip the wheel and the menus both play doesn't read as a menu pose; at most <see cref="PartsShown"/>, then how many more. The
    /// documents themselves are the clip info's own list, so they aren't repeated.</summary>
    internal static string Parts(IEnumerable<(string Path, bool Shared)> parts)
    {
        const string Through = ", through ";
        string menu = Branches.First(b => b.Id == MenuBranch).Label;
        var layers = parts.GroupBy(p => p.Path.Split(Through)[0], StringComparer.Ordinal)
            .OrderBy(g => g.Key.StartsWith(menu, StringComparison.Ordinal) ? 1 : 0)
            .Select(g =>
            {
                var through = g.Select(p => p.Path.Split(Through) is { Length: > 1 } s ? s[1] : null).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                bool direct = g.Any(p => !p.Path.Contains(Through, StringComparison.Ordinal));
                string text = g.Key + (through.Count == 0 ? "" : (direct ? ", partly through " : Through) + string.Join(", ", through));
                return g.All(p => p.Shared) ? $"{text}, {BySharedMachine}" : text;
            }).ToList();
        return string.Join("; ", layers.Take(PartsShown)) + (layers.Count > PartsShown ? $"; and {layers.Count - PartsShown} more" : "");
    }

    internal static List<ClipCategory> Sort(List<ClipCategory> l, IReadOnlyList<string> order)
    {
        l.Sort((a, b) => IndexIn(order, a.Name).CompareTo(IndexIn(order, b.Name)));
        return l;
    }

    static int IndexIn(IReadOnlyList<string> order, string name)
    {
        for (int i = 0; i < order.Count; i++) if (order[i] == name) return i;
        return order.Count;
    }
}
