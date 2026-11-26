using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Retiming;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Edits;

/// <summary>What one clip holds that the edits care about, and how each edit fares on it.</summary>
public sealed class ClipEditFacts
{
    public required ClipEntry Clip { get; init; }
    public bool Walked { get; init; }
    public bool GraphComplete { get; init; }
    public bool MotionField { get; init; }
    public bool LengthFree { get; init; }
    /// <summary>The verdict allows a change of motion (<see cref="ClipGraphVerdict.CanChangeMotion"/>): mirror and root-motion scale.</summary>
    public bool MotionFree { get; init; }
    /// <summary>A clip node naming the clip is a blend's child (<see cref="ClipGraphVerdict.UnderBlend"/>).</summary>
    public bool UnderBlend { get; init; }
    public IReadOnlyList<uint> HandTracks { get; init; } = [];
    /// <summary>The track list as the swap compares it (every hash and kind), with the length: equal keys make swappable clips.</summary>
    public string SwapKey { get; init; } = "";
    public int Ticks { get; init; }
    /// <summary>Multi-key curves by what the re-timing writes for them.</summary>
    public int ByteCurves, FloatCurves, RawCurves, BitmapNonBone, Plants;
    /// <summary>The tags of the multi-key curves counted in <see cref="RawCurves"/> and <see cref="BitmapNonBone"/>.</summary>
    public string RawTags = "", BitmapTags = "";
    public bool TimedList83, StepObjects, Marker;
    /// <summary>The clip's byte array holds a list of times the tool doesn't adjust yet (<see cref="ByteArrayTimes.HoldsTimes"/>): trim and
    /// speed are refused.</summary>
    public bool ArrayTimes { get; init; }
    public double MarkerFitCm = double.NaN;
    public double TravelCm, SidewaysCm, TurnDeg;
    /// <summary>Each edit's refusal on this clip (null when it succeeds), and its reason.</summary>
    public Dictionary<string, (EditRefusal? Kind, string? Why)> Edits { get; } = [];

    public string Features() =>
        $"{Ticks,5} t; bytes {ByteCurves}, f18 {FloatCurves}, raw {RawCurves}{(RawCurves > 0 ? $" ({RawTags})" : "")}, bitmap {BitmapNonBone}{(BitmapNonBone > 0 ? $" ({BitmapTags})" : "")}, plants {Plants}; 83EC6C6D {(TimedList83 ? "y" : "-")}, steps {(StepObjects ? "y" : "-")}, array times {(ArrayTimes ? "y" : "-")}, marker {(Marker ? $"y ({MarkerFitCm:F2} cm)" : "-")}; "
        + FormattableString.Invariant($"travel {TravelCm:F1} cm, sideways {SidewaysCm:F1} cm, turn {TurnDeg:F1} deg; hand {HandTracks.Count}; motion field {(MotionField ? "y" : "-")}; blend {(UnderBlend ? "y" : "-")}; length free {(LengthFree ? "y" : "-")}");
}

/// <summary>A census of an install's clips for the edits: the swappable pairs (one track list, every hash and kind, and one length), the
/// mirrorable clips (no hand track and no motion field), the clips trim and speed may take (no graph place holds their length), the clips
/// root-motion scale may take (no motion field), and, by running each edit, the clips where it is refused for an arena kind the head lacks or
/// for any other reason. The edits run with fixed parameters: trim of a tenth off each end, speed 1.25 and 0.8, mirror, travel 0.5.</summary>
public sealed class EditCensus
{
    public const string Mirror = "mirror", Scale = "scale 0.5", Trim = "trim 10% each end", SpeedUp = "speed 1.25", SlowDown = "speed 0.8";
    public static readonly string[] EditNames = [Mirror, Scale, Trim, SpeedUp, SlowDown];

    public required IReadOnlyList<ClipEditFacts> Clips { get; init; }
    public required bool GraphComplete { get; init; }
    /// <summary>Groups of two or more clips sharing a swap key, each sorted by index.</summary>
    public required IReadOnlyList<IReadOnlyList<ClipEditFacts>> SwapGroups { get; init; }

    public int Walked => Clips.Count(c => c.Walked);
    public long SwapPairs => SwapGroups.Sum(g => (long)g.Count * (g.Count - 1) / 2);
    public int SwapClips => SwapGroups.Sum(g => g.Count);
    public int Mirrorable => Clips.Count(c => c.Walked && c.HandTracks.Count == 0 && c.MotionFree);
    public int LengthFree => Clips.Count(c => c.Walked && c.LengthFree);
    public int Scalable => Clips.Count(c => c.Walked && c.MotionFree);
    public int Armed => Clips.Count(c => c.Walked && c.HandTracks.Count > 0);
    public int MotionFieldClips => Clips.Count(c => c.MotionField);
    public int UnderBlend => Clips.Count(c => c.Walked && c.UnderBlend);
    /// <summary>Walked clips whose byte array holds a list of times the tool doesn't adjust yet; of them, those trim and speed would otherwise
    /// be allowed on.</summary>
    public int ArrayTimes => Clips.Count(c => c.Walked && c.ArrayTimes);
    public int ArrayTimesLengthFree => Clips.Count(c => c.Walked && c.ArrayTimes && c.LengthFree);

