using System.Text.RegularExpressions;
using static GrbAnimTool.Core.Index.ClipCategories;

namespace GrbAnimTool.Core.Index;

/// <summary>A word or words a clip's name may hold, the category they place it in, and why. <see cref="Words"/> must stand together, in
/// order, as words of the name (or joined, as one word of it); <see cref="NotBefore"/> lists words after which the match doesn't count
/// ("face" before "up" is a body lying face up, not a face).</summary>
public sealed record NameKeyword(IReadOnlyList<string> Words, CategoryKind Kind, string Category, string Reason, IReadOnlyList<string>? NotBefore = null);

/// <summary>A family of clip names (the name's first part, or its first parts) and who plays it, with the evidence. With
/// <see cref="KeepsTeammates"/>, the family is the non-player characters' at large, the AI teammates not set apart: where a machine
/// several layers play names one of its clips, the family keeps the teammates' share of that machine beside its own characters. With
/// <see cref="NoTeammates"/>, the family's characters are never AI teammates (the reason says why): a document of the teammates' own that
/// names one of its clips gives the teammates only probably (a copied machine's branch the teammates may never reach).</summary>
public sealed record FamilyRule(IReadOnlyList<string> Parts, IReadOnlyList<string> Who, string Reason, bool KeepsTeammates = false, string? NoTeammates = null);

/// <summary>A machine several layers play by reference whose split by side is read: in it, a clip of the main character's families is
/// the player's alone.</summary>
public sealed record SideMachine(Regex Pattern, string Reason);

/// <summary>The last source, for a kind neither the graph nor the tool's list placed: the clip's name. Its family (the first part or
/// parts, <see cref="FamilyRules"/>) for who, and only that; its words for what, and for a word the tool's glossary explains, the words of
/// its meaning (for what alone: a meaning's words never place who). A rule fires only on a word the name holds; the reason quotes it.
/// The family also narrows who where the graph gives a machine several layers play (<see cref="Narrow"/>).
/// <para>Words: the name split at '_', '-', spaces and dots, then each part at its case changes ("ClimbUp" gives "climb" and "up";
/// "MCH", "tm8" and "V0" stay whole), lower case; a word's trailing digits are also tried without them ("Turr01" is "turr").</para></summary>
public static class NameRules
{
    static FamilyRule F(string parts, string[] who, string reason, bool keepsTeammates = false, string? noTeammates = null) =>
        new(parts.Split(' '), who, reason, keepsTeammates, noTeammates);

    /// <summary>The families with evidence of whose they are, the longest first where one starts another. The player's own layers (MCH,
    /// the menus' character) play the main character's families. The civilians' (civ: the generic layer's Civilian machines play it, and
    /// no civilian's record is hostile) are NPC; the teammates' (tm8: the teammates' layer plays it) are Teammates. npc_ is the clips of
    /// the other side of the player's takedowns, grabs, carries and hijacks and of conversations, enemies and other NPCs alike. The
    /// soldiers' mil_ and the archetypes' pmc_ are the enemies' and their allies': the allied riflemen's records have the hostile
    /// riflemen's archetype, and the machine of an ally (Sam Fisher) plays pmc_ clips. Only the heavy archetype's (pmc_hvy) is the
    /// enemies' alone: every heavy NPC's record is hostile, and no ally's machine names one. By the name alone npc_, mil_ and pmc_ say
    /// Enemy and NPC, never Teammates (the teammates' own copies of the machines, which name many of them, say that); but they are the
    /// non-player characters' at large, so where a machine the teammates' layer shares with other layers names one, they keep its
    /// Teammates (<see cref="FamilyRule.KeepsTeammates"/>). The heavies' and the civilians' families rule the teammates out
    /// (<see cref="FamilyRule.NoTeammates"/>): a teammate is neither a hostile heavy nor a civilian, so where the teammates' own copy of a
    /// machine names such a clip (19 heavy deaths in their death machine, 16 civilian dodges in their dodge layer), that is most likely a
    /// branch of the copy they never reach, and the teammates play the clip only probably. Families the records don't settle (nme, vip,
    /// acd, rbl, term and the like) give no who.</summary>
    public static readonly IReadOnlyList<FamilyRule> FamilyRules =
    [
        F("pmc hvy", [Enemy], "the heavy archetype's family (pmc_hvy): every heavy NPC's record is hostile",
            noTeammates: "a teammate is never a heavy: every heavy NPC's record is hostile"),
        F("mch", [Player], "the main character's family (mch)"),
        F("mca", [Player], "the main character's family (mca)"),
        F("mcm", [Player], "the main character's family (mcm)"),
        F("civ", [Npc], "the civilians' family (civ): no civilian's record is hostile", noTeammates: "a teammate is never a civilian"),
        F("tm8", [Teammates], "the AI teammates' family (tm8)"),
        F("npc", [Enemy, Npc], "the non-player characters' family (npc): the other side of a takedown, grab, carry or hijack, and of a conversation", keepsTeammates: true),
        F("mil", [Enemy, Npc], "the soldiers' family (mil): hostile and allied soldiers alike", keepsTeammates: true),
        F("pmc", [Enemy, Npc], "the soldier archetypes' family (pmc): the enemies', and the allies' who share the rifleman archetype", keepsTeammates: true),
    ];

