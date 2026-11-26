using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Graph;
using static GrbAnimTool.Tests.GraphSynthetic;

namespace GrbAnimTool.Tests;

/// <summary>The graph reader on documents made here: the head and walker, the parsed node classes, the per-clip verdicts, and damaged
/// documents, which must be refused with a <see cref="GraphDocumentException"/> and make every verdict refuse.</summary>
public sealed class GraphUnitTests
{
    [Fact]
    public void ADocumentReadsToItsEnd()
    {
        var r = Record(1, GraphDocument.DocumentType, "Plain", ClipDocument(ClipA));
        var d = GraphDocument.Read(r);
        Assert.Equal(2, d.Objects.Count);
        Assert.Equal(1, d.TokenedObjects);   // the clip node; the ID object stands inline
        Assert.Equal(1, d.InlineObjects);
        var n = Assert.Single(GraphNodes.ClipNodes(d, out int unread));
        Assert.Equal(0, unread);
        Assert.Equal(ClipA, n.ClipId);
    }

    [Fact]
    public void TheVerdicts()
    {
        var g = ClipGraph.Read(Install(), Clips);
        Assert.True(g.Complete);
        Assert.Equal(4, g.Documents.Count);
        Assert.Equal(1, g.ClipNodes);
        var mf = Assert.Single(g.MotionFields).Field;
        Assert.Equal(new[] { OtherRecord, ClipB }, mf.Clips);
        Assert.Equal(50, mf.Rows);

        var a = g.Verdict(ClipA);   // a clip node only
        Assert.True(a.HasNodeUse);
        Assert.True(a.CanChangeLength);
        Assert.True(a.CanChangeMotion);
        Assert.False(a.WarnOnReplace);
        Assert.Contains(a.Uses, u => u.Kind == GraphUseKind.GraphClipList);

        var b = g.Verdict(ClipB);   // a motion field: its list by ID, its segment by list index 1
        Assert.True(b.IsMotionField);
        Assert.False(b.CanChangeLength);
        Assert.False(b.CanChangeMotion);
        Assert.True(b.WarnOnReplace);
        var seg = Assert.Single(b.Uses, u => u.Kind == GraphUseKind.MotionFieldSegment);
        Assert.Equal(1, seg.ListIndex);
        Assert.True(seg.HoldsDuration);
        Assert.Contains("segment", b.LengthRefusal);

        var c = g.Verdict(ClipC);   // an unread class: held to hold the length
        var u = Assert.Single(c.Uses, x => x.Kind == GraphUseKind.Unread);
        Assert.Equal(0x12345678u, u.Class);
        Assert.False(c.CanChangeLength);
        Assert.True(c.CanChangeMotion);

        // GR_Graph's list only: a node the read misses names it, so nothing that depends on the graph is allowed
        var d = g.Verdict(ClipD);
        Assert.False(d.HasNodeUse);
        Assert.True(d.ListedWithoutNodeUse);
        Assert.False(d.CanChangeLength);
        Assert.False(d.CanChangeMotion);
        Assert.True(d.WarnOnReplace);
        Assert.Equal(ClipGraphVerdict.ListedWithoutNodeUseReason, d.LengthRefusal);
        Assert.Equal(ClipGraphVerdict.ListedWithoutNodeUseReason, d.MotionRefusal);
        Assert.Equal(ClipGraphVerdict.ListedWithoutNodeUseReason, d.ReplaceWarning);
        Assert.Contains("no graph node the tool reads", d.LengthRefusal);
        Assert.All(new[] { a, b, c }, v => Assert.False(v.ListedWithoutNodeUse));
        Assert.All(new[] { a, b, c, d }, v => Assert.False(v.UnderBlend));
        Assert.Null(a.MotionWarning);
        Assert.Null(a.MotionRefusal);
        Assert.Null(a.ReplaceWarning);

        var census = g.Census(Clips);
        Assert.Equal(3, census.ClipsWithNodeUse);
        Assert.Equal(2, census.ClipsWithLengthHeld);
        Assert.Equal(1, census.ClipsLengthFree);
        Assert.Equal(1, census.ClipsListedWithoutNodeUse);
        Assert.Equal(1, census.MotionFieldClips);
        Assert.Equal(0, census.ClipsMotionFieldUnknown);
        Assert.Equal(4, census.ClipsOnGraphList);
        Assert.Equal(0, census.ClipNodesUnderUnreadParent);   // clip A's node has no parent before it: counted apart
        Assert.Equal(0, census.ClipNodesUnderUnreadParentAllowed);
        Assert.Equal(1, census.ClipNodesWithNoParent);
        Assert.Equal(0, census.ClipsUnderBlend);
        Assert.Equal(0, census.ParentObjectsChecked);
    }

