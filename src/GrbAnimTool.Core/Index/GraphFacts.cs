using System.Buffers.Binary;
using System.Globalization;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Graph;

namespace GrbAnimTool.Core.Index;

/// <summary>One graph document as the index keeps it: its record ID; its name without the GUID the game appends to it; its children, the
/// documents its record names in its tail (the machines and layers it holds); its references, the other documents whose GUID stands in its
/// own decoded stream (a machine it plays by reference, such as a shared reference machine); and how many of the datafile's clips it names.
/// Children and references are places in <see cref="GraphFacts.Documents"/>.</summary>
public sealed record IndexDocument(ulong Id, string Name, IReadOnlyList<int> Children, IReadOnlyList<int> References, int Clips)
{
    /// <summary>A record name without the 32 hex digits of the GUID the game appends to a graph document's name
    /// ("Ground-StateMachine7b591e07..." gives "Ground-StateMachine"); a name without them is given back as it is.</summary>
    public static string ShortName(string recordName) =>
        GuidSuffix(recordName) is null ? recordName : recordName[..^32];

    /// <summary>The 32 hex digits at the end of a record name, lower case, or null when it doesn't end in them.</summary>
    internal static string? GuidSuffix(string recordName)
    {
        if (recordName.Length <= 32) return null;
        var tail = recordName.AsSpan(recordName.Length - 32);
        foreach (char c in tail) if (!char.IsAsciiHexDigit(c)) return null;
        return tail.ToString().ToLowerInvariant();
    }
}

/// <summary>What the graph says about one clip, for the index: the documents that name it (in any place the graph reader finds, GR_Graph's
/// own clip list left out, as it names every clip), and of them those where a motion field names it. Both by the documents' places in
/// <see cref="GraphFacts.Documents"/>, sorted.</summary>
public sealed record ClipGraphFacts(IReadOnlyList<int> Documents, IReadOnlyList<int> MotionFieldDocuments);

/// <summary>The facts the index takes from an install's animation graph, before any rule reads them: every graph document with its
/// children and references, and for each clip the documents that name it. This is the part that costs a read of the whole graph, so it is
/// what the cache keeps; the rules that turn it into categories run on it each time (see <see cref="GraphRules"/>).
/// <para>How documents name each other: a record's tail names its children by record ID (GR_Graph names its own by a handle holding the
/// GUID that ends the child's record name), and a document's decoded stream holds the GUID of each document it plays: its children again,
/// and the shared machines it plays by reference. In the game's graph every document but GR_Graph has exactly one parent by the tails, and
/// the shared machines (the reference machines under <c>AnimationComponent-ReferenceStateMachines</c>) are reached from the layers that use
/// them only by such references.</para></summary>
public sealed class GraphFacts
{
    /// <summary>The class of a document handle in a record's tail: a number, a zero u32, this class and a 16-byte GUID, which is the end
    /// of one graph document's record name. GR_Graph names some of its documents this way.</summary>
    public const uint DocumentHandleClass = 0xAF8D3FBC;

    /// <summary>The version of how <see cref="Read"/> reads the facts; the cache's fingerprint holds it, so facts read another way by an
    /// older copy of the tool are read again. Raise it with any change to what the read takes from the graph.</summary>
    public const int FormatVersion = 1;

    public required IReadOnlyList<IndexDocument> Documents { get; init; }
    /// <summary>The facts of each clip of the datafile that some document names; a clip no document names has no entry.</summary>
    public required IReadOnlyDictionary<ulong, ClipGraphFacts> Clips { get; init; }
    /// <summary>The documents that didn't read whole, each "name 0xID: why". With any, the graph's categories may be missing for some clips.</summary>
    public required IReadOnlyList<string> Failures { get; init; }
    /// <summary>How many clips the read looked for (the datafile's clips).</summary>
    public required int ClipsLookedFor { get; init; }

    public bool Complete => Failures.Count == 0;

    Dictionary<ulong, int>? _byId;

