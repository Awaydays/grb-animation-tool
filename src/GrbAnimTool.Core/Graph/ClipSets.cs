using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace GrbAnimTool.Core.Graph;

/// <summary>How a set's clips are chosen. <see cref="Random"/>: one at random each time (a random selection, or an interval pick or switch
/// on a random value). <see cref="Sequence"/>: one after another. <see cref="Blended"/>: blended by a value (aim, heading, speed), so
/// replacing them needs matching poses, not variety. <see cref="Probable"/>: grouped by name, not read from the graph.</summary>
/// <summary>A set's kind. <see cref="Chosen"/> is no set of the game's: clips the player selected together, replaced as a set is.</summary>
public enum ClipSetKind { Random, Sequence, Blended, Probable, Chosen }

/// <summary>A set: clips the game chooses between or cycles through in one place, in the graph's order. <see cref="Input"/> is what a
/// blended set reads (a value's name); <see cref="BlendSeconds"/> the blend the state uses when it picks again, when read.</summary>
public sealed record ClipSet(ClipSetKind Kind, IReadOnlyList<ulong> Clips, string Where, string? Input = null, double? BlendSeconds = null)
{
    /// <summary>Read from the game's graph (not grouped by name, nor chosen by the player).</summary>
    public bool FromGraph => Kind is not (ClipSetKind.Probable or ClipSetKind.Chosen);

    /// <summary>The set's kind in the screens' words.</summary>
    public string Words => Kind switch
    {
        ClipSetKind.Random => "set (random)",
        ClipSetKind.Sequence => "set (in turn)",
        ClipSetKind.Blended => "blended set" + (Input is null ? "" : $" ({Input})"),
        ClipSetKind.Chosen => "clips selected",
        _ => "probable set",
    };

    /// <summary>The state's own name in the graph (the part of <see cref="Where"/> after its machine), or <see cref="Where"/> itself.</summary>
    public string State => Where.LastIndexOf(" / ", StringComparison.Ordinal) is int i and >= 0 ? Where[(i + 3)..] : Where;

    /// <summary>The machine the state is in (the part of <see cref="Where"/> before the state), or null.</summary>
    public string? Machine => Where.LastIndexOf(" / ", StringComparison.Ordinal) is int i and >= 0 ? Where[..i] : null;

    /// <summary>The clips selected together (<see cref="ClipSetKind.Chosen"/>), in the order given.</summary>
    public static ClipSet ChosenOf(IReadOnlyList<ulong> clips) => new(ClipSetKind.Chosen, clips, "the clips selected");

    /// <summary>The blend between two clips of the set in a preview: the state's own when read, else <see cref="DefaultBlendSeconds"/>.</summary>
    public double PreviewBlend => BlendSeconds ?? DefaultBlendSeconds;
    public const double DefaultBlendSeconds = 0.3;
}

/// <summary>The sets of an install's graph. A state's nodes are read by its variable table (the object before its root): each node takes
/// its own count of the table's values in node order, and a value of kind 8, type 08 holding v links node v + 1. A random selection's and a
/// sequence's children are all its links; an interval pick's, a switch's and a blend's are the links after their first value (the input).
/// A state that doesn't read this way is left out, and its clips can only be grouped by name (<see cref="ClipSetKind.Probable"/>): the
/// same name with a numbered ending, the same family (the name before the number) and a length within a factor of two. A clip a state
/// that read names (in a set or not: a condition picks it, or it plays alone) is never put in a probable one.</summary>
public sealed class ClipSets
{
    const uint Root = 0xF55E36AE, Table = 0xFFCBD7D4, IdObject = 0xBB7CBDA3, StateObject = 0x6D659EAB, RandomSelection = 0xEB8D3D0C,
        IntervalPick = 0x5314B576, SequenceNode = 0x2A35981C, Blend = 0x12E2CE59, Switch = 0xD1C36D96, Property = 0x4EEB8E90,
        Exit = 0xFFCCFAB1, ExitTest = 0x98396CCA, TagTest = 0xD38E0067, Markup = 0x2AAA008F;
    /// <summary>Nodes that only pass a value on, followed to the value they read.</summary>
    static readonly HashSet<uint> ValueNodes = [0xE238ECE7, 0x2CE2E567, 0x781F597C, 0xF6B45AF3];
    /// <summary>The markup a state with a random selection picks again on (0x49 in group 4); its exit's blend is the switch's blend.</summary>
    const uint RepickMarkup = 0x49, RepickGroup = 4;