    /// <summary>A clip node under a blend's child: trim, speed, mirror and scale stay allowed, with the blend's warning; the census counts
    /// it, and finds the clip's own duration standing in the blend child's bytes where it was put.</summary>
    [Fact]
    public void AClipUnderABlendsChildIsAllowedWithAWarning()
    {
        var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Blend", BlendDocument(ClipA, 1.5f))], Clips);
        Assert.True(g.Complete);
        var a = g.Verdict(ClipA);
        var node = Assert.Single(a.Uses, u => u.Kind == GraphUseKind.ClipNode);
        Assert.Equal(GraphClasses.BlendChild, node.ParentClass);
        Assert.True(node.ParentAt > 0);
        Assert.True(a.UnderBlend);
        Assert.True(a.CanChangeLength);
        Assert.True(a.CanChangeMotion);
        Assert.True(a.WarnOnReplace);   // import and equal swaps warn on a blend's child (its ReplaceWarning)
        Assert.Equal(ClipGraphVerdict.BlendWarning, a.MotionWarning);
        Assert.Contains("child of a blend", a.MotionWarning);

        var census = g.Census([(ClipA, 1.5f), (ClipB, 0.8f), (ClipC, 2.0f), (ClipD, 3.0f)]);
        Assert.Equal(1, census.ClipsUnderBlend);
        Assert.Equal(1, census.ClipsUnderBlendLengthFree);
        Assert.Equal(1, census.ClipsUnderBlendMotionFree);
        Assert.Equal(1, census.ClipNodesUnderUnreadParent);
        Assert.Equal(1, census.ParentObjectsChecked);
        Assert.Equal(1, census.ParentsHoldingOwnDuration);       // the nominal put there
        Assert.Equal(0, census.ParentsHoldingControlDuration);   // clip C's 2.0 s, half-way round
        Assert.Equal(3, census.ClipsListedWithoutNodeUse);       // B, C and D: on the list only
        Assert.Contains(census.Lines(), l => l.StartsWith("clips under a blend's child 1"));
        Assert.Equal(0, census.ClipsUnderBlendIndirect);
        // import and equal swaps carry the blend's own replace warning
        Assert.Equal(1, node.BlendLevel);
        Assert.True(a.UnderBlendDirectly);
        Assert.False(a.UnderBlendIndirectly);
        Assert.Equal(ClipGraphVerdict.ReplaceBlendWarning, a.ReplaceWarning);
    }

    /// <summary>A clip node under a pick under a blend's child: the walk up by the order rule finds the blend one or two levels above the
    /// parent; three levels above it is past the walk.</summary>
    [Fact]
    public void AClipUnderAPickUnderABlendsChildIsUnderTheBlend()
    {
        for (int between = 1; between <= 3; between++)
        {
            var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Blend", BlendGrandchildDocument(ClipA, between))], Clips);
            Assert.True(g.Complete);
            var a = g.Verdict(ClipA);
            var node = Assert.Single(a.Uses, u => u.Kind == GraphUseKind.ClipNode);
            Assert.Equal(PickClass, node.ParentClass);
            bool seen = between + 1 <= ClipGraph.BlendLevels;
            Assert.Equal(seen ? between + 1 : 0, node.BlendLevel);
            Assert.Equal(seen, a.UnderBlend);
            Assert.Equal(seen, a.UnderBlendIndirectly);
            Assert.False(a.UnderBlendDirectly);
            Assert.Equal(seen ? ClipGraphVerdict.BlendWarning : null, a.MotionWarning);
            Assert.Equal(seen ? ClipGraphVerdict.ReplaceBlendWarning : null, a.ReplaceWarning);
            Assert.True(a.CanChangeMotion);
            var census = g.Census(Clips);
            Assert.Equal(seen ? 1 : 0, census.ClipsUnderBlend);
            Assert.Equal(seen ? 1 : 0, census.ClipsUnderBlendIndirect);
        }
    }

    /// <summary>The order rule's limit, pinned: objects written depth first, a pick holding a blend's child (with its own clip node) and then
    /// a second pick holding clip A's node. The second pick's real parent is the first pick, but the walk up takes the earlier sibling's
    /// subtree for its owner, so clip A counts as under the blend one level above its parent: an extra warning, never a refusal.</summary>
    [Fact]
    public void AnEarlierSiblingsSubtreeStandsInForAnAncestor()
    {
        var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Siblings", SiblingSubtreeDocument(ClipA, ClipB))], Clips);
        Assert.True(g.Complete);
        var b = Assert.Single(g.Verdict(ClipB).Uses, u => u.Kind == GraphUseKind.ClipNode);
        Assert.Equal(1, b.BlendLevel);
        var a = g.Verdict(ClipA);
        var node = Assert.Single(a.Uses, u => u.Kind == GraphUseKind.ClipNode);
        Assert.Equal(PickClass, node.ParentClass);
        Assert.Equal(2, node.BlendLevel);
        Assert.True(a.UnderBlendIndirectly);
        Assert.Equal(ClipGraphVerdict.BlendWarning, a.MotionWarning);
        Assert.True(a.CanChangeMotion);
        Assert.True(a.CanChangeLength);
    }

