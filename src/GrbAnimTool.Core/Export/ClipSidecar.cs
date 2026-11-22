using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Export;

/// <summary>The sidecar written next to a clip's .glb (same base name, <c>.grbanim.json</c>): what the clip is, its whole track list and
/// where each track went, and every track the .glb can't carry, decoded where a reader exists (rotations, positions, bytes, floats, foot
/// plants) and always as the curve's stored bytes; the event objects and their times as far as they read. With it an import can put the
/// clip back without losing what Blender doesn't hold. Values are in the game's axes and units (metres), times in ticks of 1/60 s, the
/// events' times in 1/120 s as stored.</summary>
public sealed record ClipSidecar
{
    public const string FormatName = "grbanim-clip";
    public const int CurrentVersion = 1;
    public const string Extension = ".grbanim.json";

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = CurrentVersion;
    public string Tool { get; init; } = ToolInfo.Name;
    public string ToolVersion { get; init; } = ToolInfo.Version;
    /// <summary>The .glb's file name, beside this file.</summary>
    public string Glb { get; init; } = "";
    public SidecarConventions Conventions { get; init; } = new();
    public SidecarClip Clip { get; init; } = new();
    public List<SidecarTrack> Tracks { get; init; } = [];
    /// <summary>Every track the .glb doesn't carry: helpers, IK offsets, markers and other tracks naming no skeleton bone, bytes, floats,
    /// plants, and the forms no reader decodes.</summary>
    public List<SidecarValues> OtherTracks { get; init; } = [];
    public SidecarEvents Events { get; init; } = new();

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // a float track's value may be a hash whose bits aren't a finite float; FloatBits keeps it exactly
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // the file is read by people and other programs, not put into a web page: an apostrophe stays an apostrophe
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a sidecar; throws <see cref="InvalidDataException"/> for one that isn't this format or version.</summary>
    public static ClipSidecar FromJson(string json)
    {
        ClipSidecar? s;
        try { s = JsonSerializer.Deserialize<ClipSidecar>(json, Options); }
        catch (JsonException ex) { throw new InvalidDataException($"the sidecar can't be read: {ex.Message}", ex); }
        if (s is null || s.Format != FormatName) throw new InvalidDataException($"not a {FormatName} sidecar");
        if (s.FormatVersion != CurrentVersion) throw new InvalidDataException($"a sidecar of version {s.FormatVersion}; this tool reads version {CurrentVersion}");
        return s;
    }

    /// <summary>The sidecar of one clip: <paramref name="sampled"/> says which tracks the .glb carries.</summary>
    public static ClipSidecar Of(ClipRecord c, SharedCurveTable? table, Skeleton skeleton, BoneNames names, SampledClip sampled, string glbFile,
        int index, ClipStatus status)
    {
        var carried = sampled.CarriedTracks().ToHashSet();
        var tracks = new List<SidecarTrack>();
        var others = new List<SidecarValues>();
        for (int i = 0; i < c.Tracks.Count; i++)
        {
            uint h = c.Tracks[i];
            var stored = i < c.Curves.Count ? c.Curves[i] : null;
            var u = stored is null ? null : SharedCurveTable.Resolve(stored, table);
            int? shared = stored is { IsReference: true } ? (int)stored.Ref : null;
            string? name = names.NameOf(h);
            string carriedBy = !carried.Contains(i) ? "sidecar" : h == 0 ? "armature" : "bone";
            string? node = carriedBy == "armature" ? GlbWriter.ArmatureNode : carriedBy == "bone" ? skeleton.Bones[skeleton.IndexOf(h)].Name : null;
            tracks.Add(new SidecarTrack
            {
                Index = i, Hash = Hex(h), Name = name, Kind = u is null ? "Unresolved" : u.Kind.ToString(), Tag = u?.Tag ?? -1, Form = u?.Form ?? -1,
                Keys = u?.Keys ?? 0, SharedCurve = shared, CarriedBy = carriedBy, Node = node,
            });
            if (carriedBy == "sidecar") others.Add(Values(i, h, name, u, shared));
        }
        var groups = EventObjects.Groups(c);
        return new ClipSidecar
        {
            Glb = glbFile,
            Clip = new SidecarClip
            {
                Id = $"0x{c.Id:X16}", Name = c.Name, Index = index, Duration = c.Duration, Ticks = c.LastTick, Status = status.ToString(),
                RecordBytes = c.Bytes.Length, RecordSha256 = Convert.ToHexString(SHA256.HashData(c.Bytes)).ToLowerInvariant(),
                Word1 = Hex(c.Word1), Word2 = Hex(c.Word2), TrackListWord = Hex(c.TrackListWord), TrackCount = c.Tracks.Count,
            },
            Tracks = tracks,
            OtherTracks = others,
            Events = new SidecarEvents
            {
                Objects = EventObjects.Before(c).Select(o => new SidecarObject { At = o.At, Class = Hex(o.Class), Length = o.Length }).ToList(),
                Groups = groups.Select(g => new SidecarEventGroup
                {
                    At = g.At, DeclaredCount = g.DeclaredCount,
                    Events = g.Events.Select(e => new SidecarEvent { At = e.At, Class = Hex(e.Class), Type = e.Type }).ToList(),
                    TimesAt = g.TimesAt,
                    Times120 = g.TimesAt >= 0 ? [.. g.Times] : null,
                    Seconds = g.TimesAt >= 0 ? g.Times.Select(t => t / 120f).ToList() : null,
                }).ToList(),
            },
        };
    }

