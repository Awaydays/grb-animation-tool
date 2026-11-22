using System.Buffers.Binary;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Graph;

/// <summary>How a graph place names a clip.</summary>
public enum GraphUseKind
{
    /// <summary>A clip node: by ID, holding no other form of the clip (no duration).</summary>
    ClipNode,
    /// <summary>An entry of a motion field's clip list: by ID. The field's selection reads the clip's motion through its rows.</summary>
    MotionFieldClip,
    /// <summary>A segment of a motion field's table: by its list index, holding the clip's start, covered length and remainder.</summary>
    MotionFieldSegment,
    /// <summary>GR_Graph's clip list in its record's tail: by ID, the ID alone.</summary>
    GraphClipList,
    /// <summary>The clip's ID stands in an object no parser here reads. Treated as holding the clip's duration.</summary>
    Unread,
}

/// <summary>One place in the graph that names a clip: the document, the object's class and where it stands, the list index for a use by
/// index, and the two facts the edits need: whether it holds the clip's length, and whether it is a motion field's.</summary>
public sealed record ClipGraphUse(ulong ClipId, GraphUseKind Kind, uint Class, ulong DocumentId, string DocumentName, int At, int? ListIndex,
    bool HoldsDuration, bool MotionField)
{
    /// <summary>For an unread place: the class of the last object before it that is neither an ID object nor part of a test (a
    /// condition, a group of them, a markup test and its data): the node it belongs to as far as the order shows, else 0.</summary>
    public uint OwnerClass { get; init; }
    /// <summary>For a place in a record's bytes after its stream (its tail) rather than in the document.</summary>
    public bool InRecordTail { get; init; }
    /// <summary>For a place in a record's bytes before its stream.</summary>
    public bool BeforeStream { get; init; }
    /// <summary>For a place in the document's head (its class table and tokens), before the body.</summary>
    public bool InHead { get; init; }
    /// <summary>For a clip node: the class of the nearest object before it that is neither a clip node, an ID object nor part of a test (the
    /// node that reaches it, as far as the order shows), else 0. Such a parent is not read for the clip's length; see <see cref="ClipGraphVerdict"/>.</summary>
    public uint ParentClass { get; init; }
    /// <summary>For a clip node with a parent (<see cref="ParentClass"/> not 0): where the parent's class stands in the document, else -1.</summary>
    public int ParentAt { get; init; } = -1;
    /// <summary>For a clip node: how many levels up a blend's child (<see cref="GraphClasses.BlendChild"/>) stands, walking up by the same
    /// order rule as <see cref="ParentClass"/> (1 the parent itself, 2 the parent's parent, up to <see cref="ClipGraph.BlendLevels"/>), else 0.</summary>
    public int BlendLevel { get; init; }

    public string Describe() => Kind switch
    {
        GraphUseKind.ClipNode => $"clip node 0x{Class:X8} in {DocumentName} at +{At:X}, by ID",
        GraphUseKind.MotionFieldClip => $"motion field clip list 0x{Class:X8} in {DocumentName} at +{At:X}, by ID (list index {ListIndex})",
        GraphUseKind.MotionFieldSegment => $"motion field segment 0x{Class:X8} in {DocumentName} at +{At:X}, by list index {ListIndex}, holds the clip's length",
        GraphUseKind.GraphClipList => $"GR_Graph's clip list at tail +{At:X}, by ID",
        _ => $"unread {(InRecordTail ? "record tail" : BeforeStream ? "record bytes before the stream" : InHead ? "document head" : $"object 0x{Class:X8}")}{(OwnerClass != 0 && OwnerClass != Class ? $" after 0x{OwnerClass:X8}" : "")} in {DocumentName} at +{At:X}, by ID; treated as holding the clip's length",
    };
}