    static readonly Dictionary<byte, int> ValueSize = new() { [0x01] = 1, [0x07] = 4, [0x08] = 4, [0x10] = 4, [0x19] = 16 };

    readonly Dictionary<ulong, List<ClipSet>> _byClip = [];

    public IReadOnlyList<ClipSet> All { get; }
    /// <summary>States read by the table, and those that weren't (their clips may fall back to names).</summary>
    public int StatesRead { get; }
    public int StatesUnread { get; }

    readonly HashSet<ulong> _inReadStates;

    /// <summary>Whether a state that read names <paramref name="clipId"/>: then a clip in no set is known to be in none (it plays alone, or a
    /// condition picks it); otherwise the tool can't tell.</summary>
    public bool ReadByGraph(ulong clipId) => _inReadStates.Contains(clipId);

    ClipSets(List<ClipSet> all, int read, int unread, HashSet<ulong> inReadStates)
    {
        All = all; StatesRead = read; StatesUnread = unread; _inReadStates = inReadStates;
        foreach (var s in all)
            foreach (var c in s.Clips.Distinct())
                (_byClip.TryGetValue(c, out var l) ? l : _byClip[c] = []).Add(s);
    }

    /// <summary>The sets <paramref name="clipId"/> is in: those read from the graph first (random and in turn before blended), then a probable one.</summary>
    public IReadOnlyList<ClipSet> Of(ulong clipId) => _byClip.TryGetValue(clipId, out var l) ? l : [];

    /// <summary>The set a clip is shown in: its first set (<see cref="Of"/>), or null.</summary>
    public ClipSet? Main(ulong clipId) => Of(clipId) is { Count: > 0 } l ? l[0] : null;

    /// <summary>The sets of <paramref name="graph"/>'s documents; the probable sets from <paramref name="clips"/> (ID, name, length in ticks)
    /// for the clips no graph set holds.</summary>
    public static ClipSets Read(ClipGraph graph, IEnumerable<(ulong Id, string Name, int Ticks)> clips) => Read(graph.Documents, clips);

    public static ClipSets Read(IEnumerable<GraphDocument> documents, IEnumerable<(ulong Id, string Name, int Ticks)> clips) => Read(documents, clips, null);

    /// <summary>An install's sets: from the animation datafile's graph (<paramref name="graph"/>, null when it didn't read: its clips then
    /// grouped by name only) and from the other datafiles' own graph documents, where they read; the other datafiles' clips are never
    /// grouped by name.</summary>
    public static ClipSets ReadAll(Install.GameInstall g, ClipGraph? graph) =>
        Read([.. graph?.Documents ?? [], .. g.OtherGraphDocuments().Documents],
            g.ForgeClips.Concat(g.OtherClips.Where(c => !c.FromFile)).Select(c => (c.Id, c.Name, c.LastTick)), g.OtherClips.Select(c => c.Id).ToHashSet());

