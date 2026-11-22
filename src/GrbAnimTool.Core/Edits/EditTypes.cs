using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Edits;

/// <summary>Why an edit was refused.</summary>
public enum EditRefusal
{
    /// <summary>The record can't be read, or its curves don't read to its end.</summary>
    Unreadable,
    /// <summary>A parameter is out of its range.</summary>
    BadParameter,
    /// <summary>The animation graph couldn't be read whole, so what holds the clip's length or reads its motion isn't known.</summary>
    GraphIncomplete,
    /// <summary>A graph place holds the clip's length: no trim or speed.</summary>
    LengthHeld,
    /// <summary>A motion field names the clip: no mirror or root-motion scale.</summary>
    MotionField,
    /// <summary>The clip holds a hand track (an IK offset, a hand helper or an add-on hand target): no mirror.</summary>
    HandTracks,
    /// <summary>Swap: the two clips' track lists differ (a hash or a kind).</summary>
    TrackListsDiffer,
    /// <summary>Swap: the two clips' lengths differ.</summary>
    LengthsDiffer,
    /// <summary>Root-motion scale: the clip has no mover position, or the mover doesn't travel.</summary>
    NoRootMotion,
    /// <summary>Mirror: a bone track has no counterpart track to take its mirrored values, or the plants hold one foot's slot only.</summary>
    Unpaired,
    /// <summary>The new curves need an arena kind the record's head doesn't hold.</summary>
    ArenaKindMissing,
    /// <summary>An arena kind would drop to 0 where it wasn't, an entry no game clip holds.</summary>
    ArenaKindToZero,
    /// <summary>A curve whose values aren't decoded would hold 256 keys or more.</summary>
    TooManyKeys,
    /// <summary>Something the edit must move can't be moved (an event list not found, a time past the list's range, a first key not at
    /// tick 0, a form with no known arena).</summary>
    CantMove,
    /// <summary>The rebuild refused the new record.</summary>
    Rebuild,
    /// <summary>The written record didn't pass its read-back.</summary>
    ReadBack,
    /// <summary>Trim and speed: the clip's byte array holds a list of times the tool doesn't adjust yet (<see cref="ByteArrayTimes"/>), which
    /// a re-timing would leave out of step.</summary>
    TimesUnread,
    /// <summary>The game's clip list names the clip but no graph node the read found does: a node names it in a form the read misses, so
    /// what holds its length or reads its motion can't be checked. No trim, speed, mirror or root-motion scale.</summary>
    ListedWithoutNodeUse,
    /// <summary>Mirror: the clip holds no bone or mover track (a face's or a dialogue's clip of byte and form-14 channels), so there is no
    /// motion to mirror.</summary>
    NoMotion,
    /// <summary>Mirror: a curve the mirror would rewrite holds 256 keys or more, and the tool writes no such curve until one is seen playing
    /// in game.</summary>
    LongCurve,
    /// <summary>Swap: no other clip of the install has the clip's length and its identical track list.</summary>
    NoSwapSource,
    /// <summary>Trim: the clip lasts under two ticks, so there is nothing to cut.</summary>
    TooShort,
}

/// <summary>An edit was refused; nothing was made. <see cref="Exception.Message"/> says why in plain words, naming the clip.</summary>
public sealed class EditRefusedException(EditRefusal kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public EditRefusal Kind { get; } = kind;
}

/// <summary>What an edit needs besides the clip: the skeleton clips are posed on (the body with the add-on), the shared curve table, and the
/// graph's verdict on the clip (null when the graph wasn't read; the edits that depend on it are then refused).</summary>
public sealed record EditContext(Skeleton Skeleton, SharedCurveTable Table, ClipGraphVerdict? Verdict);