/// <summary>What the graph says about one clip, and the edits it allows. A graph that didn't read whole, or a clip that wasn't among the
/// clips the read looked for, allows nothing that depends on it.
/// <para>What holds a clip's length: the places where the clip's ID stands (a clip node's ID object, a motion field's clip list entry, GR_Graph's
/// list, and any other place the ID stands, read or not, which counts as holding it), and a motion field's segments, which name a list entry by
/// index. That is the whole rule. A node that reaches a clip node through a link (a state's variable table, a blend's child, a pick, a
/// switch) is not looked at, whatever its class, even a class no parser here reads: such a parent is taken to hold no form of the clip's
/// length. <see cref="ClipGraphCensus.ClipNodesUnderUnreadParent"/> counts the clip nodes this limit reaches, and
/// <see cref="ClipGraphCensus.ParentsHoldingOwnDuration"/> against <see cref="ClipGraphCensus.ParentsHoldingControlDuration"/> how often an
/// allowed clip node's parent object holds the clip's own duration against another clip's (in the game's documents, no more often).</para>
/// <para>A clip GR_Graph's list names but no graph node the read found names (<see cref="ListedWithoutNodeUse"/>): in the game the list is
/// exactly the set of clips the clip nodes name, so such a clip is named by a node in a form the read misses, and whether that node holds
/// its length or reads its motion can't be checked. Trim, speed, mirror and root-motion scale are refused, and import warns.</para>
/// <para>Blend children (<see cref="UnderBlend"/>): a clip node whose parent is a blend's child (<see cref="GraphClasses.BlendChild"/>),
/// or whose parent stands under one (a pick or a switch between the blend's child and the clip node): the walk goes up by the order rule
/// (the nearest earlier object that is neither a clip node, an ID object nor part of a test) from the parent, up to
/// <see cref="ClipGraph.BlendLevels"/> levels (the parent being the first, so the blend's child may stand one or two levels above the
/// parent). The order isn't a proven link, so a level above the parent is a likely owner, not a certain one: where objects are written
/// depth first, a parent that follows an earlier sibling's subtree takes that subtree's last object for its owner, so an earlier sibling's
/// subtree may stand in for an ancestor. A blend's child in such a subtree then marks the clip (an extra warning), and a real ancestor
/// behind a deep earlier sibling may be missed (a missing warning); a blend further up than the walk isn't seen. The verdict only ever
/// adds warnings from this, never allows or refuses an edit by it. The child's nominal, which the blend's parameter picks it by, isn't read: on a
/// blend over the move speed it is the clip's root speed (travel over duration), and on a heading blend its side follows the clip's heading.
/// Speed, root-motion scale and mirror stay allowed on such clips but carry <see cref="MotionWarning"/>; import and equal swaps carry
/// <see cref="ReplaceBlendWarning"/> in <see cref="ReplaceWarning"/>. The read doesn't tell a move-speed blend from a heading blend.</para></summary>
public sealed class ClipGraphVerdict
{
    /// <summary>The reason for a clip GR_Graph's list names with no node use (<see cref="ListedWithoutNodeUse"/>).</summary>
    public const string ListedWithoutNodeUseReason = "the game's clip list names this clip but no graph node the tool reads does; its use can't be checked";
    /// <summary>The warning on speed, root-motion scale and mirror for a blend's child (<see cref="UnderBlend"/>).</summary>
    public const string BlendWarning = "this clip is a child of a blend whose settings read the clip's root speed or heading; changing them may leave the blend picking it wrongly";
    /// <summary>The warning on import and equal swaps for a blend's child (<see cref="UnderBlend"/>).</summary>
    public const string ReplaceBlendWarning = "this clip is a child of a blend whose settings read the clip's root speed or heading; if the new clip's root speed or heading differs, the blend may pick it wrongly";

    public required ulong ClipId { get; init; }
    public required IReadOnlyList<ClipGraphUse> Uses { get; init; }
    /// <summary>Every graph document read; when false, nothing is known and every verdict below refuses or warns.</summary>
    public required bool GraphComplete { get; init; }
    /// <summary>The clip was among the clips the read looked for. When false (the default), its uses weren't searched and every verdict
    /// below refuses or warns.</summary>
    public bool Scanned { get; init; }