    /// <summary>A clip ID in a document's head (its class table), and one in an ID object with no object before it: both unread and held;
    /// the first has no object, the second's owner walk runs out and names none.</summary>
    [Fact]
    public void AClipIdInTheHeadOrBeforeAnyOwnerIsUnreadAndHeld()
    {
        var head = ClipDocument(ClipA);
        BitConverter.TryWriteBytes(head.AsSpan(12, 8), ClipC);   // the first class row's 16-byte word
        var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Head", head), Record(6, GraphDocument.DocumentType, "Lone", LoneIdObjectDocument(ClipB))], Clips);
        Assert.True(g.Complete);
        var c = g.Verdict(ClipC);
        var u = Assert.Single(c.Uses, x => x.Kind != GraphUseKind.GraphClipList);
        Assert.Equal(GraphUseKind.Unread, u.Kind);
        Assert.True(u.InHead);
        Assert.Equal(12, u.At);
        Assert.Contains("document head", u.Describe());
        Assert.False(c.CanChangeLength);
        Assert.True(g.Verdict(ClipA).CanChangeLength);

        var b = g.Verdict(ClipB);
        var lone = Assert.Single(b.Uses, x => x.Kind == GraphUseKind.Unread);
        Assert.Equal(GraphClasses.IdObject, lone.Class);
        Assert.Equal(0u, lone.OwnerClass);
        Assert.False(lone.InHead);
        Assert.False(b.CanChangeLength);
        // an unread object that is itself the first object owns its own place
        Assert.Equal(0x12345678u, Assert.Single(ClipGraph.Read(Install(), Clips).Verdict(ClipC).Uses, x => x.Kind == GraphUseKind.Unread).OwnerClass);
    }

    /// <summary>A verdict made anywhere but <see cref="ClipGraph.Verdict"/> that doesn't set <see cref="ClipGraphVerdict.Scanned"/> refuses.</summary>
    [Fact]
    public void AVerdictIsUnscannedByDefault()
    {
        var v = new ClipGraphVerdict { ClipId = ClipA, Uses = [], GraphComplete = true };
        Assert.False(v.Scanned);
        Assert.False(v.CanChangeLength);
        Assert.False(v.CanChangeMotion);
        Assert.True(v.WarnOnReplace);
        Assert.Contains("isn't known", v.ReplaceWarning);
    }

    /// <summary>The uses <see cref="ClipGraph.ClipUses"/> and the verdicts hold are a copy: a change to the lists the read built doesn't
    /// reach them, and the verdict's list can't be changed.</summary>
    [Fact]
    public void TheUsesCantBeChangedFromOutside()
    {
        var g = ClipGraph.Read(Install(), Clips);
        int n = g.Verdict(ClipA).Uses.Count;
#pragma warning disable CS0618   // the old member, kept for callers
        g.UsesByClip[ClipA].Clear();
#pragma warning restore CS0618
        Assert.Equal(n, g.ClipUses[ClipA].Count);
        Assert.Equal(n, g.Verdict(ClipA).Uses.Count);
        Assert.IsNotType<ClipGraphUse[]>(g.Verdict(ClipA).Uses);
        Assert.Throws<NotSupportedException>(() => ((IList<ClipGraphUse>)g.ClipUses[ClipA]).Clear());
    }

    static void AllRefuse(ClipGraph g)
    {
        Assert.False(g.Complete);
        foreach (var id in Clips)
        {
            var v = g.Verdict(id);
            Assert.False(v.CanChangeLength);
            Assert.False(v.CanChangeMotion);
            Assert.True(v.WarnOnReplace);
            Assert.False(v.MotionFieldKnown);
            Assert.NotNull(v.LengthRefusal);
        }
        Assert.Equal(Clips.Length, g.Census(Clips).ClipsMotionFieldUnknown);
    }

    /// <summary>An ID the read didn't look for: the record the motion field names beside clip B, a clip left out of the read, and an ID
    /// named nowhere. Each refuses every edit, though the graph read whole.</summary>
    [Fact]
    public void AnIdTheReadDidntLookForRefusesEveryEdit()
    {
        var g = ClipGraph.Read(Install(), [ClipA, ClipB, ClipC]);
        Assert.True(g.Complete);
        foreach (var id in new[] { OtherRecord, ClipD, 0x0000010000000E05ul })
        {
            var v = g.Verdict(id);
            Assert.False(v.Scanned);
            Assert.False(v.CanChangeLength);
            Assert.False(v.CanChangeMotion);
            Assert.True(v.WarnOnReplace);
            Assert.False(v.MotionFieldKnown);
            Assert.Equal("this clip wasn't part of the graph read", v.LengthRefusal);
        }
        Assert.True(g.Verdict(ClipA).CanChangeLength);
    }

    /// <summary>A clip node whose bytes aren't the game's form (a longer field mask, another mask byte, an ID object of another length)
    /// isn't read as a clip node: its clip's ID is an unread place, which holds the length.</summary>
    [Fact]
    public void AClipNodeOfAnotherFormIsUnreadAndHoldsTheLength()
    {
        Assert.True(ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Plain", ClipDocument(ClipA, [0x0E, 0x7F, 0xFC, 0x03, 0x00, 0x01, 0x00]))], Clips)
            .Verdict(ClipA).CanChangeLength);   // the game's other form
        foreach (var doc in new[]
        {
            ClipDocument(ClipA, [0x0E, 0x7F, 0xFC, 0x02, 0x00, 0x01, 0x00, 0x05]),   // one more byte
            ClipDocument(ClipA, [0x0E, 0x7F, 0xFC, 0x06, 0x00, 0x01, 0x00]),         // another field mask
            ClipDocument(ClipA, idExtra: 1),                                          // an ID object of 47 B
        })
        {
            var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Odd", doc)], Clips);
            Assert.True(g.Complete);
            Assert.Equal(1, g.ClipNodesUnread);
            Assert.Equal(0, g.ClipNodes);
            var v = g.Verdict(ClipA);
            Assert.Single(v.Uses, u => u.Kind == GraphUseKind.Unread && u.HoldsDuration);
            Assert.False(v.CanChangeLength);
            Assert.Contains("unread", v.LengthRefusal);
        }
    }

