namespace GrbAnimTool.Core.Checks;

/// <summary>The hand tracks: the tracks by which the game places a hand on a weapon or a prop. A clip holding any of them is an "armed"
/// clip here, by its tracks, not its name. The game aims the hand by them, so a new motion that moves a hand without them may see the hand
/// pulled back to where the old clip had it.</summary>
public static class HandTracks
{
    /// <summary>The IK offsets.</summary>
    public static readonly IReadOnlyList<uint> IkOffsets = [0x58A97CAE, 0xB3D7457E];
    /// <summary>The helpers (6C8CB68C holds the hand weights).</summary>
    public static readonly IReadOnlyList<uint> Helpers = [0x64F9A1CB, 0x6C8CB68C, 0xB663DEC6, 0xF22AE62D];
    /// <summary>The fake-gun add-on's hand-target bones.</summary>
    public static readonly IReadOnlyList<uint> AddOnTargets = [0x0F208B89, 0x447440F8, 0x8B9974FF, 0xFD962D0E];

    public static readonly IReadOnlySet<uint> All = IkOffsets.Concat(Helpers).Concat(AddOnTargets).ToHashSet();

    public static bool IsHandTrack(uint hash) => All.Contains(hash);

    /// <summary>The hand tracks among <paramref name="tracks"/>, each once, in order.</summary>
    public static IReadOnlyList<uint> In(IEnumerable<uint> tracks) => tracks.Where(All.Contains).Distinct().ToList();

    /// <summary>Whether a clip of these tracks is armed: it holds any hand track.</summary>
    public static bool IsArmed(IEnumerable<uint> tracks) => tracks.Any(All.Contains);
}