    /// <summary>Uses by graph nodes (GR_Graph's clip list left out).</summary>
    public IEnumerable<ClipGraphUse> NodeUses => Uses.Where(u => u.Kind != GraphUseKind.GraphClipList);
    public bool HasNodeUse => NodeUses.Any();
    /// <summary>The read is an answer, GR_Graph's list names the clip, and no graph place but the list does: a node names it in a form the
    /// read misses. Every edit that depends on the graph is refused.</summary>
    public bool ListedWithoutNodeUse => MotionFieldKnown && !HasNodeUse && Uses.Any(u => u.Kind == GraphUseKind.GraphClipList);
    /// <summary>Some place holds the clip's length, or may (an unread place, or a node the read misses: <see cref="ListedWithoutNodeUse"/>),
    /// or the graph didn't read, or the clip wasn't looked for.</summary>
    public bool LengthHeldElsewhere => !Scanned || !GraphComplete || ListedWithoutNodeUse || Uses.Any(u => u.HoldsDuration);
    /// <summary>A motion field names the clip, as far as the read found: its segment table holds the clip's length and its selection reads
    /// the clip's motion. Only an answer when <see cref="MotionFieldKnown"/>; otherwise false says nothing.</summary>
    public bool IsMotionField => Uses.Any(u => u.MotionField);
    /// <summary><see cref="IsMotionField"/> is an answer: the graph read whole and the clip was looked for.</summary>
    public bool MotionFieldKnown => GraphComplete && Scanned;
    /// <summary>A clip node naming the clip has a blend's child for its parent, or above its parent within
    /// <see cref="ClipGraph.BlendLevels"/> levels (see the class's remarks): speed, root-motion scale and mirror carry
    /// <see cref="MotionWarning"/>, import and equal swaps <see cref="ReplaceBlendWarning"/>.</summary>
    public bool UnderBlend => UnderBlendDirectly || UnderBlendIndirectly;
    /// <summary>A clip node naming the clip has a blend's child for its parent.</summary>
    public bool UnderBlendDirectly => Uses.Any(u => u.Kind == GraphUseKind.ClipNode && (u.ParentClass == GraphClasses.BlendChild || u.BlendLevel == 1));
    /// <summary>A blend's child stands above a clip node's parent (and none is the parent of any clip node naming the clip).</summary>
    public bool UnderBlendIndirectly => !Uses.Any(u => u.Kind == GraphUseKind.ClipNode && (u.ParentClass == GraphClasses.BlendChild || u.BlendLevel == 1))
        && Uses.Any(u => u.Kind == GraphUseKind.ClipNode && u.BlendLevel > 1);

    /// <summary>Trim and speed: only where no graph place holds the clip's length. Speed carries <see cref="MotionWarning"/> when it is set.</summary>
    public bool CanChangeLength => !LengthHeldElsewhere;
    /// <summary>Mirror and root-motion scale: refused on motion-field clips (and when that isn't known) and on a clip only GR_Graph's list
    /// names; they carry <see cref="MotionWarning"/> when it is set. (The tool offers no swap of unequal length.)</summary>
    public bool CanChangeMotion => MotionFieldKnown && !IsMotionField && !ListedWithoutNodeUse;
    /// <summary>Import and equal swaps: allowed, with a warning (<see cref="ReplaceWarning"/>) on motion-field clips, on a clip only
    /// GR_Graph's list names, when the answer isn't known, and on a blend's child (<see cref="UnderBlend"/>).</summary>
    public bool WarnOnReplace => ReplaceWarning is not null;

    /// <summary>The warning on speed, root-motion scale and mirror where they are allowed (<see cref="BlendWarning"/> for a blend's child), or null.</summary>
    public string? MotionWarning => UnderBlend ? BlendWarning : null;

    /// <summary>Why trim and speed are refused, or null when they are allowed.</summary>
    public string? LengthRefusal => !Scanned ? "this clip wasn't part of the graph read"
        : !GraphComplete ? "the animation graph couldn't be read whole, so what holds this clip's length isn't known"
        : ListedWithoutNodeUse ? ListedWithoutNodeUseReason
        : Uses.FirstOrDefault(u => u.HoldsDuration) is { } u ? $"the clip's length is held by the {u.Describe()}" : null;

    /// <summary>Why mirror and root-motion scale are refused, or null when they are allowed.</summary>
    public string? MotionRefusal => !Scanned ? "this clip wasn't part of the graph read"
        : !GraphComplete ? "the animation graph couldn't be read whole, so whether a motion field uses this clip isn't known"
        : ListedWithoutNodeUse ? ListedWithoutNodeUseReason
        : IsMotionField ? "a motion field's selection reads this clip's motion and its segment table holds its length" : null;

    /// <summary>The warning on import and equal swaps, or null when there is none (<see cref="WarnOnReplace"/> is false): set when the
    /// answer is unknown, on a clip only GR_Graph's list names, on a motion field's clip, and on a blend's child (<see cref="ReplaceBlendWarning"/>,
    /// added after any of the others).</summary>
    public string? ReplaceWarning
    {
        get
        {
            string? main = !MotionFieldKnown
                    ? $"whether a motion field uses this clip isn't known ({(!Scanned ? "this clip wasn't part of the graph read" : "the graph couldn't be read whole")})"
                : ListedWithoutNodeUse ? ListedWithoutNodeUseReason
                : IsMotionField ? "the motion field's rows still describe the game's clip, so its selection may pick it badly" : null;
            if (!UnderBlend) return main;
            return main is null ? ReplaceBlendWarning : $"{main}; also, {ReplaceBlendWarning}";
        }
    }
}

/// <summary>A document that didn't read, and why.</summary>
public sealed record GraphFailure(ulong DocumentId, string Name, string Error);