    [Fact]
    public void AMotionFieldEntryNoSegmentNamesHoldsTheLength()
    {
        var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Moving", MotionFieldDocument([ClipA, ClipB], [0.8f, 1.2f], segmented: [1]))], Clips);
        Assert.True(g.Complete);
        var a = g.Verdict(ClipA);
        var entry = Assert.Single(a.Uses, u => u.Kind == GraphUseKind.MotionFieldClip);
        Assert.True(entry.HoldsDuration);
        Assert.DoesNotContain(a.Uses, u => u.Kind == GraphUseKind.MotionFieldSegment);
        Assert.False(a.CanChangeLength);
        Assert.True(a.IsMotionField);
        Assert.False(a.CanChangeMotion);
    }

    /// <summary>A clip ID inside a motion field's span but in an object of another class: unread, held, and a motion field's.</summary>
    [Fact]
    public void AnUnreadPlaceInAMotionFieldsSpanIsAMotionFields()
    {
        var doc = MotionFieldDocument([ClipA, ClipB], [0.8f, 1.2f], between: [0x05, 0x0A, .. Id(ClipC), 0, 0]);
        var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Moving", doc)], Clips);
        Assert.True(g.Complete);
        var c = g.Verdict(ClipC);
        var u = Assert.Single(c.Uses, x => x.Kind == GraphUseKind.Unread);
        Assert.Equal(InlineClass, u.Class);
        Assert.True(u.MotionField);
        Assert.True(c.IsMotionField);
        Assert.False(c.CanChangeMotion);
        Assert.False(c.CanChangeLength);
    }

    /// <summary>A clip ID in a record's bytes before its stream, or after it (its tail): unread, held, and described as such.</summary>
    [Fact]
    public void AClipIdOutsideTheStreamHoldsTheLength()
    {
        var doc = ClipDocument(ClipA);
        var before = Record(5, GraphDocument.DocumentType, "Before", doc, before: [0, 0, 0, 0, .. Id(ClipD)]);
        var after = Record(5, GraphDocument.DocumentType, "After", doc, tail: DocumentTail(doc, Id(ClipD)));
        foreach (var (rec, tail) in new[] { (before, false), (after, true) })
        {
            var g = ClipGraph.Read([Graph(Clips), rec], Clips);
            Assert.True(g.Complete);
            var v = g.Verdict(ClipD);
            var u = Assert.Single(v.Uses, x => x.Kind == GraphUseKind.Unread);
            Assert.Equal(tail, u.InRecordTail);
            Assert.Equal(!tail, u.BeforeStream);
            Assert.Contains(tail ? "record tail" : "before the stream", u.Describe());
            Assert.False(v.CanChangeLength);
            Assert.True(g.Verdict(ClipA).CanChangeLength);
        }
    }

    /// <summary>On a graph that didn't read whole, a motion-field clip whose document failed reads "not found" but isn't known: every check
    /// on the motion field refuses or warns.</summary>
    [Fact]
    public void TheMotionFieldAnswerIsUnknownOnAnIncompleteGraph()
    {
        var recs = Install();
        var bad = recs[2].Bytes.ToArray();
        bad[^1] ^= 0x01;   // the motion field's document: its stated decoded length
        recs[2] = new DatafileRecord { Id = recs[2].Id, Type = recs[2].Type, Name = recs[2].Name, Bytes = bad };
        var v = ClipGraph.Read(recs, Clips).Verdict(ClipB);
        Assert.False(v.IsMotionField);
        Assert.False(v.MotionFieldKnown);
        Assert.False(v.CanChangeMotion);
        Assert.True(v.WarnOnReplace);
        Assert.False(v.CanChangeLength);
    }

    [Fact]
    public void ASegmentShorterThanItsObjectIsRefused()
    {
        var doc = MotionFieldDocument([ClipA, ClipB], [0.8f, 1.2f]).ToList();
        var seg = GraphSynthetic.Obj(GraphClasses.MotionFieldSegment);
        int at = Enumerable.Range(0, doc.Count - seg.Length).First(i => doc.Skip(i).Take(seg.Length).SequenceEqual(seg)) + 2;
        doc.RemoveAt(at + 28);   // the first segment's flag: 28 B, the next object right after
        var g = ClipGraph.Read([Graph(Clips), Record(5, GraphDocument.DocumentType, "Short", [.. doc])], Clips);
        Assert.Contains("shorter than 29 B", Assert.Single(g.Failures).Error);
        Assert.False(g.Verdict(ClipA).CanChangeLength);
    }

