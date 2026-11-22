namespace GrbAnimTool.Core.Index;

/// <summary>The two kinds of category a clip has: who plays it, and what it is.</summary>
public enum CategoryKind
{
    Who,
    What,
}

/// <summary>Where a category came from, best first: the game's animation graph, the list shipped with the tool, the clip's name. A
/// better source wins per kind; a worse one only fills a kind the better ones left empty.</summary>
public enum CategorySource
{
    Graph = 0,
    ToolList = 1,
    Name = 2,
}

/// <summary>One category of a clip: its name from the vocabulary, the source that decided it, and why (the rule's reason, in the
/// tool's own words).</summary>
public sealed record ClipCategory(string Name, CategorySource Source, string Reason)
{
    /// <summary>For a who-category of the game's graph: the part of the graph that decided it, the layers in plain words with their roots'
    /// names (and the machine on the way that narrowed one), those played in the world first ("the player's layer (MCH-LayerGraph); the
    /// menus' character's layer (Avatar-LayerGraph)"). Null for any other category.</summary>
    public string? Part { get; init; }

    /// <summary>Added beside the categories the best source gave (the menu rule's Menu and lobby beside a list's Idle): it doesn't count
    /// toward which source decided the kind (<see cref="ClipIndexEntry.SourceOf"/>).</summary>
    public bool Beside { get; init; }

    /// <summary>The source as the clip info shows it.</summary>
    public string SourceLabel => ClipCategories.Label(Source);
}

/// <summary>The category vocabulary, exactly these strings, and the source labels the screens show.</summary>
public static class ClipCategories
{
    public const string Uncategorised = "Uncategorised";

    // who plays the clip
    public const string Player = "Player";
    /// <summary>The AI teammates: the graph's NPC machine gives them a layer of their own, apart from the other NPCs'.</summary>
    public const string Teammates = "Teammates";
    public const string Enemy = "Enemy";
    /// <summary>The other non-player characters, civilians and allies; the AI teammates are <see cref="Teammates"/>.</summary>
    public const string Npc = "NPC";
    public const string Animal = "Animal";
    public const string VehicleItself = "Vehicle";
    public const string Cinematic = "Cinematic";

    // what the clip is
    public const string Locomotion = "Locomotion";
    public const string Idle = "Idle";
    public const string WeaponHandling = "Weapon handling";
    public const string Emote = "Emote";
    public const string Death = "Death";
    public const string HitReaction = "Hit reaction";
    public const string InjuryAndDowned = "Injury and downed";
    public const string Cover = "Cover";
    public const string Vehicle = "Vehicle";
    public const string Interaction = "Interaction";
    /// <summary>Climb, vault, jump, fall, slide.</summary>
    public const string Traversal = "Traversal";
    public const string Swimming = "Swimming";
    public const string MeleeAndTakedown = "Melee and takedown";
    public const string CarryAndDrag = "Carry and drag";
    public const string FaceAndDialogue = "Face and dialogue";
    public const string Pose = "Pose";
    /// <summary>The clips of the menus, the lobby and the squad screen: by the graph, clips no layer played in the world names, only the
    /// menus' character's layer; by the tool's list, the squad's end-screen victory poses. Beside the clip's other what, never in its place.</summary>
    public const string MenuAndLobby = "Menu and lobby";

    /// <summary>The who-categories in the order the screens list them, <see cref="Uncategorised"/> last.</summary>
    public static readonly IReadOnlyList<string> Who = [Player, Teammates, Enemy, Npc, Animal, VehicleItself, Cinematic, Uncategorised];

    /// <summary>The what-categories in the order the screens list them, <see cref="Uncategorised"/> last.</summary>
    public static readonly IReadOnlyList<string> What =
    [
        Locomotion, Idle, WeaponHandling, Emote, Death, HitReaction, InjuryAndDowned, Cover, Vehicle, Interaction, Traversal, Swimming,
        MeleeAndTakedown, CarryAndDrag, FaceAndDialogue, Pose, MenuAndLobby, Uncategorised,
    ];

    public static IReadOnlyList<string> Of(CategoryKind kind) => kind == CategoryKind.Who ? Who : What;

    /// <summary>Whether <paramref name="name"/> is a category of <paramref name="kind"/>, spelt exactly ("Uncategorised" included).</summary>
    public static bool IsIn(CategoryKind kind, string name) => Of(kind).Contains(name, StringComparer.Ordinal);

    public const string FromGraph = "from the game's graph";
    public const string FromToolList = "from the tool's list";
    public const string FromName = "from the name";

    /// <summary>The source as the clip info shows it.</summary>
    public static string Label(CategorySource s) => s switch
    {
        CategorySource.Graph => FromGraph,
        CategorySource.ToolList => FromToolList,
        _ => FromName,
    };
}