/// <summary>The animation graph of an install, read for what names each clip of the animation datafile: every graph document's clip nodes,
/// motion fields (clip lists and segment tables) and GR_Graph's clip list, and every other place a clip's ID stands in a document or a
/// document record's bytes, which is treated as holding the clip's length. Graph documents are the datafile's records of the two graph types.</summary>
public sealed class ClipGraph
{
    /// <summary>The graph record GR_Graph.</summary>
    public const ulong GraphRecordId = 0x000001555E68541F;
    /// <summary>How many levels up from a clip node's parent (the parent itself the first) the read looks for a blend's child.</summary>
    public const int BlendLevels = 3;

    public required IReadOnlyList<GraphDocument> Documents { get; init; }
    public required IReadOnlyList<GraphFailure> Failures { get; init; }
    public bool Complete => Failures.Count == 0 && GraphListRead;
    /// <summary>GR_Graph was found and its clip list read.</summary>
    public required bool GraphListRead { get; init; }
    public required int ClipNodes { get; init; }
    /// <summary>Clip nodes whose clip isn't a clip of the datafile (clips of other entries).</summary>
    public required int ClipNodesNamingOtherRecords { get; init; }
    /// <summary>Clip nodes whose ID object doesn't have the clip node's form; any clip ID in them is an unread place.</summary>
    public required int ClipNodesUnread { get; init; }
    public required IReadOnlyList<(GraphDocument Document, MotionField Field)> MotionFields { get; init; }
    public required int GraphClipListEntries { get; init; }
    /// <summary>The uses found for each clip, as the read built them. The lists are the caller's to change; the graph keeps its own copy,
    /// taken when this is set (<see cref="ClipUses"/>), so a change here doesn't reach the verdicts. Not set, no clip has a use (and with
    /// <see cref="ScannedClips"/> empty every verdict refuses).</summary>
    [Obsolete("Use ClipUses, which can't be changed.")]
    public IReadOnlyDictionary<ulong, List<ClipGraphUse>> UsesByClip
    {
        get => _usesByClip;
        init
        {
            _usesByClip = value;
            _clipUses = value.ToDictionary(k => k.Key, k => (IReadOnlyList<ClipGraphUse>)k.Value.ToArray().AsReadOnly());
        }
    }
    IReadOnlyDictionary<ulong, List<ClipGraphUse>> _usesByClip = new Dictionary<ulong, List<ClipGraphUse>>();
    IReadOnlyDictionary<ulong, IReadOnlyList<ClipGraphUse>> _clipUses = new Dictionary<ulong, IReadOnlyList<ClipGraphUse>>();

    /// <summary>The uses found for each clip: a copy no caller can change.</summary>
    public IReadOnlyDictionary<ulong, IReadOnlyList<ClipGraphUse>> ClipUses => _clipUses;
    /// <summary>The clip IDs the read looked for. Only these have their uses found; any other ID's verdict refuses.</summary>
    public IReadOnlySet<ulong> ScannedClips { get; init; } = new HashSet<ulong>();

    readonly Dictionary<ulong, ClipGraphVerdict> _verdicts = [];

    /// <summary>The verdict on one clip. For an ID the read didn't look for (see <see cref="ScannedClips"/>), every edit is refused.</summary>
    public ClipGraphVerdict Verdict(ulong clipId)
    {
        lock (_verdicts)
        {
            if (_verdicts.TryGetValue(clipId, out var v)) return v;
            return _verdicts[clipId] = new ClipGraphVerdict
            {
                ClipId = clipId, GraphComplete = Complete, Scanned = ScannedClips.Contains(clipId),
                Uses = ClipUses.TryGetValue(clipId, out var l) ? l : [],
            };
        }
    }

    public static ClipGraph Read(GameInstall g) => Read(g.Records, g.Clips.Select(c => c.Id));