    [Fact]
    public void AVarintWiderThan32BitsIsRefused()
    {
        int p = 0;
        Assert.Equal(int.MaxValue, GraphDocument.Varint([0xFF, 0xFF, 0xFF, 0xFF, 0x07], ref p, "a test"));
        foreach (byte last in new byte[] { 0x10, 0x20, 0x40, 0x08 })
        {
            p = 0;
            Assert.Throws<InvalidDataException>(() => GraphDocument.Varint([0x80, 0x80, 0x80, 0x80, last], ref p, "a test"));
        }
    }

    /// <summary>GR_Graph's form: the first object token is of another class than the handle list, whose table count is 0; its objects
    /// stand before the body, and the body reads.</summary>
    [Fact]
    public void TheFirstObjectTokenNeedNotBeTheHandleList()
    {
        const uint First = 0x59E76516;
        var doc = Document([(First, 1), (GraphDocument.HandleListClass, 0), (GraphClasses.ClipNode, 1), (GraphClasses.IdObject, 0)], [(1, 0), (1, 2)], ClipNodeBytes(ClipA));
        var d = GraphDocument.Read(Record(5, GraphDocument.DocumentType, "First", doc));
        Assert.Equal(2, d.Objects.Count);
        Assert.Equal(ClipA, Assert.Single(GraphNodes.ClipNodes(d, out _)).ClipId);
        // a place of the first token's class in the body, beyond its table count less the token's N: the token's objects would then be read
        // as inline, against the rule that they stand before the body, so the document is refused
        doc = Document([(First, 1), (GraphClasses.ClipNode, 1), (GraphClasses.IdObject, 0)], [(1, 0), (1, 1)], [.. GraphSynthetic.Obj(First), 0, 0, .. ClipNodeBytes(ClipA)]);
        var ex = Assert.Throws<GraphDocumentException>(() => GraphDocument.Read(Record(5, GraphDocument.DocumentType, "First", doc)));
        Assert.Contains("the first object token's class 0x59E76516 stands 1 times in the body", ex.Message);
        // places of that class the table counts beyond the first token stand in the body as any other class's
        doc = Document([(First, 2), (GraphClasses.ClipNode, 1), (GraphClasses.IdObject, 0)], [(1, 0), (1, 0), (1, 1)], [.. GraphSynthetic.Obj(First), 0, 0, .. ClipNodeBytes(ClipA)]);
        d = GraphDocument.Read(Record(5, GraphDocument.DocumentType, "First", doc));
        Assert.Equal(3, d.Objects.Count);
        Assert.True(d.Objects[0].Tokened);
    }

    [Fact]
    public void AMissingGraphRecordMakesEveryVerdictRefuse()
    {
        var recs = Install().Skip(1).ToList();
        var g = ClipGraph.Read(recs, Clips);
        Assert.Contains(g.Failures, f => f.DocumentId == ClipGraph.GraphRecordId);
        AllRefuse(g);
    }

    [Fact]
    public void ADamagedDocumentMakesEveryVerdictRefuse()
    {
        var recs = Install();
        var bad = recs[2].Bytes.ToArray();
        bad[^1] ^= 0x01;   // the stated decoded length
        recs[2] = new DatafileRecord { Id = recs[2].Id, Type = recs[2].Type, Name = recs[2].Name, Bytes = bad };
        var g = ClipGraph.Read(recs, Clips);
        var f = Assert.Single(g.Failures);
        Assert.Equal(recs[2].Id, f.DocumentId);
        AllRefuse(g);
    }

    [Fact]
    public void AMotionFieldThatDoesntReadMakesEveryVerdictRefuse()
    {
        var recs = Install();
        // a segment naming list index 5 of a list of 2
        var doc = MotionFieldDocument([OtherRecord, ClipB], [0.8f, 1.2f]);
        int at = doc.Length - 3;   // the last segment's u16 clip index
        doc[at] = 5;
        recs[2] = Record(recs[2].Id, GraphDocument.DocumentType, recs[2].Name, doc);
        var g = ClipGraph.Read(recs, Clips);
        Assert.Contains("names clip 5", Assert.Single(g.Failures).Error);
        AllRefuse(g);
    }