    /// <summary>A document's place in <see cref="Documents"/>, or -1.</summary>
    public int IndexOf(ulong documentId)
    {
        var d = _byId ??= Documents.Select((x, i) => (x.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);
        return d.GetValueOrDefault(documentId, -1);
    }

    /// <summary>Reads the graph documents among <paramref name="records"/> one by one, in parallel, for the clips of
    /// <paramref name="clipIds"/>: each document's uses of the clips (by the graph reader, <see cref="ClipGraph"/>), its children and its
    /// references. <paramref name="progress"/> gets the share of the documents' bytes read, 0 to 1, rising. A document that doesn't read
    /// is listed in <see cref="Failures"/> and names no clip (its children are still taken from its tail when its stream can be found).</summary>
    public static GraphFacts Read(IReadOnlyList<DatafileRecord> records, IEnumerable<ulong> clipIds, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        var clips = clipIds.ToHashSet();
        var docs = records.Where(r => GraphDocument.IsDocumentType(r.Type)).ToList();
        var names = new DocumentNames(docs);
        var results = new (List<int> Children, List<int> References, Dictionary<ulong, bool>? Uses, string? Failure)[docs.Count];
        long total = Math.Max(1, docs.Sum(r => (long)r.Bytes.Length)), done = 0;
        double reported = 0;
        var sync = new object();
        progress?.Report(0);
        Parallel.For(0, docs.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
        {
            var r = docs[i];
            // a document that fails in any way is that document's failure, never the whole read's
            try { results[i] = ReadOne(r, i, clips, names); }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { results[i] = ([], [], null, $"{ex.GetType().Name}: {ex.Message}"); }
            long now = Interlocked.Add(ref done, r.Bytes.Length);
            if (progress is null) return;
            lock (sync)
            {
                // reports from several threads arrive in any order: only a larger share is passed on
                double share = Math.Min(1.0, now / (double)total);
                if (share <= reported) return;
                reported = share;
                progress.Report(share);
            }
        });
        cancel.ThrowIfCancellationRequested();
        var byClip = new Dictionary<ulong, (SortedSet<int> Docs, SortedSet<int> Fields)>();
        var list = new List<IndexDocument>(docs.Count);
        var failures = new List<string>();
        for (int i = 0; i < docs.Count; i++)
        {
            var (children, references, uses, failure) = results[i];
            if (failure is not null) failures.Add($"{IndexDocument.ShortName(docs[i].Name)} 0x{docs[i].Id:X16}: {failure}");
            foreach (var (clip, field) in uses ?? [])
            {
                if (!byClip.TryGetValue(clip, out var e)) byClip[clip] = e = ([], []);
                e.Docs.Add(i);
                if (field) e.Fields.Add(i);
            }
            list.Add(new IndexDocument(docs[i].Id, IndexDocument.ShortName(docs[i].Name), children, references, uses?.Count ?? 0));
        }
        return new GraphFacts
        {
            Documents = list, Failures = failures, ClipsLookedFor = clips.Count,
            Clips = byClip.ToDictionary(k => k.Key, k => new ClipGraphFacts(k.Value.Docs.ToArray(), k.Value.Fields.ToArray())),
        };
    }

    /// <summary>The graph documents by record ID and by the GUID that ends their record name, the GUID as the stream holds it (.NET's byte
    /// order, the 16 bytes split into two u64s for the scan).</summary>
    sealed class DocumentNames
    {
        public readonly Dictionary<ulong, int> ById = [];
        public readonly Dictionary<string, int> ByGuidText = new(StringComparer.Ordinal);
        public readonly Dictionary<ulong, List<(ulong High, int Doc)>> ByGuidLow = [];

        public DocumentNames(List<DatafileRecord> docs)
        {
            for (int i = 0; i < docs.Count; i++)
            {
                ById.TryAdd(docs[i].Id, i);
                if (IndexDocument.GuidSuffix(docs[i].Name) is not { } g || !ByGuidText.TryAdd(g, i)) continue;
                var b = Guid.ParseExact(g, "N").ToByteArray();
                ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(b), hi = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8));
                if (!ByGuidLow.TryGetValue(lo, out var l)) ByGuidLow[lo] = l = [];
                l.Add((hi, i));
            }
        }
    }

    /// <summary>One document: the clips it names (with whether a motion field of it names each), its children and its references.</summary>
    static (List<int>, List<int>, Dictionary<ulong, bool>?, string?) ReadOne(DatafileRecord r, int self, HashSet<ulong> clips, DocumentNames names)
    {
        var rec = r.Bytes.Span;
        int at = GraphDocument.FindStream(rec, out int len);
        List<int> children = at < 0 ? [] : Children(rec[(at + len)..], self, names);
        ClipGraph g;
        try { g = ClipGraph.Read([r], clips); }
        catch (InvalidDataException ex) { return (children, [], null, ex.Message); }
        // the read of one document alone notes GR_Graph as missing unless it is GR_Graph: only this document's own failures count
        var own = g.Failures.Where(f => f.DocumentId == r.Id).Select(f => f.Error).ToList();
        string? failure = own.Count > 0 ? string.Join("; ", own) : null;
        if (g.Documents.Count == 0) return (children, [], null, failure ?? "it doesn't read as a graph document");
        var references = References(g.Documents[0].Bytes, self, names).Where(d => !children.Contains(d)).ToList();
        var uses = new Dictionary<ulong, bool>();
        foreach (var (clip, l) in g.ClipUses)
        {
            // GR_Graph's own clip list names every clip of the graph, so it says nothing of what plays one
            var named = l.Where(u => u.Kind != GraphUseKind.GraphClipList).ToList();
            if (named.Count > 0) uses[clip] = named.Any(u => u.MotionField);
        }
        return (children, references, uses, failure);
    }

    /// <summary>The documents a record's tail names: by record ID (any 8 bytes that are another graph document's ID), and by handle (the
    /// handle class followed by a GUID that ends a graph document's record name). Each once, in the order they stand.</summary>
    static List<int> Children(ReadOnlySpan<byte> tail, int self, DocumentNames names)
    {
        var found = new List<int>();
        for (int p = 0; p + 8 <= tail.Length; p++)
        {
            if (names.ById.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(tail[p..]), out int i) && i != self && !found.Contains(i)) found.Add(i);
            if (p + 20 <= tail.Length && BinaryPrimitives.ReadUInt32LittleEndian(tail[p..]) == DocumentHandleClass
                && names.ByGuidText.TryGetValue(new Guid(tail.Slice(p + 4, 16)).ToString("N", CultureInfo.InvariantCulture), out int h) && h != self && !found.Contains(h))
                found.Add(h);
        }
        return found;
    }

    /// <summary>The documents whose GUID stands anywhere in a decoded stream, each once, in the order they first stand.</summary>
    static List<int> References(byte[] b, int self, DocumentNames names)
    {
        var found = new List<int>();
        for (int p = 0; p + 16 <= b.Length; p++)
        {
            if (!names.ByGuidLow.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)), out var l)) continue;
            ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 8));
            foreach (var (high, doc) in l)
                if (high == hi && doc != self && !found.Contains(doc)) found.Add(doc);
        }
        return found;
    }
}