    /// <summary>Reads every graph document among <paramref name="records"/> and finds where each of <paramref name="clipIds"/> is named.
    /// A document that doesn't read is listed in <see cref="Failures"/>; the graph is then incomplete and every verdict refuses. Only places
    /// where a clip's ID stands, and motion field segments by list index, are taken as holding its length; nodes that reach a clip node by a
    /// link aren't (see <see cref="ClipGraphVerdict"/>).</summary>
    public static ClipGraph Read(IEnumerable<DatafileRecord> records, IEnumerable<ulong> clipIds)
    {
        var clips = clipIds.ToHashSet();
        var highs = clips.Select(id => id >> 40).ToHashSet();   // a cheap first test before the set lookup
        var docs = new List<GraphDocument>();
        var failures = new List<GraphFailure>();
        foreach (var r in records.Where(r => GraphDocument.IsDocumentType(r.Type)))
        {
            try { docs.Add(GraphDocument.Read(r)); }
            catch (GraphDocumentException ex) { failures.Add(new GraphFailure(r.Id, r.Name, ex.Message)); }
        }
        var uses = new Dictionary<ulong, List<ClipGraphUse>>();
        void Add(ClipGraphUse u) { if (!uses.TryGetValue(u.ClipId, out var l)) uses[u.ClipId] = l = []; l.Add(u); }
        int nodes = 0, nodesOther = 0, nodesUnread = 0, listEntries = 0;
        bool listRead = false;
        var fields = new List<(GraphDocument, MotionField)>();
        foreach (var d in docs)
        {
            string name = d.Name;
            var known = new Dictionary<int, ClipGraphUse>();   // places a parser reads, by the clip ID's offset
            var clipNodes = GraphNodes.ClipNodes(d, out int unread);
            foreach (var n in clipNodes)
            {
                nodes++;
                if (!clips.Contains(n.ClipId)) { nodesOther++; continue; }
                int j = Owner(d, n.ObjectIndex - 1);
                // the blend's child at the parent or above it, by the same rule a level at a time
                int level = 0;
                for (int k = j, up = 1; k >= 0 && up <= BlendLevels; k = Owner(d, k - 1), up++)
                    if (d.Objects[k].Class == GraphClasses.BlendChild) { level = up; break; }
                known[n.ClipIdAt] = new ClipGraphUse(n.ClipId, GraphUseKind.ClipNode, GraphClasses.ClipNode, d.Id, name, n.At, null, HoldsDuration: false, MotionField: false)
                    { ParentClass = j >= 0 ? d.Objects[j].Class : 0, ParentAt = j >= 0 ? d.Objects[j].At : -1, BlendLevel = level };
            }
            nodesUnread += unread;
            List<MotionField> mfs;
            try { mfs = GraphNodes.MotionFields(d); }
            catch (InvalidDataException ex)
            {
                // the document read but a motion field didn't: its clips are left to the unread places below (conservative)
                failures.Add(new GraphFailure(d.Id, name, ex.Message));
                mfs = [];
            }
            // each motion field's span: from its node to the end of its last segment (which runs on over the rows block)
            var spans = mfs.Select(mf => (From: mf.At, To: d.ObjectEnd(d.ObjectAtOrBefore(mf.Segments[^1].At)))).ToList();
            foreach (var mf in mfs)
            {
                fields.Add((d, mf));
                for (int k = 0; k < mf.Clips.Count; k++)
                {
                    ulong id = mf.Clips[k];
                    if (!clips.Contains(id)) continue;
                    bool segmented = mf.Segments.Any(s => s.ClipIndex == k);
                    // the entry holds the ID alone; the segments naming it by index hold its length. An entry no segment names is
                    // treated as holding it itself (conservative)
                    known[mf.ClipIdAts[k]] = new ClipGraphUse(id, GraphUseKind.MotionFieldClip, GraphClasses.MotionFieldClip, d.Id, name, mf.ClipIdAts[k], k,
                        HoldsDuration: !segmented, MotionField: true);
                }
                foreach (var s in mf.Segments)
                {
                    ulong id = mf.Clips[s.ClipIndex];
                    if (clips.Contains(id)) Add(new ClipGraphUse(id, GraphUseKind.MotionFieldSegment, GraphClasses.MotionFieldSegment, d.Id, name, s.At, s.ClipIndex, HoldsDuration: true, MotionField: true));
                }
            }
            // every place a clip's ID stands in the document, head and body: a parsed one, or an unread one
            var b = d.Bytes;
            for (int p = 0; p + 8 <= b.Length; p++)
            {
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
                if (!highs.Contains(v >> 40) || !clips.Contains(v)) continue;
                if (known.TryGetValue(p, out var u)) { Add(u); continue; }
                if (p < d.BodyAt)
                {
                    Add(new ClipGraphUse(v, GraphUseKind.Unread, 0, d.Id, name, p, null, HoldsDuration: true, MotionField: false) { InHead = true });
                    continue;
                }
                int i = d.ObjectAtOrBefore(p);
                uint cls = i >= 0 ? d.Objects[i].Class : 0;
                int j = i;
                while (j >= 0 && GraphClasses.IsIdOrTest(d.Objects[j].Class)) j--;
                uint owner = j >= 0 ? d.Objects[j].Class : 0;
                bool inField = cls is GraphClasses.MotionFieldClip or GraphClasses.MotionFieldSegment or GraphClasses.MotionField
                    || spans.Exists(s => p >= s.From && p < s.To);
                Add(new ClipGraphUse(v, GraphUseKind.Unread, cls, d.Id, name, p, null, HoldsDuration: true, MotionField: inField) { OwnerClass = owner });
            }
            // the record's own bytes outside the stream: GR_Graph's clip list, else unread
            var tailKnown = new HashSet<int>();
            var tail = d.Tail.Span;
            if (d.Id == GraphRecordId && d.Type == GraphDocument.GraphType)
            {
                try
                {
                    foreach (var (id, at) in GraphNodes.GraphClipList(tail))
                    {
                        listEntries++;
                        tailKnown.Add(at);
                        if (clips.Contains(id)) Add(new ClipGraphUse(id, GraphUseKind.GraphClipList, GraphClasses.GraphClipListEntry, d.Id, name, at, null, HoldsDuration: false, MotionField: false) { InRecordTail = true });
                    }
                    listRead = true;
                }
                catch (InvalidDataException ex) { failures.Add(new GraphFailure(d.Id, name, $"GR_Graph's clip list: {ex.Message}")); }
            }
            ScanOutside(d.Record.Span[..d.StreamAt], 0, d, null, tail: false);
            ScanOutside(tail, d.StreamAt + d.StreamLength, d, tailKnown, tail: true);
        }
        if (!listRead && !failures.Any(f => f.DocumentId == GraphRecordId)) failures.Add(new GraphFailure(GraphRecordId, "GR_Graph", "the graph record isn't in the datafile"));
#pragma warning disable CS0618   // the old member is still set, for callers that read it; it also takes the copy the verdicts use
        return new ClipGraph
        {
            Documents = docs, Failures = failures, GraphListRead = listRead, ClipNodes = nodes, ClipNodesNamingOtherRecords = nodesOther,
            ClipNodesUnread = nodesUnread, MotionFields = fields, GraphClipListEntries = listEntries, UsesByClip = uses, ScannedClips = clips,
        };
#pragma warning restore CS0618

        // the order rule: the nearest object at or before j that is neither an ID object, part of a test nor a clip node, else -1
        static int Owner(GraphDocument d, int j)
        {
            while (j >= 0 && (GraphClasses.IsIdOrTest(d.Objects[j].Class) || d.Objects[j].Class == GraphClasses.ClipNode)) j--;
            return j;
        }

        void ScanOutside(ReadOnlySpan<byte> s, int baseAt, GraphDocument d, HashSet<int>? skip, bool tail)
        {
            for (int p = 0; p + 8 <= s.Length; p++)
            {
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);
                if (!highs.Contains(v >> 40) || !clips.Contains(v) || (skip?.Contains(p) ?? false)) continue;
                Add(new ClipGraphUse(v, GraphUseKind.Unread, 0, d.Id, d.Name, baseAt + p, null, HoldsDuration: true, MotionField: false) { InRecordTail = tail, BeforeStream = !tail });
            }
        }
    }

    /// <summary>The numbers over the datafile's clips.</summary>
    public ClipGraphCensus Census(IEnumerable<ulong> clipIds)
    {
        var c = new ClipGraphCensus { Documents = Documents.Count, Failures = Failures.Count, ClipNodes = ClipNodes, ClipNodesNamingOtherRecords = ClipNodesNamingOtherRecords,
            ClipNodesUnread = ClipNodesUnread, MotionFields = MotionFields.Count, MotionFieldDocuments = MotionFields.Select(m => m.Document.Id).Distinct().Count(),
            MotionFieldClipEntries = MotionFields.Sum(m => m.Field.Clips.Count), MotionFieldSegments = MotionFields.Sum(m => m.Field.Segments.Count),
            GraphClipListEntries = GraphClipListEntries };
        foreach (var id in clipIds)
        {
            c.Clips++;
            var v = Verdict(id);
            if (v.HasNodeUse) c.ClipsWithNodeUse++; else c.ClipsWithNoNodeUse++;
            if (v.Uses.Any(u => u.Kind == GraphUseKind.GraphClipList)) c.ClipsOnGraphList++;
            if (v.Uses.Any(u => u.HoldsDuration)) c.ClipsWithLengthHeld++;
            if (v.IsMotionField) c.MotionFieldClips++;
            if (!v.MotionFieldKnown) c.ClipsMotionFieldUnknown++;
            if (v.Uses.Any(u => u.Kind == GraphUseKind.Unread)) c.ClipsWithUnreadUse++;
            if (v.CanChangeLength) c.ClipsLengthFree++;
            if (v.ListedWithoutNodeUse) c.ClipsListedWithoutNodeUse++;
            if (v.UnderBlend)
            {
                c.ClipsUnderBlend++;
                if (v.UnderBlendIndirectly) c.ClipsUnderBlendIndirect++;
                if (v.CanChangeLength) c.ClipsUnderBlendLengthFree++;
                if (v.CanChangeMotion) c.ClipsUnderBlendMotionFree++;
            }
        }
        foreach (var u in ClipUses.Values.SelectMany(l => l))
        {
            c.UsesByKind[u.Kind] = c.UsesByKind.GetValueOrDefault(u.Kind) + 1;
            if (u.Kind == GraphUseKind.Unread)
            {
                var k = (u.Class, u.OwnerClass, u.InRecordTail || u.BeforeStream);
                c.UnreadPlaces[k] = c.UnreadPlaces.GetValueOrDefault(k) + 1;
            }
            if (u.Kind != GraphUseKind.ClipNode) continue;
            if (u.ParentClass == 0) c.ClipNodesWithNoParent++;
            else if (!GraphClasses.IsParsed(u.ParentClass))
            {
                c.ClipNodesUnderUnreadParent++;
                if (Verdict(u.ClipId).CanChangeLength) c.ClipNodesUnderUnreadParentAllowed++;
            }
        }
        return c;
    }

    /// <summary>The census of <see cref="Census(IEnumerable{ulong})"/>, and over the clips' durations the check behind the rule that a
    /// clip node's parent holds no form of its length: for each clip node naming a clip allowed trim and speed, whether its parent object's
    /// bytes hold the clip's own duration as an f32 (within 0.5 ms), against a control clip's: the first clip from half-way round
    /// <paramref name="clips"/> whose duration differs by 0.02 s or more. A clip of no duration (0) is left out.</summary>
    public ClipGraphCensus Census(IEnumerable<(ulong Id, float Duration)> clips)
    {
        var list = clips.ToList();
        var c = Census(list.Select(x => x.Id));
        var docs = new Dictionary<ulong, GraphDocument>();
        foreach (var d in Documents) docs.TryAdd(d.Id, d);
        int n = list.Count;
        for (int k = 0; k < n; k++)
        {
            var (id, own) = list[k];
            if (own <= 0 || !Verdict(id).CanChangeLength) continue;
            float control = 0;
            for (int q = 0; q < n && control == 0; q++)
                if (list[(k + n / 2 + q) % n].Duration is var x && x > 0 && Math.Abs(x - own) >= 0.02f) control = x;
            foreach (var u in Verdict(id).Uses)
            {
                if (u.Kind != GraphUseKind.ClipNode || u.ParentAt < 0 || !docs.TryGetValue(u.DocumentId, out var d)) continue;
                int from = u.ParentAt + 4, to = d.ObjectEnd(d.ObjectAtOrBefore(u.ParentAt));
                c.ParentObjectsChecked++;
                if (HoldsF32(d.Bytes, from, to, own)) c.ParentsHoldingOwnDuration++;
                if (control > 0 && HoldsF32(d.Bytes, from, to, control)) c.ParentsHoldingControlDuration++;
            }
        }
        return c;

        static bool HoldsF32(byte[] b, int from, int to, float value)
        {
            for (int p = Math.Max(0, from); p + 4 <= Math.Min(to, b.Length); p++)
                if (Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p)) - value) < 0.0005f) return true;
            return false;
        }
    }
}