    static string Hex(uint v) => v.ToString("X8");

    static SidecarValues Values(int index, uint hash, string? name, Curve? u, int? shared)
    {
        if (u is null) return new SidecarValues { Index = index, Hash = Hex(hash), Name = name, Kind = "Unresolved", SharedCurve = shared };
        int k = (int)Math.Min(u.Keys, int.MaxValue);
        var v = new SidecarValues
        {
            Index = index, Hash = Hex(hash), Name = name, Kind = u.Kind.ToString(), Tag = u.Tag, Form = u.Form, SharedCurve = shared,
            Ticks = [.. CurveDecoder.KeyTicks(u).Take(k)],
            CurveHex = ScalarDecoder.Stored(u) is { } st ? Convert.ToHexString(st) : null,
        };
        if (k == 0) return v;
        if (CurveTags.IsRotation(u.Tag) && Enumerable.Range(0, k).Select(i => CurveDecoder.KeyRot(u, i)).ToList() is var rs && rs.All(q => q is not null))
            return v with { Rotations = rs.Select(q => new[] { q!.Value.X, q.Value.Y, q.Value.Z, q.Value.W }).ToList() };
        if (CurveTags.IsPosition(u.Tag) && Enumerable.Range(0, k).Select(i => CurveDecoder.KeyPos(u, i)).ToList() is var ps && ps.All(p => p is not null))
            return v with { Positions = ps.Select(p => new[] { p!.Value.X, p.Value.Y, p.Value.Z }).ToList() };
        if (ScalarDecoder.Bytes(u) is { } bytes) return v with { Bytes = bytes.Select(x => (int)x).ToList() };
        if (ScalarDecoder.Floats(u) is { } floats)
            return v with { Floats = [.. floats], FloatBits = Enumerable.Range(0, k).Select(i => Hex(ScalarDecoder.KeyFloatBits(u, i)!.Value)).ToList() };
        if (u.Kind == TrackKind.Plant && Enumerable.Range(0, k).Select(i => CurveDecoder.Plant(u, i)).ToList() is var pl && pl.All(p => p is not null))
            return v with
            {
                Slot = hash >= 0xFFFFFFE4 ? (int)(hash - 0xFFFFFFE4) : null,
                Plants = pl.Select(p => new SidecarPlant { Tick = p!.Value.Tick, LengthTicks = p.Value.LengthTicks, Point = [p.Value.Point.X, p.Value.Point.Y, p.Value.Point.Z] }).ToList(),
            };
        var raw = Enumerable.Range(0, k).Select(i => ScalarDecoder.KeyRaw(u, i)).ToList();
        return raw.All(r => r is not null) ? v with { Raw = raw.Select(r => Convert.ToHexString(r!)).ToList() } : v;
    }
}

