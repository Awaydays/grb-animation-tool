using System.Globalization;
using System.Reflection;
using System.Text;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Skeletons;

/// <summary>Which skeleton a clip moves: <see cref="Body"/> the human body (the game's characters share its bones, by name: the player's,
/// the enemies', the civilians'; the tool poses every such clip on the player's skeleton, bones it lacks carried as they are),
/// <see cref="Own"/> another skeleton of the game's (an animal, a drone, a vehicle, a door), <see cref="Unknown"/> none the tool can name
/// (a face, a prop, a camera: no one skeleton record holds its bones).</summary>
public enum ClipSkeletonKind { Body, Own, Unknown }

/// <summary>A clip's skeleton: its kind, and for <see cref="ClipSkeletonKind.Own"/> the skeleton record's ID and the forge entry (the
/// datafile) that holds it.</summary>
public sealed record ClipSkeleton(ClipSkeletonKind Kind, ulong SkeletonId = 0, ulong EntryId = 0)
{
    public static readonly ClipSkeleton Body = new(ClipSkeletonKind.Body);
    public static readonly ClipSkeleton Unknown = new(ClipSkeletonKind.Unknown);
}

/// <summary>Works out a clip's skeleton: a clip that keys <see cref="BodyBones"/> or more of the body skeleton's bones is on the body; else
/// the table built into the core (<see cref="ResourceName"/>: from the game's own files, the smallest skeleton record holding every bone
/// the clip keys, by IDs only) names its skeleton; else it is unknown.</summary>
public static class ClipSkeletons
{
    /// <summary>The body bones a clip keys at least to be on the body (of its 53).</summary>
    public const int BodyBones = 30;
    public const string ResourceName = "GrbAnimTool.Core.clip-skeletons.txt";
    const string Header = "# GRB Anim Tool: the skeleton of each game clip not on the human body, by clip ID (hex): the skeleton record's ID and the forge entry holding it";

    static Dictionary<ulong, (ulong Skeleton, ulong Entry)>? _table;

    /// <summary>The table built into the core, by clip ID (empty when this build has none).</summary>
    public static IReadOnlyDictionary<ulong, (ulong Skeleton, ulong Entry)> Table => _table ??= Embedded();

    static Dictionary<ulong, (ulong, ulong)> Embedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return s is null ? [] : Read(s);
    }

    /// <summary>Reads the table: one line per clip, its ID, the skeleton record's ID and the entry's, each 16 hex digits; # starts a comment.</summary>
    public static Dictionary<ulong, (ulong, ulong)> Read(Stream s)
    {
        using var r = new StreamReader(s, Encoding.ASCII);
        var d = new Dictionary<ulong, (ulong, ulong)>();
        int n = 0;
        while (r.ReadLine() is { } line)
        {
            n++;
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(' ');
            if (p.Length != 3 || p.Any(x => x.Length != 16)
                || !ulong.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong clip)
                || !ulong.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong skel)
                || !ulong.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong entry))
                throw new InvalidDataException($"clip skeleton table, line {n}: not three IDs");
            if (!d.TryAdd(clip, (skel, entry))) throw new InvalidDataException($"clip skeleton table, line {n}: clip {clip:X16} twice");
        }
        return d;
    }

    /// <summary>Writes the table (sorted by clip ID), for the harness's table command.</summary>
    public static void Write(TextWriter w, IEnumerable<(ulong Clip, ulong Skeleton, ulong Entry)> rows)
    {
        w.Write(Header + "\n");
        foreach (var (c, s, e) in rows.OrderBy(x => x.Clip)) w.Write($"{c:X16} {s:X16} {e:X16}\n");
    }

    /// <summary>The bones a clip keys: the hashes of its rotation and position tracks (shared curves resolved), the mover left out.</summary>
    public static HashSet<uint> BoneTracks(ClipRecord c, SharedCurveTable? table)
    {
        var hs = new HashSet<uint>();
        for (int i = 0; i < Math.Min(c.Tracks.Count, c.Curves.Count); i++)
        {
            var cv = SharedCurveTable.Resolve(c.Curves[i], table) ?? c.Curves[i];
            if (c.Tracks[i] != 0 && cv.Kind is TrackKind.Rotation or TrackKind.Position) hs.Add(c.Tracks[i]);
        }
        return hs;
    }

    /// <summary>The clip's skeleton (see the class): on the body by its tracks, else by the table.</summary>
    public static ClipSkeleton Of(ulong clipId, ClipRecord c, SharedCurveTable? table, Skeleton body, IReadOnlyDictionary<ulong, (ulong Skeleton, ulong Entry)>? own = null)
    {
        var bones = BoneTracks(c, table);
        if (bones.Count(body.Contains) >= BodyBones) return ClipSkeleton.Body;
        return (own ?? Table).TryGetValue(clipId, out var t) ? new ClipSkeleton(ClipSkeletonKind.Own, t.Skeleton, t.Entry) : ClipSkeleton.Unknown;
    }

    /// <summary>The share of <paramref name="slotBones"/> (the bones a clip keys) that <paramref name="source"/> has, by name: the track
    /// map's rule takes a source's bone where the slot keys one of its name, the rest pose where it has none.</summary>
    public static double Shared(IReadOnlyCollection<uint> slotBones, Skeleton source) =>
        slotBones.Count == 0 ? 0 : slotBones.Count(source.Contains) / (double)slotBones.Count;

    /// <summary>Two skeletons map onto each other when the source holds at least this share of the bones the slot keys.</summary>
    public const double MapShare = 0.9;
}