/// <summary>An edited clip: the whole record, parsed again, what the rebuild changed, and notes on what the edit left as it was or
/// measured.</summary>
/// <param name="Bytes">The whole record (type, length, name, head, body), as a datafile holds it.</param>
/// <param name="Record">The record, parsed again; it walks to its end and every key decodes.</param>
/// <param name="Rebuilt">The rebuild's own account (tracks rewritten and kept, the arena kinds before and after).</param>
/// <param name="Notes">Plain notes: what was kept unchanged and why, warnings, measures.</param>
public record EditedClip(byte[] Bytes, ClipRecord Record, RebuiltClip Rebuilt, IReadOnlyList<string> Notes)
{
    /// <summary>The graph verdict's warnings for this edit on this clip (a motion field's slot, a blend that reads the clip's speed or
    /// heading, a graph not read whole), and the edit's own (the mirror's tracks shaped like an IK offset): the edit was made, and these say
    /// what in the game may not follow it.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A root-motion scale's result, with the feet measured on the record as written.</summary>
/// <param name="Plants">The planted spans the foot lock held (per foot, from and to tick).</param>
/// <param name="ResidualSlideCm">The most a planted ankle slides beyond what it slid in the game's clip, read back from the written record (cm).</param>
/// <param name="UnlockedSlideCm">The most it would have slid with the travel scaled and no foot lock (cm).</param>
/// <param name="TravelCm">The mover's ground travel before and after (cm).</param>
public sealed record ScaledClip(byte[] Bytes, ClipRecord Record, RebuiltClip Rebuilt, IReadOnlyList<string> Notes,
    IReadOnlyList<(string Side, int From, int To)> Plants, double ResidualSlideCm, double UnlockedSlideCm, (double Before, double After) TravelCm)
    : EditedClip(Bytes, Record, Rebuilt, Notes);

/// <summary>The tracks an edit treats by name.</summary>
public static class EditTracks
{
    /// <summary>The support hand's IK offset and its left-shoulder counterpart.</summary>
    public static readonly uint[] IkOffsets = [0x58A97CAE, 0xB3D7457E];
    /// <summary>The hand helpers (the hands' weights on the weapon and their targets' names), in no skeleton.</summary>
    public static readonly uint[] HandHelpers = [0x64F9A1CB, 0x6C8CB68C, 0xB663DEC6, 0xF22AE62D];
    /// <summary>The add-on skeleton's hand-target bones.</summary>
    public static readonly uint[] HandTargets = [0x0F208B89, 0x447440F8, 0x8B9974FF, 0xFD962D0E];

    /// <summary>Every hand track: a clip holding any of them is armed, by its tracks.</summary>
    public static readonly IReadOnlySet<uint> Hand = new HashSet<uint>([.. IkOffsets, .. HandHelpers, .. HandTargets]);

    /// <summary>The ground marker: at every tick the mover's remaining way to its end place in the mover's own frame, height 0; its turn times
    /// the mover's is the same at every tick.</summary>
    public const uint Marker = 0x1087C86F;

    /// <summary>The foot plant tracks are 0xFFFFFFE4 + slot; slots pair left (even) with right (odd).</summary>
    public const uint FirstPlantSlot = 0xFFFFFFE4;

    public static bool IsPlantSlot(uint hash) => hash >= FirstPlantSlot && hash <= FirstPlantSlot + 7;

    /// <summary>The foot a plant slot is for: 0 the left (an even slot), 1 the right.</summary>
    public static int PlantSide(uint hash) => (int)((hash - FirstPlantSlot) & 1);

    /// <summary>The frame a foot plant's point stands in: the mover's at the plant's start tick (its point in metres from the mover there,
    /// in the mover's own axes). Its place in the clip's world is <c>mover(tick).Apply(point)</c>.</summary>
    public static Vector3 PlantInWorld(Transform moverAtStart, Vector3 point) => moverAtStart.Apply(point);
    /// <summary>The hand tracks a clip holds, in track order.</summary>
    public static List<uint> HandTracksOf(ClipRecord c) => c.Tracks.Where(Hand.Contains).Distinct().ToList();
}