public sealed record SidecarConventions
{
    public int FramesPerSecond { get; init; } = ClipRecord.TicksPerSecond;
    public string Units { get; init; } = "metres";
    public string Axes { get; init; } = "the game's model space: x the character's left-right (its left at -x), y forward, z up; the .glb holds every vector turned by (x, y, z) to (x, z, -y) for glTF's y up";
    public string ArmatureNode { get; init; } = GlbWriter.ArmatureNode;
    public string Mover { get; init; } = "track 0, the root motion, is the armature node's own channels";
    public string Keys { get; init; } = "the .glb: every tick from 0 to the last, LINEAR; this file: each track's own keys";
}

public sealed record SidecarClip
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The record's index in the animation datafile.</summary>
    public int Index { get; init; }
    public float Duration { get; init; }
    /// <summary>The last tick (duration x 60, rounded): the clip has this many ticks plus one frames.</summary>
    public int Ticks { get; init; }
    public string Status { get; init; } = "";
    public int RecordBytes { get; init; }
    public string RecordSha256 { get; init; } = "";
    public string Word1 { get; init; } = "";
    public string Word2 { get; init; } = "";
    public string TrackListWord { get; init; } = "";
    public int TrackCount { get; init; }
}

public sealed record SidecarTrack
{
    public int Index { get; init; }
    public string Hash { get; init; } = "";
    public string? Name { get; init; }
    public string Kind { get; init; } = "";
    public int Tag { get; init; }
    public int Form { get; init; }
    public uint Keys { get; init; }
    /// <summary>The shared table's curve index (from 1) when the track's curve is a reference.</summary>
    public int? SharedCurve { get; init; }
    /// <summary>"bone" (a channel of the node <see cref="Node"/>), "armature" (the mover on the armature's node) or "sidecar".</summary>
    public string CarriedBy { get; init; } = "";
    public string? Node { get; init; }
}

public sealed record SidecarValues
{
    public int Index { get; init; }
    public string Hash { get; init; } = "";
    public string? Name { get; init; }
    public string Kind { get; init; } = "";
    public int Tag { get; init; }
    public int Form { get; init; }
    public int? SharedCurve { get; init; }
    /// <summary>The foot plant slot (0 left, 1 right) of a plant track.</summary>
    public int? Slot { get; init; }
    public List<int> Ticks { get; init; } = [];
    public List<float[]>? Rotations { get; init; }
    public List<float[]>? Positions { get; init; }
    public List<int>? Bytes { get; init; }
    public List<float>? Floats { get; init; }
    /// <summary>The floats' bits as eight hex digits: in the helper tracks, the hash of the bone they name.</summary>
    public List<string>? FloatBits { get; init; }
    public List<SidecarPlant>? Plants { get; init; }
    /// <summary>Each key's value as stored, in hex, for the forms no reader decodes.</summary>
    public List<string>? Raw { get; init; }
    /// <summary>The curve as stored (a reference's curve in the shared table), in hex.</summary>
    public string? CurveHex { get; init; }
}

public sealed record SidecarPlant
{
    public int Tick { get; init; }
    public int LengthTicks { get; init; }
    /// <summary>Metres: x lateral, y forward, z height.</summary>
    public float[] Point { get; init; } = [];
}

public sealed record SidecarEvents
{
    public string Note { get; init; } = "objects found by their handle pattern before the track list; times in 1/120 s as stored";
    public List<SidecarObject> Objects { get; init; } = [];
    public List<SidecarEventGroup> Groups { get; init; } = [];
}

public sealed record SidecarObject
{
    public int At { get; init; }
    public string Class { get; init; } = "";
    public int Length { get; init; }
}

public sealed record SidecarEventGroup
{
    public int At { get; init; }
    /// <summary>The group object's event count, -1 for objects standing before any group object.</summary>
    public int DeclaredCount { get; init; }
    public List<SidecarEvent> Events { get; init; } = [];
    /// <summary>Where the times stand in the record, -1 when no list of the count was found.</summary>
    public int TimesAt { get; init; }
    public List<int>? Times120 { get; init; }
    public List<float>? Seconds { get; init; }
}

public sealed record SidecarEvent
{
    public int At { get; init; }
    public string Class { get; init; } = "";
    public int Type { get; init; }
}