    /// <summary>Every byte of a document changed in turn, and every cut of its record: each reads or is refused with a
    /// <see cref="GraphDocumentException"/>, never another exception.</summary>
    [Fact(Timeout = 60_000)]
    public async Task EveryByteChangedAndEveryCutIsRefusedCleanly()
    {
        var counts = new List<(int Refused, int Changes)>();
        await Task.Run(() =>
        {
            foreach (var doc in new[] { ClipDocument(ClipA), MotionFieldDocument([ClipB, ClipA], [1.2f, 0.8f]), UnreadDocument(ClipC) })
            {
                int refused = 0;
                for (int i = 0; i < doc.Length; i++)
                    foreach (byte x in new byte[] { 0x01, 0x80, 0xFF })
                    {
                        var d = (byte[])doc.Clone();
                        d[i] ^= x;
                        var r = Record(7, GraphDocument.DocumentType, "Changed", d);
                        try { GraphDocument.Read(r); }
                        catch (GraphDocumentException) { refused++; }
                        ClipGraph.Read([Graph(Clips), r], Clips);   // never throws
                    }
                counts.Add((refused, doc.Length * 3));
                var rec = Record(7, GraphDocument.DocumentType, "Cut", doc).Bytes;
                for (int n = 0; n < rec.Length; n++)
                {
                    var r = new DatafileRecord { Id = 7, Type = GraphDocument.DocumentType, Name = "Cut", Bytes = rec[..n] };
                    Assert.Throws<GraphDocumentException>(() => GraphDocument.Read(r));
                }
            }
        });
        // changes in the counts and tokens are refused; the head's unread words and some body bytes aren't checked. The floors are the
        // refusals the reader makes today, so a check that stops refusing shows
        int[] floors = [80, 200, 77];   // of 426, 720 and 249 changes
        Assert.True(counts.Select(x => x.Refused).Zip(floors).All(x => x.First >= x.Second), "refused: " + string.Join(", ", counts.Select(x => $"{x.Refused} of {x.Changes}")));
    }

    [Fact]
    public void TheHeadsCountsAreChecked()
    {
        // a class table that says 2 clip nodes where the tokens name 1
        var doc = Document([(GraphDocument.HandleListClass, 1), (GraphClasses.ClipNode, 2), (GraphClasses.IdObject, 0)], [(1, 0), (1, 1)], ClipNodeBytes(ClipA));
        var ex = Assert.Throws<GraphDocumentException>(() => GraphDocument.Read(Record(1, GraphDocument.DocumentType, "Counts", doc)));
        Assert.Contains("the table says 2", ex.Message);
        // a token for an object the body doesn't hold
        doc = Document([(GraphDocument.HandleListClass, 1), (GraphClasses.ClipNode, 2), (GraphClasses.IdObject, 0)], [(1, 0), (1, 1), (1, 1)], ClipNodeBytes(ClipA));
        ex = Assert.Throws<GraphDocumentException>(() => GraphDocument.Read(Record(1, GraphDocument.DocumentType, "Owed", doc)));
        Assert.Contains("unused", ex.Message);
        // a stream that runs past its record
        var rec = Record(1, GraphDocument.DocumentType, "Garbage", ClipDocument(ClipA)).Bytes.ToArray();
        rec[24] ^= 0x40;   // the first literal run's length
        ex = Assert.Throws<GraphDocumentException>(() => GraphDocument.Read(new DatafileRecord { Id = 1, Type = GraphDocument.DocumentType, Name = "Garbage", Bytes = rec }));
        Assert.Contains("LZO1X", ex.Message);
    }
}

/// <summary>The graph of the vanilla install. Every number here was read from the game's own documents: the totals agree with the
/// documents' own heads (the walker uses every object token, so every document reads to its end).</summary>
[Collection("install")]
public sealed class GraphTests(InstallFixture f)
{
    static ClipGraph? _graph;
    static readonly object ReadOnce = new();
    ClipGraph G { get { lock (ReadOnce) return _graph ??= ClipGraph.Read(f.Install); } }

    const ulong Hub = 0x000001544EA94BB9, ArmedSprint = 0x0000014AB299308A, ArmedSlopes = 0x0000014AB299308B, DeathMachine = 0x000001A2B8D2752A;

    [GameDataFact]
    public void EveryDocumentReadsToItsEnd()
    {
        var g = G;
        Assert.Empty(g.Failures);
        Assert.True(g.Complete);
        Assert.Equal(312, g.Documents.Count);
        Assert.Equal(311, g.Documents.Count(d => d.Type == GraphDocument.DocumentType));
        Assert.Single(g.Documents, d => d.Type == GraphDocument.GraphType && d.Id == ClipGraph.GraphRecordId);
        Assert.Equal(48_706_568, g.Documents.Sum(d => (long)d.Bytes.Length));
        Assert.Equal(20_617_510, g.Documents.Sum(d => (long)d.StreamLength));
        foreach (var d in g.Documents)
        {
            var objTokens = d.Tokens.Where(t => t.Kind == 0x40).ToList();
            Assert.Equal(objTokens.Sum(t => (long)t.N) - objTokens[0].N, d.TokenedObjects);   // all but the handle list's token
        }
        Assert.Equal(482_507, g.Documents.Sum(d => d.TokenedObjects));
        Assert.Equal(193_357, g.Documents.Sum(d => d.InlineObjects));
    }