    /// <summary>As <see cref="Read(IEnumerable{GraphDocument}, IEnumerable{ValueTuple{ulong, string, int}})"/>; the clips of
    /// <paramref name="noNames"/> (other datafiles' clips) are never grouped by name: they are in a set only where a graph that read says so.</summary>
    public static ClipSets Read(IEnumerable<GraphDocument> documents, IEnumerable<(ulong Id, string Name, int Ticks)> clips, IReadOnlySet<ulong>? noNames)
    {
        if (noNames is not null) clips = clips.Where(c => !noNames.Contains(c.Id)).ToList();
        var docs = documents.ToList();
        var props = PropertyNames(docs);
        var found = new List<ClipSet>();
        var seen = new HashSet<string>();
        // every clip a state that read names: the graph says how it is played, so it is never grouped by name
        var inReadStates = new HashSet<ulong>();
        int read = 0, unread = 0;
        foreach (var d in docs)
            for (int i = 0; i < d.Objects.Count; i++)
            {
                if (d.Objects[i].Class != Root) continue;
                State? s;
                try { s = ReadState(d, i); }
                catch (InvalidDataException) { s = null; }
                if (s is null) { unread++; continue; }
                read++;
                foreach (var n in s.Nodes)
                    if (GraphNodes.TryClipNode(d, n.Obj, out var cn)) inReadStates.Add(cn.ClipId);
                double? blend = null;
                foreach (var n in s.Nodes)
                {
                    ClipSetKind? kind = n.Class switch
                    {
                        RandomSelection => ClipSetKind.Random,
                        SequenceNode => ClipSetKind.Sequence,
                        IntervalPick or Switch when n.Vars.Count > 0 && IsRandom(Input(s, n.Vars[0], props)) => ClipSetKind.Random,
                        Blend => ClipSetKind.Blended,
                        _ => null,
                    };
                    if (kind is not { } k) continue;
                    bool hasInput = n.Class is IntervalPick or Switch or Blend;
                    var kids = n.Vars.Skip(hasInput ? 1 : 0).Where(v => v.Kind == 8 && v.Type == 8).Select(v => (int)v.U + 1).ToList();
                    var ids = new List<ulong>();
                    foreach (int c in kids)
                        if (GraphNodes.TryClipNode(d, s.Nodes[c].Obj, out var cn) && !ids.Contains(cn.ClipId)) ids.Add(cn.ClipId);
                    if (ids.Count < 2) continue;
                    if (k == ClipSetKind.Random) blend ??= RepickBlend(s);
                    // one set per place: the same clips chosen the same way in another copy of a state are one set
                    string key = $"{k}|{string.Join(",", ids)}";
                    if (!seen.Add(key)) continue;
                    found.Add(new ClipSet(k, ids, $"{ShortName(d.Name)} / {s.Name ?? "a state"}",
                        k == ClipSetKind.Blended && n.Vars.Count > 0 ? Input(s, n.Vars[0], props) : null, k == ClipSetKind.Random ? blend : null));
                }
            }
        // random and in turn before blended, so a clip shows the set it is picked in
        var all = found.OrderBy(s => s.Kind == ClipSetKind.Blended).ToList();
        all.AddRange(Probable(clips, inReadStates));
        return new ClipSets(all, read, unread, inReadStates);
    }

    /// <summary>A name with a numbered ending: the family (everything before the number, its separator included) and the number.</summary>
    static readonly Regex Numbered = new(@"^(.*\D)(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>The probable sets: clips no state that read names (<paramref name="inGraph"/>), grouped by the name before a numbered ending, each group split so its lengths
    /// lie within a factor of two (ordered by length, a clip over twice the group's shortest starts a new group); groups of two or more.</summary>
    internal static IEnumerable<ClipSet> Probable(IEnumerable<(ulong Id, string Name, int Ticks)> clips, HashSet<ulong> inGraph)
    {
        foreach (var g in clips.Where(c => !inGraph.Contains(c.Id) && c.Ticks > 0).Select(c => (c, m: Numbered.Match(c.Name)))
                     .Where(x => x.m.Success && x.m.Groups[1].Value.Length > 0).GroupBy(x => x.m.Groups[1].Value, StringComparer.Ordinal))
        {
            var byLength = g.Select(x => x.c).OrderBy(c => c.Ticks).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
            var part = new List<(ulong Id, string Name, int Ticks)>();
            foreach (var c in byLength.Append((Id: 0UL, Name: "", Ticks: int.MaxValue)))
            {
                if (part.Count > 0 && (c.Ticks == int.MaxValue || c.Ticks > 2 * part[0].Ticks))
                {
                    if (part.Count >= 2) yield return new ClipSet(ClipSetKind.Probable, part.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Id).ToList(), $"the names {g.Key}*");
                    part = [];
                }
                if (c.Ticks != int.MaxValue) part.Add(c);
            }
        }
    }