    /// <summary>The clips where <paramref name="edit"/> succeeded.</summary>
    public int Succeeded(string edit) => Clips.Count(c => c.Edits.TryGetValue(edit, out var r) && r.Kind is null && r.Why is null);

    /// <summary>Refusals of <paramref name="edit"/> by kind.</summary>
    public SortedDictionary<EditRefusal, int> Refusals(string edit)
    {
        var d = new SortedDictionary<EditRefusal, int>();
        foreach (var c in Clips) if (c.Edits.TryGetValue(edit, out var r) && r.Kind is { } k) d[k] = d.GetValueOrDefault(k) + 1;
        return d;
    }

    public static EditCensus Run(GameInstall g, ClipGraph graph, int? parallelism = null)
    {
        var mirror = new SkeletonMirror(g.Skeleton);
        var bag = new ConcurrentBag<ClipEditFacts>();
        Parallel.ForEach(g.ForgeClips, new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount }, e => bag.Add(Facts(g, graph, mirror, e)));
        var clips = bag.OrderBy(c => c.Clip.Index).ToList();
        var groups = clips.Where(c => c.Walked).GroupBy(c => c.SwapKey).Where(x => x.Count() >= 2)
            .Select(x => (IReadOnlyList<ClipEditFacts>)x.OrderBy(c => c.Clip.Index).ToList()).OrderByDescending(x => x.Count).ThenBy(x => x[0].Clip.Index).ToList();
        return new EditCensus { Clips = clips, GraphComplete = graph.Complete, SwapGroups = groups };
    }

    /// <summary>One clip's facts, each edit run on it.</summary>
    public static ClipEditFacts Facts(GameInstall g, ClipGraph graph, SkeletonMirror mirror, ClipEntry e)
    {
        var c = e.Record;
        var v = graph.Verdict(e.Id);
        if (c is null || !c.WalkedToEnd || !c.DurationValid)
            return new ClipEditFacts { Clip = e, Walked = false, GraphComplete = v.MotionFieldKnown, MotionField = v.IsMotionField, LengthFree = v.CanChangeLength, MotionFree = v.CanChangeMotion, UnderBlend = v.UnderBlend };
        var t = g.SharedTable;
        // the length as the swap compares it: the tick and the duration's own bits
        var key = new StringBuilder().Append(c.LastTick).Append('/').Append(BitConverter.SingleToInt32Bits(c.Duration).ToString("X8")).Append(':');
        int bytes = 0, floats = 0, raw = 0, bitmap = 0, plants = 0;
        bool resolvedAll = true;
        var rawTags = new SortedSet<byte>(); var bitmapTags = new SortedSet<byte>();
        for (int i = 0; i < c.Tracks.Count; i++)
        {
            var u = SharedCurveTable.Resolve(c.Curves[i], t);
            if (u is null) { resolvedAll = false; key.Append($"{c.Tracks[i]:X8}?,"); continue; }
            key.Append($"{c.Tracks[i]:X8}{(int)u.Kind},");
            if (u.Kind == TrackKind.Plant) plants++;
            if (u.Keys < 2) continue;
            if (u.Kind is TrackKind.Rotation or TrackKind.Position or TrackKind.Plant) continue;
            if (u.Form == 13) bytes++; else if (u.Form == 18) floats++; else { raw++; rawTags.Add(u.Tag); }
            if (u.Tag % 3 == 2) { bitmap++; bitmapTags.Add(u.Tag); }
        }
        var groups = EventObjects.Groups(c);
        bool timed = groups.Any(x => x.TimesAt >= 0 && x.Events.Any(ev => ev.Class == 0x83EC6C6D));
        bool steps = StepTimeList.Find(c) is not null || groups.Any(x => x.Events.Any(ev => ev.Class == EventObjects.StepEvent));
        GroundMarker? marker = null;
        double travel = 0, side = 0, turn = 0;
        if (resolvedAll)
        {
            try
            {
                marker = GroundMarker.Of(c, t);
                var m = GroundMarker.Mover(c, t, c.LastTick);
                var inv = Quaternion.Inverse(m[0].Rotation);
                foreach (var x in m)
                {
                    var d = Vector3.Transform(x.Position - m[0].Position, inv);
                    travel = Math.Max(travel, new Vector2(d.X, d.Y).Length() * 100);
                    side = Math.Max(side, Math.Abs(d.X) * 100);
                    turn = Math.Max(turn, CurveValues.Deg(m[0].Rotation, x.Rotation));
                }
            }
            catch (EditRefusedException) { }
        }
        var f = new ClipEditFacts
        {
            Clip = e, Walked = true, GraphComplete = v.MotionFieldKnown, MotionField = v.IsMotionField, LengthFree = v.CanChangeLength, MotionFree = v.CanChangeMotion, UnderBlend = v.UnderBlend,
            HandTracks = EditTracks.HandTracksOf(c), SwapKey = key.ToString(), Ticks = c.LastTick,
            ByteCurves = bytes, FloatCurves = floats, RawCurves = raw, BitmapNonBone = bitmap, Plants = plants,
            RawTags = string.Join(" ", rawTags.Select(x => $"0x{x:X2}")), BitmapTags = string.Join(" ", bitmapTags.Select(x => $"0x{x:X2}")),
            TimedList83 = timed, StepObjects = steps, Marker = marker is not null, MarkerFitCm = marker?.FitCm ?? double.NaN, ArrayTimes = ByteArrayTimes.HoldsTimes(c),
            TravelCm = travel, SidewaysCm = side, TurnDeg = turn,
        };
        var ctx = new EditContext(g.Skeleton, t, v);
        int last = c.LastTick, cut = Math.Max(1, last / 10);
        Try(f, Mirror, () => MirrorEdit.Apply(c, ctx, mirror));
        Try(f, Scale, () => RootScaleEdit.Apply(c, ctx, 0.5f, measure: false));
        Try(f, Trim, () => RetimeEdit.Trim(c, ctx, last > 2 * cut ? cut : 0, last > 2 * cut ? last - cut : last));
        Try(f, SpeedUp, () => RetimeEdit.Speed(c, ctx, 1.25));
        Try(f, SlowDown, () => RetimeEdit.Speed(c, ctx, 0.8));
        return f;
    }

    static void Try(ClipEditFacts f, string edit, Func<EditedClip> run)
    {
        try { run(); f.Edits[edit] = (null, null); }
        catch (EditRefusedException ex) { f.Edits[edit] = (ex.Kind, ex.Message); }
        // anything else is a fault of the edit's own code: kept, counted and shown, never taken for a success
        catch (Exception ex) { f.Edits[edit] = (null, $"fault: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The clips where <paramref name="edit"/> failed by a fault of its own code (neither a result nor a refusal).</summary>
    public IEnumerable<ClipEditFacts> Faults(string edit) => Clips.Where(c => c.Edits.TryGetValue(edit, out var r) && r.Kind is null && r.Why is not null);

    /// <summary>The counts as lines.</summary>
    public IEnumerable<string> Summary()
    {
        yield return $"clips {Clips.Count}, walked {Walked}; graph read whole: {GraphComplete}";
        yield return $"swappable: {SwapGroups.Count} groups of clips with one track list (every hash and kind) and one length, {SwapClips} clips in them, {SwapPairs} pairs";
        yield return $"armed (a hand track) {Armed}; motion-field clips {MotionFieldClips}";
        yield return $"mirrorable (no hand track, no motion field): {Mirrorable}; the mirror succeeds on {Succeeded(Mirror)}";
        yield return $"trim and speed allowed (no graph place holds the length): {LengthFree}; succeed: trim {Succeeded(Trim)}, speed 1.25 {Succeeded(SpeedUp)}, speed 0.8 {Succeeded(SlowDown)}";
        yield return $"a list of times in the byte array, not adjusted yet (trim and speed refused): {ArrayTimes} clips; of them no graph place holds the length {ArrayTimesLengthFree}";
        yield return $"root-motion scale allowed (no motion field): {Scalable}; succeeds on {Succeeded(Scale)}";
        yield return $"under a blend's child, up to three levels up (class 0x{GraphClasses.BlendChild:X8} the parent of a clip node naming it, or above that parent within three levels; speed, scale and mirror carry a warning): {UnderBlend} clips; "
            + $"of them trim and speed allowed {Clips.Count(c => c.Walked && c.UnderBlend && c.LengthFree)}, mirror and scale allowed {Clips.Count(c => c.Walked && c.UnderBlend && c.MotionFree)}";
        foreach (var ed in EditNames)
            yield return $"   {ed} refused: " + string.Join(", ", Refusals(ed).Select(k => $"{k.Key} {k.Value}")) + $"; faults {Faults(ed).Count()}";
    }
}