    /// <summary>The families of one part that place one who (the player's and the civilians' and teammates'), by that part: a view of
    /// <see cref="FamilyRules"/>.</summary>
    public static readonly IReadOnlyDictionary<string, (string Who, string Reason)> Families = FamilyRules
        .Where(f => f.Parts.Count == 1 && f.Who.Count == 1)
        .ToDictionary(f => f.Parts[0], f => (f.Who[0], f.Reason), StringComparer.OrdinalIgnoreCase);

    /// <summary>The family of a name: the first rule of <see cref="FamilyRules"/> whose parts begin the name's, case ignored; or null.</summary>
    public static FamilyRule? FamilyOf(string name)
    {
        var parts = Parts(name);
        foreach (var f in FamilyRules)
        {
            if (f.Parts.Count > parts.Count) continue;
            bool all = true;
            for (int i = 0; i < f.Parts.Count && all; i++) all = string.Equals(parts[i], f.Parts[i], StringComparison.OrdinalIgnoreCase);
            if (all) return f;
        }
        return null;
    }

    /// <summary>The shared machines where a main-character clip is the player's alone. Elsewhere the NPCs' layers play the main
    /// character's clips too: the shared swimming and revive machines hold almost only mca_ clips, and the NPCs' layers play them.</summary>
    public static readonly IReadOnlyList<SideMachine> PlayerSideMachines =
    [
        new(new Regex("^ReferenceStateMachines-Conversations$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "the conversation machine plays the main character's clips on the player's entry, and its NPC split for every other character"),
    ];

    /// <summary>The most words a glossary meaning's head may have for its words to be read as the name's: an expansion ("swimming",
    /// "low health", "AI teammates"), not a sentence about where the part is used.</summary>
    public const int GlossaryHeadWords = 3;

    static NameKeyword K(string words, CategoryKind kind, string category, string reason, params string[] notBefore) =>
        new(words.Split(' '), kind, category, reason, notBefore.Length == 0 ? null : notBefore);

    const CategoryKind What = CategoryKind.What, Who = CategoryKind.Who;

    /// <summary>The words, each with its category. The abbreviations are the game's own: cvr cover, swm swim, trav traversal, cqc
    /// close-quarters combat, inj injured, phon phoneme, driv driving, turr turret, vhc vehicle, gre grenade. The shipped glossary marks
    /// cvr, phon, driv, vhc and gre sure with the graph's evidence (the cover, lip-sync and vehicle machines and the NPCs' grenade layer
    /// play them).</summary>
    public static readonly IReadOnlyList<NameKeyword> Keywords =
    [
        K("death", What, Death, "a death"), K("dead", What, Death, "dead"), K("dying", What, Death, "dying"),
        K("hit", What, HitReaction, "a hit"), K("hits", What, HitReaction, "hits"), K("flinch", What, HitReaction, "a flinch"),
        K("stagger", What, HitReaction, "a stagger"),
        K("downstate", What, InjuryAndDowned, "the down state"), K("revive", What, InjuryAndDowned, "a revive"),
        K("revived", What, InjuryAndDowned, "revived"), K("reviver", What, InjuryAndDowned, "the reviver"),
        K("wounded", What, InjuryAndDowned, "wounded"), K("injured", What, InjuryAndDowned, "injured"), K("injury", What, InjuryAndDowned, "an injury"),
        K("inj", What, InjuryAndDowned, "injured (inj)"), K("lowhealth", What, InjuryAndDowned, "low health"), K("low health", What, InjuryAndDowned, "low health"),
        K("verylowhealth", What, InjuryAndDowned, "very low health"), K("bandage", What, InjuryAndDowned, "a bandage"),
        K("downed", What, InjuryAndDowned, "downed"),
        K("cover", What, Cover, "cover"), K("cvr", What, Cover, "cover (cvr)"), K("peek", What, Cover, "peeking out of cover"),
        K("emote", What, Emote, "an emote"),
        K("idle", What, Idle, "an idle"), K("idles", What, Idle, "idles"), K("pidle", What, Idle, "a passive idle"),
        K("fidget", What, Idle, "a fidget"), K("fidgets", What, Idle, "fidgets"),
        K("cycle", What, Locomotion, "a cycle (a looping step)"), K("walk", What, Locomotion, "a walk"), K("run", What, Locomotion, "a run"),
        K("sprint", What, Locomotion, "a sprint"), K("jog", What, Locomotion, "a jog"), K("strafe", What, Locomotion, "a strafe"),
        K("turn", What, Locomotion, "a turn"), K("start", What, Locomotion, "a start (setting off)"), K("stop", What, Locomotion, "a stop"),
        K("climb", What, Traversal, "a climb"), K("vault", What, Traversal, "a vault"), K("jump", What, Traversal, "a jump"),
        K("basejump", What, Traversal, "a base jump"), K("fall", What, Traversal, "a fall"), K("slide", What, Traversal, "a slide"),
        K("ladder", What, Traversal, "a ladder"), K("trav", What, Traversal, "traversal (trav)"), K("traversal", What, Traversal, "traversal"),
        K("mantle", What, Traversal, "a mantle"), K("skydive", What, Traversal, "a skydive"), K("skydiving", What, Traversal, "skydiving"),
        K("parachute", What, Traversal, "a parachute"),
        K("vent", What, Traversal, "a vent crawl"), K("crossover", What, Traversal, "crossing over an obstacle"),
        K("cross over", What, Traversal, "crossing over an obstacle"),
        K("swim", What, Swimming, "swimming"), K("swm", What, Swimming, "swimming (swm)"), K("swimming", What, Swimming, "swimming"),
        K("underwater", What, Swimming, "under water"),
        K("takedown", What, MeleeAndTakedown, "a takedown"), K("cqc", What, MeleeAndTakedown, "close-quarters combat (cqc)"),
        K("melee", What, MeleeAndTakedown, "melee"), K("knife", What, MeleeAndTakedown, "a knife"), K("karambit", What, MeleeAndTakedown, "a karambit"),
        K("punch", What, MeleeAndTakedown, "a punch"), K("grab", What, MeleeAndTakedown, "a grab"), K("grabbed", What, MeleeAndTakedown, "grabbed"),
        K("carry", What, CarryAndDrag, "carrying"), K("carrying", What, CarryAndDrag, "carrying"), K("carried", What, CarryAndDrag, "carried"),
        K("dragged", What, CarryAndDrag, "dragged"),
        K("dragging", What, CarryAndDrag, "dragging"),
        K("face", What, FaceAndDialogue, "the face", "up", "down"), K("facial", What, FaceAndDialogue, "facial"),
        K("phon", What, FaceAndDialogue, "a phoneme (phon)"), K("phoneme", What, FaceAndDialogue, "a phoneme"), K("lipsync", What, FaceAndDialogue, "lip sync"),
        K("conversation", What, FaceAndDialogue, "a conversation"), K("speak", What, FaceAndDialogue, "speaking"), K("speack", What, FaceAndDialogue, "speaking"),
        K("talk", What, FaceAndDialogue, "talking"), K("talker", What, FaceAndDialogue, "the talker"), K("dialogue", What, FaceAndDialogue, "dialogue"),
        K("mood", What, FaceAndDialogue, "a mood of the face"),
        K("reload", What, WeaponHandling, "a reload"), K("stow", What, WeaponHandling, "stowing"), K("silencer", What, WeaponHandling, "a silencer"),
        K("chamber", What, WeaponHandling, "chambering"), K("recoil", What, WeaponHandling, "recoil"), K("grenade", What, WeaponHandling, "a grenade"),
        K("unpin", What, WeaponHandling, "unpinning"), K("overheat", What, WeaponHandling, "overheating"), K("foregrip", What, WeaponHandling, "the foregrip"),
        K("shoot", What, WeaponHandling, "shooting"), K("aimpan", What, WeaponHandling, "an aim pan"), K("aim pan", What, WeaponHandling, "an aim pan"),
        K("aim pans", What, WeaponHandling, "aim pans"),
        // gre: the grenade clips' word (the NPCs' grenade layer plays mca_ar_gre_crh_throw and its kin); c4 and mines are explosives the
        // character throws or plants; m72 and sgl the rocket and grenade launchers
        K("gre", What, WeaponHandling, "a grenade (gre)"), K("c4", What, WeaponHandling, "C4"), K("mine", What, WeaponHandling, "a mine"),
        K("m72", What, WeaponHandling, "the M72 launcher"), K("sgl", What, WeaponHandling, "the grenade launcher (sgl)"),
        K("vehicle", What, Vehicle, "a vehicle"), K("vhc", What, Vehicle, "a vehicle (vhc)"), K("driv", What, Vehicle, "driving (driv)"),
        K("drive", What, Vehicle, "driving"), K("driver", What, Vehicle, "the driver"), K("turr", What, Vehicle, "a turret (turr)"),
        K("turret", What, Vehicle, "a turret"), K("bike", What, Vehicle, "a bike"), K("motorbike", What, Vehicle, "a motorbike"),
        K("boat", What, Vehicle, "a boat"), K("plane", What, Vehicle, "a plane"),
        K("heli", What, Vehicle, "a helicopter"), K("helicopter", What, Vehicle, "a helicopter"), K("apc", What, Vehicle, "an armoured carrier (apc)"),
        K("truck", What, Vehicle, "a truck"), K("car", What, Vehicle, "a car"), K("suv", What, Vehicle, "an suv"), K("blackhawk", What, Vehicle, "a helicopter"),
        K("interaction", What, Interaction, "an interaction"), K("interact", What, Interaction, "an interaction"), K("hack", What, Interaction, "hacking"),
        K("loot", What, Interaction, "looting"),
        K("pose", What, Pose, "a pose"), K("poseref", What, Pose, "a reference pose"),
        // the look-at sweeps: the poses a look-at blend samples by angle to turn the upper body and the head
        K("look at", What, Pose, "a look-at sweep (a pose the look-at blends by angle)"),
        // no who: a name's words and a glossary meaning's ("non-player characters", "player versus player") don't say who plays a clip;
        // only its family does (FamilyRules)
    ];

    static readonly Regex CaseChange = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The name's parts at '_', '-', spaces and dots, as spelt.</summary>
    public static IReadOnlyList<string> Parts(string name) => name.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The name's words: each part split at its case changes, lower case.</summary>
    public static IReadOnlyList<string> Words(string name) =>
        Parts(name).SelectMany(p => CaseChange.Split(p)).Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();

    /// <summary>The categories the name gives, and the glossary entries that explain its words. <paramref name="glossary"/> is the tool's
    /// sure glossary (or none).</summary>
    public static (List<ClipCategory> Who, List<ClipCategory> What, List<GlossaryEntry> Explained) Place(string name, IReadOnlyDictionary<string, GlossaryEntry>? glossary)
    {
        var who = new Dictionary<string, string>(StringComparer.Ordinal);
        var what = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = Parts(name);
        if (FamilyOf(name) is { } fam) foreach (var w in fam.Who) who.TryAdd(w, $"the name's family: {fam.Reason}");
        var words = Words(name);
        var joined = parts.Select(p => p.ToLowerInvariant()).ToList();
        foreach (var (k, w) in Matches(words, joined))
            if (k.Kind == CategoryKind.What) what.TryAdd(k.Category, $"the name holds \"{w}\": {k.Reason}");
        var explained = new List<GlossaryEntry>();
        if (glossary is not null)
        {
            // each part, and each word, the glossary explains. Its meaning's short head (ShortHead: the expansion, before the explanation
            // of where the part is used) is read as the name's own words for what; never for who. A speed's walk, run or sprint says
            // nothing of a clip whose name says it stands still (a pose, a suppressed hold, an offset: StillWords)
            bool still = words.Any(w => StillWords.Contains(TrimDigits(w)));
            foreach (var p in joined.Concat(words).Distinct(StringComparer.Ordinal))
            {
                if (!glossary.TryGetValue(p, out var g) && !glossary.TryGetValue(TrimDigits(p), out g)) continue;
                if (explained.Contains(g)) continue;
                explained.Add(g);
                if (ShortHead(g.Meaning) is not { } headText) continue;
                var head = Regex.Replace(headText, "[^A-Za-z0-9]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
                foreach (var (k, _) in Matches(Words(head), Parts(head).Select(x => x.ToLowerInvariant()).ToList()))
                    if (k.Kind == CategoryKind.What && !(still && k.Category == Locomotion))
                        what.TryAdd(k.Category, $"the name's \"{g.Abbreviation}\" means \"{g.Meaning}\" (the tool's glossary): {k.Reason}");
            }
        }
        return (GraphRules.Sort(who.Select(x => new ClipCategory(x.Key, CategorySource.Name, x.Value)).ToList(), ClipCategories.Who),
            GraphRules.Sort(what.Select(x => new ClipCategory(x.Key, CategorySource.Name, x.Value)).ToList(), ClipCategories.What), explained);
    }

    /// <summary>The refinement of the generic layer's soldiers' machine (Military), whose who is both sides.</summary>
    public const string SoldiersRefinement = "refine.military";

    /// <summary>The generic NPC layer's branch: what it reaches outside its Civilian machines (and its ally's) plays for soldiers, and
    /// outside its Military machine for civilians too, so its who there is both sides.</summary>
    public const string GenericNpcBranch = "branch.npc";

    /// <summary>Who for a clip that a machine several layers play by reference names: the graph gives it the union of those layers'
    /// characters, as which side plays which of the machine's states isn't read; the same for a clip under the soldiers' machine
    /// (<see cref="SoldiersRefinement"/>), which plays for hostile and allied soldiers alike, and for a clip a document of the generic NPC
    /// layer's own names outside its split by kind (<see cref="GenericNpcBranch"/>: its dodges, reactions, look-ats and the like play
    /// for soldiers and civilians alike). The name's family narrows that union to the family's
    /// characters among it where the family has evidence there (with the teammates for a family of the non-player characters at large,
    /// <see cref="FamilyRule.KeepsTeammates"/>): a family of the other characters' (civ, tm8, npc, mil, pmc) is never the
    /// player's own, in any shared machine; a main-character family is the player's alone only in a machine of
    /// <see cref="PlayerSideMachines"/>. The documents only one layer reaches give their characters as they are. Null when the family
    /// narrows nothing (no family, no shared machine, or a family that would leave none of a machine's characters); otherwise the
    /// categories, each with the document and the family's reason. A category a document of one layer alone keeps (not
    /// <see cref="DocumentPlacement.Shared"/>: the generic layer's or the soldiers' machine, narrowed or not) is labelled as the graph's,
    /// as that layer plays the clip whatever its name; one only a shared machine's union narrowed by the name keeps is labelled as the
    /// name's (the name decided among the graph's options), and only probably plays it (<see cref="ClipIndexEntry.WhoProbably"/>).</summary>
    public static List<ClipCategory>? Narrow(string name, ClipGraphFacts clip, GraphFacts facts, IReadOnlyList<DocumentPlacement> placed)
    {
        if (FamilyOf(name) is not { } fam) return null;
        bool playerFamily = fam.Who.Contains(Player);
        var who = new Dictionary<string, string>(StringComparer.Ordinal);
        // the categories a document of one layer alone keeps, with that document's reason: the graph's, sure
        var single = new Dictionary<string, string>(StringComparer.Ordinal);
        // the parts of the graph of every document that keeps each category, shared or not, as the graph's own lines collect them
        var parts = new Dictionary<string, List<(string, bool)>>(StringComparer.Ordinal);
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (int d in clip.Documents)
        {
            var p = placed[d];
            if (p.Who.Count == 0) continue;
            string doc = facts.Documents[d].Name;
            var all = ClipCategories.Who.Where(p.Who.ContainsKey).ToList();
            union.UnionWith(all);
            var keep = all;
            string? why = null;
            var side = playerFamily ? PlayerSideMachines.FirstOrDefault(m => m.Pattern.IsMatch(doc)) : null;
            // a union the graph can't split: several layers' (which side plays which state isn't read), the soldiers' machine's, which
            // plays for hostile and allied soldiers alike, or the generic NPC layer's own, which plays for soldiers and civilians alike
            // outside its Military and Civilian machines
            bool shared = p.Branches.Count > 1, soldiers = p.Refinements.Contains(SoldiersRefinement),
                generic = p.Branches.Contains(GenericNpcBranch);
            if ((shared || soldiers || generic) && (!playerFamily || side is not null)
                && all.Where(w => fam.Who.Contains(w) || fam.KeepsTeammates && w == Teammates).ToList() is { Count: > 0 } picked && picked.Count < all.Count)
            {
                keep = picked;
                why = (shared ? $"{doc} names it, a machine several layers play ({string.Join(", ", all)})"
                        : soldiers ? $"{doc} names it, under the soldiers' machine, which plays for hostile and allied soldiers alike ({string.Join(", ", all)})"
                        : $"{doc} names it, played by the generic NPC layer, which serves soldiers and civilians alike ({string.Join(", ", all)})")
                    + $"; the name's family decides among them: {fam.Reason}" + (side is null ? "" : $"; {side.Reason}");
            }
            foreach (var w in keep)
            {
                who.TryAdd(w, why ?? $"{doc} names it: {p.Who[w]}");
                if (!shared) single.TryAdd(w, why ?? $"{doc} names it: {p.Who[w]}");
                (parts.TryGetValue(w, out var l) ? l : parts[w] = []).Add((GraphRules.PartOf(d, w, facts, placed), shared));
            }
        }
        if (who.Count == union.Count) return null;
        return GraphRules.Sort(who.Select(x => single.TryGetValue(x.Key, out var sure)
            ? new ClipCategory(x.Key, CategorySource.Graph, sure) { Part = GraphRules.Parts(parts[x.Key]) }
            : new ClipCategory(x.Key, CategorySource.Name, x.Value)).ToList(), ClipCategories.Who);
    }

    /// <summary>The name's words that say a clip moves: a speed (V1 walk, V1slow, V2 run, V3 sprint: the glossary's), or a word that
    /// joins two of the movement's states in the name (TO, TOS, TRC: "fm_std_V0_N_TO_fm_std_V0_SE" goes from one into the other).</summary>
    public static readonly IReadOnlyList<string> MoveWords = ["v1", "v1slow", "v2", "v3", "to", "tos", "trc"];

    /// <summary>The first part of the name, as spelt, that is one of <see cref="MoveWords"/>; or null when it holds none (a clip standing
    /// still, V0, that goes nowhere: a pose, a loop in place, a gesture), or when a word of it says it stands still (<see cref="StillWords"/>).</summary>
    public static string? MoveWord(string name) =>
        Words(name).Any(w => StillWords.Contains(TrimDigits(w))) ? null :Parts(name).FirstOrDefault(p => MoveWords.Contains(p.ToLowerInvariant()));

    /// <summary>A container's weak categories for a clip (<see cref="GraphRules.WeakWhat"/>), kept only where the name agrees: its
    /// Locomotion only for a name that says the clip moves (<see cref="MoveWord"/>), the word added to the reason. A container's layer
    /// holds clips of many kinds, so without that its Locomotion would be a guess; the clip stays uncategorised instead.</summary>
    public static List<ClipCategory> Corroborate(string name, IReadOnlyList<ClipCategory> weak)
    {
        string? move = null;
        bool looked = false;
        var kept = new List<ClipCategory>();
        foreach (var c in weak)
        {
            if (c.Name != Locomotion) { kept.Add(c); continue; }
            if (!looked) (move, looked) = (MoveWord(name), true);
            if (move is not null) kept.Add(c with { Reason = $"{c.Reason}; the name's \"{move}\" says it moves" });
        }
        return kept;
    }

    /// <summary>A glossary meaning's head: its text up to the first ':', ';' or '(' (all of it when it holds none).</summary>
    public static string MeaningHead(string meaning)
    {
        int cut = meaning.IndexOfAny([':', ';', '(']);
        return (cut < 0 ? meaning : meaning[..cut]).Trim();
    }

    /// <summary>A glossary meaning's head (<see cref="MeaningHead"/>) when it is short, <see cref="GlossaryHeadWords"/> words or fewer:
    /// what the abbreviation stands for ("soldiers", "low health", "AI teammates"), not a sentence about where it is used; else null. The
    /// name rules read it as the name's own words, and the search looks in it (never in the explanation after it, which speaks of many
    /// clips: "their shared deaths, hits and cover moves" is no clip's own).</summary>
    public static string? ShortHead(string meaning)
    {
        string head = MeaningHead(meaning);
        int words = Words(Regex.Replace(head, "[^A-Za-z0-9]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1))).Count;
        return words is > 0 and <= GlossaryHeadWords ? head : null;
    }

    /// <summary>The name's words that say the clip stands still whatever its speed part says: a pose or reference pose, a suppressed
    /// hold, a weapon's offset. A speed (V1 walk, V2 run, V3 sprint) gives no Locomotion with one of them.</summary>
    public static readonly IReadOnlyList<string> StillWords = ["pose", "poseref", "suppressed", "offset"];

    /// <summary>The name's words that say the clip is a healthy character's own: an idle, a fidget, a suppressed hold. The wounded and
    /// low-health machines (<see cref="GraphRules.WoundedBandRule"/>) also name such clips, beside the wounded's own; their Injury and
    /// downed doesn't stand for a clip whose name holds one of these and no word of injury (<see cref="Unwounded"/>).</summary>
    public static readonly IReadOnlyList<string> HealthyWords = ["idle", "idles", "pidle", "fidget", "fidgets", "suppressed"];

    /// <summary>The graph's strong what for a clip without the Injury and downed that only the wounded and low-health machines give
    /// (<see cref="GraphRules.WoundedBandRule"/>), where the name says the clip is a healthy character's (<see cref="HealthyWords"/>) and
    /// holds no word of injury; otherwise <paramref name="what"/> as it is.</summary>
    public static List<ClipCategory> Unwounded(string name, List<ClipCategory> what, ClipGraphFacts clip, IReadOnlyList<DocumentPlacement> placed)
    {
        if (!what.Exists(c => c.Name == InjuryAndDowned)) return what;
        var words = Words(name);
        if (!words.Any(w => HealthyWords.Contains(TrimDigits(w)))) return what;
        var parts = Parts(name).Select(p => p.ToLowerInvariant()).ToList();
        if (Matches(words, parts).Any(m => m.Keyword.Category == InjuryAndDowned)) return what;
        // only when every document giving it gives it by the wounded band's rule alone
        foreach (int d in clip.Documents)
            if (placed[d].What.ContainsKey(InjuryAndDowned) && !(placed[d].Rules.Contains(GraphRules.WoundedBandRule) && !placed[d].Rules.Contains(GraphRules.InjuryRule)))
                return what;
        return what.Where(c => c.Name != InjuryAndDowned).ToList();
    }

    /// <summary>The graph's strong what for a clip, with Carry and drag added where the name says carry (the word "carry" or "carried") and a grab
    /// machine (<see cref="GraphRules.MeleeRule"/>) gave it Melee and takedown without Carry and drag: the shared grab machines name the
    /// carries that follow a grab (taking the body out of a vehicle, aiming the handgun while carrying, the throw that ends a carry) as
    /// well as the grabs. The Melee and takedown stays (the grab machine's); the Carry and drag is labelled as the name's. Otherwise
    /// <paramref name="what"/> as it is.</summary>
    public static List<ClipCategory> Carried(string name, List<ClipCategory> what, ClipGraphFacts clip, GraphFacts facts, IReadOnlyList<DocumentPlacement> placed)
    {
        var words = Words(name);
        if (what.Exists(c => c.Name == CarryAndDrag) || !what.Exists(c => c.Name == MeleeAndTakedown)
            || !(words.Contains("carry") || words.Contains("carried"))) return what;
        int grab = clip.Documents.FirstOrDefault(d => placed[d].What.ContainsKey(MeleeAndTakedown) && placed[d].Rules.Contains(GraphRules.MeleeRule), -1);
        if (grab < 0) return what;
        string said = words.Contains("carry") ? "carry" : "carried";
        return GraphRules.Sort([.. what, new ClipCategory(CarryAndDrag, CategorySource.Name,
            $"the name holds \"{said}\": carrying; {facts.Documents[grab].Name}, a grab machine, names it, as the shared grab machines name the carries that follow a grab")], ClipCategories.What);
    }

    /// <summary>The word after "carry" and <paramref name="way"/> ("entry" or "exit") in the name: the vehicle of a carry into or out of
    /// one (mca_carry_exit_heli_left: heli; npc_carry_entry_medium_swing_door_left: medium), or null.</summary>
    public static string? CarryVehicleWord(string name, string way)
    {
        var w = Words(name);
        for (int i = 0; i + 2 < w.Count; i++) if (w[i] == "carry" && w[i + 1] == way) return w[i + 2];
        return null;
    }

    /// <summary>Vehicle for a carry out of a vehicle (the name's "carry exit" and the vehicle's word), where the graph places the clip
    /// but not as Vehicle: the carry exits stand in the shared grab machines, while the carries into the same vehicles stand in the
    /// machine for putting a body into a vehicle, which the graph places as Vehicle. The evidence, labelled as the name's: a carry entry
    /// with the same vehicle word whose graph's what is Vehicle (<paramref name="entries"/>: the vehicle word and that clip's reason), or
    /// else the vehicle word itself as a vehicle keyword of the names (<see cref="Keywords"/>: heli, truck). Otherwise
    /// <paramref name="what"/> as it is.</summary>
    public static List<ClipCategory> CarryExitVehicle(string name, List<ClipCategory> what, IReadOnlyDictionary<string, string> entries)
    {
        if (what.Count == 0 || what.Exists(c => c.Name == Vehicle) || CarryVehicleWord(name, "exit") is not { } v) return what;
        string? why = entries.TryGetValue(v, out var entry) ? entry
            : ByFirstWord.GetValueOrDefault(v)?.FirstOrDefault(k => k.Words.Count == 1 && k.Category == Vehicle) is { } k ? $"\"{v}\" is a vehicle word of the names ({k.Reason})" : null;
        if (why is null) return what;
        return GraphRules.Sort([.. what, new ClipCategory(Vehicle, CategorySource.Name,
            $"the name's \"carry exit {v}\" says it carries a body out of a vehicle: {why}")], ClipCategories.What);
    }

    /// <summary>The vehicles of the carries into one, by the graph: for each clip whose name says "carry entry" and a vehicle's word
    /// (<see cref="CarryVehicleWord"/>) and whose graph's what is Vehicle, that word and the evidence (the first such clip and its
    /// reason), for <see cref="CarryExitVehicle"/>.</summary>
    public static Dictionary<string, string> CarryEntryVehicles(IEnumerable<(string Name, IReadOnlyList<ClipCategory> GraphWhat)> clips)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, what) in clips)
            if (CarryVehicleWord(name, "entry") is { } v && !d.ContainsKey(v) && what.FirstOrDefault(c => c.Name == Vehicle) is { } veh)
                d[v] = $"{name}, the carry into the same vehicle, is Vehicle by the graph ({veh.Reason})";
        return d;
    }

    /// <summary>The keywords by their first word.</summary>
    static readonly Dictionary<string, List<NameKeyword>> ByFirstWord =
        Keywords.GroupBy(k => k.Words[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>The keywords of several words, by their words joined: a name may spell them as one part ("lowhealth", "aimpan").</summary>
    static readonly Dictionary<string, List<NameKeyword>> ByJoinedWords = Keywords.Where(k => k.Words.Count > 1)
        .GroupBy(k => string.Concat(k.Words), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>Every keyword the words hold, in the order the name holds them, each with the words as they stand.</summary>
    static IEnumerable<(NameKeyword Keyword, string Words)> Matches(IReadOnlyList<string> words, IReadOnlyList<string> parts)
    {
        var trimmed = words.Select(TrimDigits).ToArray();
        for (int i = 0; i < words.Count; i++)
        {
            var at = ByFirstWord.GetValueOrDefault(words[i]) ?? (trimmed[i] != words[i] ? ByFirstWord.GetValueOrDefault(trimmed[i]) : null);
            if (at is null) continue;
            foreach (var k in at)
            {
                var kw = k.Words;
                if (i + kw.Count > words.Count) continue;
                bool all = true;
                for (int j = 1; j < kw.Count && all; j++) all = words[i + j] == kw[j] || trimmed[i + j] == kw[j];
                if (!all) continue;
                if (k.NotBefore is { } nb && i + kw.Count < words.Count && nb.Contains(trimmed[i + kw.Count])) continue;
                yield return (k, string.Join(" ", words.Skip(i).Take(kw.Count)));
            }
        }
        foreach (var p in parts)
            if ((ByJoinedWords.GetValueOrDefault(p) ?? ByJoinedWords.GetValueOrDefault(TrimDigits(p))) is { } l)
                foreach (var k in l) yield return (k, p);
    }

    static string TrimDigits(string w)
    {
        int n = w.Length;
        while (n > 0 && char.IsAsciiDigit(w[n - 1])) n--;
        return n == 0 ? w : w[..n];
    }
}