    static bool IsRandom(string input) => input.Contains("andom", StringComparison.OrdinalIgnoreCase) || input.Contains("amdom", StringComparison.OrdinalIgnoreCase);

    static string ShortName(string n) => n.Length > 32 && n[^32..].All(char.IsAsciiHexDigit) ? n[..^32].TrimEnd('-') : n;

    sealed record Var(uint Kind, byte Type, uint U, float F, Guid G);
    sealed record Node(int Obj, uint Class, List<Var> Vars);
    sealed record State(GraphDocument Doc, List<Node> Nodes, string? Name, int NameAt);

    static void Need(byte[] b, int p, int n) { if (p < 0 || p + n > b.Length) throw new InvalidDataException("read past the document's end"); }

    /// <summary>The state whose root is object <paramref name="r"/>, by its variable table; null when it doesn't read that way (a value of a
    /// type not known, counts that don't add up, a link outside the state).</summary>
    static State? ReadState(GraphDocument d, int r)
    {
        var o = d.Objects; var b = d.Bytes;
        if (r == 0 || o[r - 1].Class != Table) return null;
        int t = r - 1, p = o[t].At + 4, end = d.ObjectEnd(t);
        Need(b, p, 2);
        if (b[p] != 0x03 || b[p + 1] != 0x60) return null;
        p += 2;
        int nk = Varint(b, ref p);
        var kinds = new uint[nk];
        for (int i = 0; i < nk; i++) { Need(b, p, 4); kinds[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; }
        if (Varint(b, ref p) != nk) return null;
        var vars = new List<Var>(nk);
        for (int i = 0; i < nk; i++)
        {
            Need(b, p, 2);
            byte ty = b[p + 1];
            if (!ValueSize.TryGetValue(ty, out int sz)) return null;
            Need(b, p + 2, sz);
            vars.Add(new Var(kinds[i], ty, sz >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 2)) : b[p + 2],
                sz == 4 ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p + 2)) : 0, sz == 16 ? new Guid(b.AsSpan(p + 2, 16)) : Guid.Empty));
            p += 2 + sz;
        }
        if (p != end - 1) return null;
        Need(b, end - 1, 1);
        int count = b[end - 1];
        var nodes = new List<Node>();
        int k = 0, vi = 0;
        for (int j = r; j + 1 < o.Count && k < count; j++)
        {
            if (o[j].Class == IdObject || o[j + 1].Class != IdObject) continue;
            if (j > r && o[j].Class == StateObject) break;
            int ia = o[j + 1].At;
            Need(b, ia, 24);
            if (b[ia + 4] != 0x02 || b[ia + 5] != 0x40 || (b[ia + 22] | b[ia + 23] << 8) != k) continue;
            int na = o[j].At + 4;
            Need(b, na, 1);
            int m = (b[na] + 7) / 8;
            Need(b, na, 1 + m + 2);
            int vc = b[na + 1 + m] | b[na + 2 + m] << 8;
            if (vi + vc > vars.Count) return null;
            nodes.Add(new Node(j, o[j].Class, vars.GetRange(vi, vc)));
            vi += vc; k++;
        }
        if (k != count || vi != vars.Count || count == 0) return null;
        foreach (var n in nodes)
            foreach (var v in n.Vars)
                if (v.Kind == 8 && v.Type == 8 && (v.U + 1 >= (uint)count)) return null;
        // the state's name: FF FF FF FF 00, a length, 01, the name, after its last node and before the next state object
        int stop = nodes[^1].Obj + 1;
        while (stop < o.Count && o[stop].Class != StateObject) stop++;
        int lim = stop < o.Count ? o[stop].At : b.Length;
        string? name = null; int nameAt = -1;
        for (int q = o[nodes[^1].Obj].At; q + 8 < lim; q++)
            if (b[q] == 0xFF && b[q + 1] == 0xFF && b[q + 2] == 0xFF && b[q + 3] == 0xFF && b[q + 4] == 0 && b[q + 6] == 1 && q + 7 + b[q + 5] <= lim)
            { name = System.Text.Encoding.ASCII.GetString(b, q + 7, b[q + 5]); nameAt = q; break; }
        return new State(d, nodes, name, nameAt);
    }

    static int Varint(byte[] b, ref int p)
    {
        int v = 0;
        for (int s = 0; s < 28; s += 7) { Need(b, p, 1); byte x = b[p++]; v |= (x & 0x7F) << s; if ((x & 0x80) == 0) return v; }
        throw new InvalidDataException("a count too long");
    }

    /// <summary>The value names the documents define: a property object's name by its GUID.</summary>
    static Dictionary<Guid, string> PropertyNames(List<GraphDocument> docs)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var d in docs)
            for (int i = 0; i < d.Objects.Count; i++)
            {
                if (d.Objects[i].Class != Property) continue;
                int a = d.Objects[i].At + 4, end = d.ObjectEnd(i);
                if (a + 4 > end || d.Bytes[a] != 0x05 || d.Bytes[a + 1] != 0x78 || d.Bytes[a + 3] != 0x01) continue;
                int l = d.Bytes[a + 2];
                if (a + 4 + l + 4 + 16 > end) continue;
                names.TryAdd(new Guid(d.Bytes.AsSpan(a + 4 + l + 4, 16)), System.Text.Encoding.ASCII.GetString(d.Bytes, a + 4, l));
            }
        return names;
    }

    /// <summary>What a node's input reads: a value's name, a constant, or the value a passing node reads (a few steps at most).</summary>
    static string Input(State s, Var v, Dictionary<Guid, string> props, int depth = 0)
    {
        if (v.Kind == 0 && v.Type == 0x19) return props.GetValueOrDefault(v.G, "an unnamed value");
        if (v.Kind == 8 && v.Type == 8 && depth < 4)
        {
            var n = s.Nodes[(int)v.U + 1];
            if (!ValueNodes.Contains(n.Class)) return "another node";
            var first = n.Vars.FirstOrDefault(x => x.Kind == 0 && x.Type == 0x19) ?? n.Vars.FirstOrDefault(x => x.Kind == 8 && x.Type == 8);
            return first is null ? "a value" : Input(s, first, props, depth + 1);
        }
        return v.Type == 0x10 ? "a constant" : "a value";
    }

    /// <summary>The blend of the state's own exit on the pick-again markup, in seconds; null when the state has none.</summary>
    static double? RepickBlend(State s)
    {
        var d = s.Doc; var o = d.Objects; var b = d.Bytes;
        for (int j = s.Nodes[^1].Obj + 1; j + 3 < o.Count && (s.NameAt < 0 || o[j].At < s.NameAt); j++)
        {
            if (o[j].Class != Exit || o[j + 1].Class != ExitTest || o[j + 2].Class != TagTest || o[j + 3].Class != Markup) continue;
            int a = o[j + 3].At + 4;
            if (a + 41 > d.ObjectEnd(j + 3)) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(a + 2)) != RepickMarkup || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(a + 6)) != RepickGroup) continue;
            float sec = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(a + 37));
            if (float.IsFinite(sec) && sec is > 0 and < 5) return Math.Round(sec, 3);
        }
        return null;
    }
}