/// <summary>The graph reader's numbers on one install.</summary>
public sealed class ClipGraphCensus
{
    public int Documents, Failures, ClipNodes, ClipNodesNamingOtherRecords, ClipNodesUnread, MotionFields, MotionFieldDocuments, MotionFieldClipEntries, MotionFieldSegments, GraphClipListEntries;
    public int Clips, ClipsWithNodeUse, ClipsWithNoNodeUse, ClipsOnGraphList, ClipsWithLengthHeld, MotionFieldClips, ClipsWithUnreadUse, ClipsLengthFree;
    /// <summary>Clips whose motion-field answer isn't known (the graph didn't read whole); <see cref="MotionFieldClips"/> is then what was found.</summary>
    public int ClipsMotionFieldUnknown;
    /// <summary>Clip nodes naming a clip of the datafile whose parent (<see cref="ClipGraphUse.ParentClass"/>) is of a class no parser here
    /// reads, and of them those naming a clip allowed trim and speed: the reach of the rule that a parent reaching a clip node by a link
    /// isn't taken as holding its length. A clip node with no parent before it is counted in <see cref="ClipNodesWithNoParent"/> instead.</summary>
    public int ClipNodesUnderUnreadParent, ClipNodesUnderUnreadParentAllowed;
    /// <summary>Clip nodes naming a clip of the datafile with no object before them that could be their parent.</summary>
    public int ClipNodesWithNoParent;
    /// <summary>Clips GR_Graph's list names with no other use (<see cref="ClipGraphVerdict.ListedWithoutNodeUse"/>): every edit refused.</summary>
    public int ClipsListedWithoutNodeUse;
    /// <summary>Clips with a clip node under a blend's child (<see cref="ClipGraphVerdict.UnderBlend"/>), and of them those allowed trim and
    /// speed, and mirror and root-motion scale (both with the blend's warning).</summary>
    public int ClipsUnderBlend, ClipsUnderBlendLengthFree, ClipsUnderBlendMotionFree;
    /// <summary>Of <see cref="ClipsUnderBlend"/>, the clips whose blend's child stands only above a clip node's parent (one to
    /// <see cref="ClipGraph.BlendLevels"/> - 1 levels above it: <see cref="ClipGraphUse.BlendLevel"/> 2 to <see cref="ClipGraph.BlendLevels"/>,
    /// the parent being 1), none being a parent itself.</summary>
    public int ClipsUnderBlendIndirect;
    /// <summary>Only from <see cref="ClipGraph.Census(IEnumerable{ValueTuple{ulong, float}})"/>: the parent objects of clip nodes naming a
    /// clip allowed trim and speed, and how many hold the clip's own duration as an f32, and a control clip's.</summary>
    public int ParentObjectsChecked, ParentsHoldingOwnDuration, ParentsHoldingControlDuration;
    public Dictionary<GraphUseKind, int> UsesByKind { get; } = [];
    /// <summary>Unread places by the object's class, its owner's class, and whether they stand in a record's bytes outside its stream
    /// (before or after it).</summary>
    public Dictionary<(uint Class, uint Owner, bool Tail), int> UnreadPlaces { get; } = [];