    [GameDataFact]
    public void TheCensus()
    {
        var c = G.Census(f.Install.Clips.Select(x => (x.Id, x.Duration)));
        // the clip node class's bytes in the 312 bodies are exactly these 31,551 objects (241 more stand in the class tables), so no clip
        // node is missed; every one has the game's form (11 B, its ID object 46, 60 or 61 B)
        Assert.Equal(31_551, c.ClipNodes);
        Assert.Equal(723, c.ClipNodesNamingOtherRecords);
        Assert.Equal(0, c.ClipNodesUnread);
        Assert.Equal(107, c.MotionFields);
        Assert.Equal(24, c.MotionFieldDocuments);
        Assert.Equal(2_272, c.MotionFieldClipEntries);
        Assert.Equal(2_307, c.MotionFieldSegments);
        Assert.Equal(9_984, c.GraphClipListEntries);
        Assert.Equal(9_938, c.Clips);
        Assert.Equal(9_938, c.ClipsWithNodeUse);
        Assert.Equal(0, c.ClipsWithNoNodeUse);
        Assert.Equal(9_938, c.ClipsOnGraphList);
        Assert.Equal(1_098, c.ClipsWithLengthHeld);
        Assert.Equal(8_840, c.ClipsLengthFree);
        Assert.Equal(1_044, c.MotionFieldClips);
        Assert.Equal(55, c.ClipsWithUnreadUse);
        Assert.Equal(0, c.ClipsMotionFieldUnknown);
        // every read clip node's parent is of a class no parser here reads: the rule that such a parent holds no form of the length reaches
        // them all, and 28,504 of them name a clip allowed trim and speed
        Assert.Equal(30_828, c.ClipNodesUnderUnreadParent);
        Assert.Equal(28_504, c.ClipNodesUnderUnreadParentAllowed);
        Assert.Equal(0, c.ClipNodesWithNoParent);
        // no clip is on GR_Graph's list without a node use: the list is the set the clip nodes name
        Assert.Equal(0, c.ClipsListedWithoutNodeUse);
        // the evidence for that rule: an allowed clip node's parent object holds the clip's own duration (an f32 within 0.5 ms) no more
        // often than a control clip's
        Assert.Equal(28_504, c.ParentObjectsChecked);
        Assert.Equal(25, c.ParentsHoldingOwnDuration);
        Assert.Equal(66, c.ParentsHoldingControlDuration);
        Assert.True(c.ParentsHoldingOwnDuration <= c.ParentsHoldingControlDuration);
        // clips under a blend's child: speed, root-motion scale and mirror allowed with the blend's warning, import and swaps with its
        // replace warning. 1,744 have it for their clip node's parent; 242 more only 2 or 3 levels up by the order rule
        Assert.Equal(1_986, c.ClipsUnderBlend);
        Assert.Equal(242, c.ClipsUnderBlendIndirect);
        Assert.Equal(1_976, c.ClipsUnderBlendLengthFree);
        Assert.Equal(1_986, c.ClipsUnderBlendMotionFree);
        Assert.Equal(30_828, c.UsesByKind[GraphUseKind.ClipNode]);
        Assert.Equal(2_270, c.UsesByKind[GraphUseKind.MotionFieldClip]);
        Assert.Equal(2_305, c.UsesByKind[GraphUseKind.MotionFieldSegment]);
        Assert.Equal(9_938, c.UsesByKind[GraphUseKind.GraphClipList]);
        Assert.Equal(153, c.UsesByKind[GraphUseKind.Unread]);
        // the unread places all belong to objects of class 0x7F75D566 (one after each bone mask 0xB0F618C4): the clip in the object itself
        // (50), or in the ID object after the object's test (103); none in a record's tail
        Assert.Equal(103, c.UnreadPlaces.Where(k => k.Key.Class == GraphClasses.IdObject).Sum(k => k.Value));
        Assert.Equal(50, c.UnreadPlaces.Where(k => k.Key.Class == 0x7F75D566).Sum(k => k.Value));
        Assert.All(c.UnreadPlaces, k => Assert.Equal(0x7F75D566u, k.Key.Owner));
        Assert.DoesNotContain(c.UnreadPlaces, k => k.Key.Tail);
    }

    MotionField Field(ulong doc, int clips) => Assert.Single(G.MotionFields, m => m.Document.Id == doc && m.Field.Clips.Count == clips).Field;

    [GameDataFact]
    public void TheMotionFields()
    {
        var hub = Field(Hub, 34);   // the hub's moving state MF_std_V1_V2_Nwpn
        Assert.Equal(35, hub.Segments.Count);
        Assert.Equal(4_608, hub.Rows);
        var sprint = Field(ArmedSprint, 12);
        Assert.Equal(12, sprint.Segments.Count);
        Assert.Equal(4_015, sprint.Rows);
        var slopes = Field(ArmedSlopes, 50);
        Assert.Equal(50, slopes.Segments.Count);
        Assert.Equal(5_121, slopes.Rows);
        // every table's rows run on from segment to segment; start + length + remainder against the clip's own duration
        int exact = 0, under = 0, over = 0;
        foreach (var (_, mf) in G.MotionFields)
        {
            int prev = 0;
            foreach (var s in mf.Segments)
            {
                Assert.Equal(prev, s.FirstRow);
                prev = s.EndRow;
                if (f.Install.FindClip(mf.Clips[s.ClipIndex]) is not { } clip) continue;
                float sum = s.Start + s.Length + s.Remainder;
                if (Math.Abs(sum - clip.Duration) < 0.002f) exact++; else if (sum < clip.Duration) under++; else over++;
            }
        }
        Assert.Equal(646, exact);
        Assert.Equal(1_658, under);   // clips not covered whole: starts 0.5 s in, stops early
        Assert.Equal(1, over);        // fm_std_V0_ale_idle_neutral_01 (2.6 s) under a segment of 3.84 s
    }