    public IEnumerable<string> Lines()
    {
        yield return $"graph documents read {Documents}, failures {Failures}";
        yield return $"clip nodes {ClipNodes} ({ClipNodesNamingOtherRecords} naming records outside the datafile's clips, {ClipNodesUnread} unread)";
        yield return $"motion fields {MotionFields} in {MotionFieldDocuments} documents: clip list entries {MotionFieldClipEntries}, segments {MotionFieldSegments}";
        yield return $"GR_Graph's clip list {GraphClipListEntries} entries";
        yield return $"clips {Clips}: with a graph node use {ClipsWithNodeUse}, none {ClipsWithNoNodeUse}; on GR_Graph's list {ClipsOnGraphList}";
        yield return $"   length held by a graph place {ClipsWithLengthHeld} (trim and speed allowed on {ClipsLengthFree}); motion-field clips {MotionFieldClips}"
            + (ClipsMotionFieldUnknown > 0 ? $" (unknown for {ClipsMotionFieldUnknown}: the graph couldn't be read whole)" : "") + $"; with an unread place {ClipsWithUnreadUse}";
        if (ClipsListedWithoutNodeUse > 0)
            yield return $"   on GR_Graph's list with no node use {ClipsListedWithoutNodeUse}: every edit refused, as a node the read misses names them";
        yield return $"clip nodes under a parent of an unread class {ClipNodesUnderUnreadParent} ({ClipNodesUnderUnreadParentAllowed} naming a clip allowed trim and speed), "
            + $"with no parent {ClipNodesWithNoParent}; such parents aren't taken as holding the clip's length";
        if (ParentObjectsChecked > 0)
            yield return $"   of the {ParentObjectsChecked} parent objects of clip nodes allowed trim and speed, {ParentsHoldingOwnDuration} hold the clip's own duration "
                + $"as an f32, {ParentsHoldingControlDuration} a control clip's";
        yield return $"clips under a blend's child {ClipsUnderBlend} ({ClipsUnderBlendIndirect} with the blend's child 1 to {ClipGraph.BlendLevels - 1} levels above the clip node's parent; "
            + $"trim and speed allowed on {ClipsUnderBlendLengthFree}, mirror and root-motion scale on {ClipsUnderBlendMotionFree}); speed, root-motion scale, mirror, "
            + "import and swaps carry the blend's warning";
        yield return "uses by kind: " + string.Join(", ", UsesByKind.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
        foreach (var (k, n) in UnreadPlaces.OrderByDescending(x => x.Value))
            yield return $"   unread: {n} in {(k.Tail ? "record bytes outside the stream" : k.Class == 0 ? "document heads" : $"objects 0x{k.Class:X8} after 0x{k.Owner:X8}")}";
    }
}