    [GameDataFact]
    public void TheHolsteredWalkIsInAMotionField()
    {
        var clip = f.Install.FindClip("mcm_nwp_fm_std_V1_cycle")!;
        Assert.Equal(0x000001A6250504A0ul, clip.Id);
        var v = G.Verdict(clip.Id);
        Assert.True(v.IsMotionField);
        Assert.False(v.CanChangeLength);
        Assert.False(v.CanChangeMotion);
        Assert.True(v.WarnOnReplace);
        var seg = Assert.Single(v.Uses, u => u.Kind == GraphUseKind.MotionFieldSegment);
        Assert.Equal(Hub, seg.DocumentId);
        Assert.Equal(0, seg.ListIndex);
        var s = Field(Hub, 34).Segments[0];
        Assert.Equal(106, s.EndRow - s.FirstRow);
        Assert.Equal((double)clip.Duration, s.Start + s.Length + s.Remainder, 0.002);
        Assert.Contains(v.Uses, u => u.Kind == GraphUseKind.MotionFieldClip && u.DocumentId == Hub && u.ListIndex == 0);
        Assert.Contains(v.Uses, u => u.Kind == GraphUseKind.ClipNode && u.DocumentId == Hub);
    }

    [GameDataFact]
    public void TheDeathMachinesClips()
    {
        var doc = Assert.Single(G.Documents, d => d.Id == DeathMachine);
        var nodes = GraphNodes.ClipNodes(doc, out _);
        Assert.Equal(251, nodes.Count);
        var clips = nodes.Select(n => n.ClipId).Distinct().ToList();
        Assert.Equal(228, clips.Count);
        Assert.All(clips, id => Assert.NotNull(f.Install.FindClip(id)));
        Assert.All(clips, id => Assert.True(G.Verdict(id).CanChangeLength));
        Assert.All(clips, id => Assert.False(G.Verdict(id).IsMotionField));
        // one death clip: named by a clip node in the enemies' death machine and one in the teammates' copy, and by GR_Graph's list
        var death = f.Install.FindClip("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01")!;
        var v = G.Verdict(death.Id);
        Assert.Equal(2, v.Uses.Count(u => u.Kind == GraphUseKind.ClipNode));
        Assert.Contains(v.Uses, u => u.DocumentId == DeathMachine);
        Assert.Contains(v.Uses, u => u.DocumentId == 0x000001A2B8DBF324);
        Assert.Single(v.Uses, u => u.Kind == GraphUseKind.GraphClipList);
        Assert.True(v.CanChangeLength);
        Assert.True(v.CanChangeMotion);
        Assert.False(v.WarnOnReplace);
        Assert.Null(v.LengthRefusal);
    }

    /// <summary>The hub's document damaged: cut anywhere up to its stated length, or changed in its head, tokens or body and written
    /// again as a stream. Each is refused with a <see cref="GraphDocumentException"/> or reads, never anything else.</summary>
    [GameDataFact]
    public void DamagedDocumentsAreRefusedCleanly()
    {
        var rec = f.Install.Records.First(r => r.Id == Hub);
        var good = GraphDocument.Read(rec);
        int streamEnd = good.StreamAt + good.StreamLength;
        var rnd = new Random(3);
        Within.Seconds(120, () =>
        {
            var cuts = new List<int> { 0, 1, good.StreamAt - 1, good.StreamAt, good.StreamAt + 1, streamEnd - 3, streamEnd - 1, streamEnd, streamEnd + 7 };
            for (int i = 0; i < 20; i++) cuts.Add(rnd.Next(streamEnd));
            foreach (int n in cuts)
                Assert.Throws<GraphDocumentException>(() => GraphDocument.Read(new DatafileRecord { Id = Hub, Type = rec.Type, Name = rec.Name, Bytes = rec.Bytes[..n] }));
            // bytes changed inside the stream itself
            for (int i = 0; i < 30; i++)
            {
                var b = rec.Bytes.ToArray();
                b[good.StreamAt + rnd.Next(good.StreamLength)] ^= (byte)(1 << rnd.Next(8));
                try { GraphDocument.Read(new DatafileRecord { Id = Hub, Type = rec.Type, Name = rec.Name, Bytes = b }); }
                catch (GraphDocumentException) { }
            }
            // the decoded document changed in its tokens: refused by the head's sums and counts or by the walker
            int refused = 0;
            for (int i = 0; i < 40; i++)
            {
                var d = (byte[])good.Bytes.Clone();
                d[rnd.Next(good.Tokens[0].At, good.BodyAt)] ^= 0xFF;
                try { GraphDocument.Read(Record(Hub, rec.Type, rec.Name, d)); }
                catch (GraphDocumentException) { refused++; }
            }
            Assert.True(refused >= 36, $"{refused} of 40 changed tokens refused");
            // and in its body: read or refused, nothing else
            for (int i = 0; i < 20; i++)
            {
                var d = (byte[])good.Bytes.Clone();
                d[rnd.Next(good.BodyAt, d.Length)] ^= 0xFF;
                try { GraphDocument.Read(Record(Hub, rec.Type, rec.Name, d)); }
                catch (GraphDocumentException) { }
            }
            return 0;
        });
    }
}
