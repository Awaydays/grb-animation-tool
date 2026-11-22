using System.Diagnostics;
using System.Text;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Cli;

static partial class Program
{
    /// <summary>The cap of the index's --out file.</summary>
    const long IndexOutCap = 32L << 20;

    /// <summary>The clip index's census: clips per category, uncategorised per kind, what each source decided, readable names; with
    /// --clip, one clip's categories with their sources and reasons and the documents that name it; with --out, the census, every
    /// document's placement and every clip's line, into a file (capped). With --cache, the graph's part through a cache in that folder
    /// (never the user's own unless named), and how it was had.</summary>
    static int IndexCommand(string game, Dictionary<string, string> o)
    {
        string? outFile = o.GetValueOrDefault("out"), cache = o.GetValueOrDefault("cache");
        var protect = ClipExport.ProtectedFolders(game, o.GetValueOrDefault("forge"));
        if (outFile is not null) ClipExport.Guard(outFile, protect);
        if (cache is not null) ClipExport.Guard(cache, protect);
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        Console.WriteLine($"opened in {g.Timings.Total.TotalSeconds:F2} s: {g.Clips.Count} clips");
        var sw = Stopwatch.StartNew();
        ClipIndex index;
        if (cache is not null)
        {
            var r = new ClipIndexCache(cache).Open(g);
            index = r.Index;
            Console.WriteLine($"cache {new ClipIndexCache(cache).FileFor(g)}: {r.Outcome}, graph part in {r.GraphTime.TotalSeconds:F2} s"
                + (r.SaveError is null ? "" : $"; not saved: {r.SaveError}"));
        }
        else
        {
            var facts = GraphFacts.Read(g.Records, g.Clips.Select(c => c.Id));
            Console.WriteLine($"graph read in {sw.Elapsed.TotalSeconds:F2} s");
            index = ClipIndex.Build(g, facts, ClipIndexList.Embedded(), IndexFingerprint.Of(g));
        }
        Console.WriteLine($"index built in {sw.Elapsed.TotalSeconds:F2} s in all");
        foreach (var f in index.Facts.Failures) Console.WriteLine($"not read: {f}");
        var census = index.Census();
        foreach (var l in census.Lines()) Console.WriteLine(l);
        Console.WriteLine(PlayerLine(index));
        if (index.List is { Problems.Count: > 0 } list) foreach (var p in list.Problems.Take(20)) Console.WriteLine($"   the tool's list: {p}");
        if (o.TryGetValue("clip", out var clip))
        {
            var c = FindClip(g, clip);
            foreach (var l in ClipLines(index.Find(c.Id)!)) Console.WriteLine(l);
        }
        if (outFile is not null)
            ClipExport.WriteFile(outFile, ClipExport.ProtectedFolders(g), fs =>
            {
                using var w = new CappedWriter(fs, IndexOutCap);
                foreach (var l in census.Lines()) w.Line(l);
                w.Line("");
                w.Line("# documents: who and what their clips are by the graph's rules, and the rules that fired");
                var parents = new List<int>[index.Facts.Documents.Count];
                for (int i = 0; i < parents.Length; i++) parents[i] = [];
                for (int i = 0; i < parents.Length; i++) foreach (int ch in index.Facts.Documents[i].Children) parents[ch].Add(i);
                for (int i = 0; i < index.Facts.Documents.Count; i++)
                {
                    var d = index.Facts.Documents[i];
                    var p = index.DocumentPlacements[i];
                    w.Line($"[{i}] 0x{d.Id:X16} {d.Name}: clips {d.Clips}; parent {string.Join(", ", parents[i].Select(x => index.Facts.Documents[x].Name))}; "
                        + $"who {Join(p.Who.Keys)}{(p.Shared ? " (shared by its layers)" : "")}; what {Join(p.What.Keys)}; weak what {Join(p.WeakWhat.Keys)}; rules {Join(p.Rules)}"
                        + (p.UnderNpcMachine ? "; under the NPC machine" : ""));
                }
                w.Line("");
                w.Line("# clips: who [source] (probable ones) | what [source] | readable name | documents (a container's weak what: graph-container; who narrowed by the family: name-narrowed)");
                foreach (var e in index.Entries)
                    w.Line($"0x{e.ClipId:X16} {e.ClipName} | {Join(e.Names(CategoryKind.Who))} [{Src(e, CategoryKind.Who)}]{(e.WhoProbably.Count > 0 ? $" (probably {Join(e.WhoProbably)})" : "")} | "
                        + $"{Join(e.Names(CategoryKind.What))} [{Src(e, CategoryKind.What)}] | {e.ReadableName ?? "-"} | {Join(e.Documents.Select(x => x.Name).Distinct())}");
            });
        return index.Facts.Complete ? 0 : 3;

        static string Join(IEnumerable<string> s) => s.Any() ? string.Join(", ", s) : "-";
        static string Src(ClipIndexEntry e, CategoryKind k) => e.SourceOf(k) switch
        {
            CategorySource.Graph => k == CategoryKind.What && e.WhatFromContainer ? "graph-container" : "graph",
            CategorySource.ToolList => "list",
            CategorySource.Name => k == CategoryKind.Who && e.WhoNarrowedByName ? "name-narrowed" : "name",
            _ => "-",
        };
    }

    /// <summary>How the index's Player clips were placed: by the player's own layers (a document reached only from the player's or the
    /// menu character's layer names the clip), only through machines the NPCs' layers play too, or by the list or the
    /// name; and how many clips the name's family took Player away from (the graph's union held it, the narrowed who doesn't).</summary>
    static string PlayerLine(ClipIndex index)
    {
        string[] own = ["branch.player", "branch.menu"];
        int ownOnly = 0, shared = 0, other = 0, removed = 0, sharedNarrowedToPlayer = 0;
        foreach (var e in index.Entries)
        {
            var docs = index.Facts.Clips.TryGetValue(e.ClipId, out var cf) ? cf.Documents : [];
            var giving = docs.Where(d => index.DocumentPlacements[d].Who.ContainsKey(ClipCategories.Player)).ToList();
            bool graphHadPlayer = giving.Count > 0;
            if (!e.In(CategoryKind.Who, ClipCategories.Player))
            {
                if (graphHadPlayer && e.WhoNarrowedByName) removed++;
                continue;
            }
            if (e.SourceOf(CategoryKind.Who) == CategorySource.ToolList || !graphHadPlayer) { other++; continue; }
            if (giving.Any(d => index.DocumentPlacements[d].Branches.All(own.Contains))) ownOnly++;
            else { shared++; if (e.WhoNarrowedByName) sharedNarrowedToPlayer++; }
        }
        return $"Player: by the player's own layers {ownOnly}; only through machines NPC layers play too {shared} (of them narrowed to the player's "
            + $"side by the family {sharedNarrowedToPlayer}); by the list or the name {other}; Player taken away by the family {removed}";
    }

    /// <summary>One clip's index entry as the clip info would show it.</summary>
    static IEnumerable<string> ClipLines(ClipIndexEntry e)
    {
        yield return $"0x{e.ClipId:X16} {e.ClipName}" + (e.ReadableName is { } n ? $": \"{n}\"" : "");
        foreach (var kind in new[] { CategoryKind.Who, CategoryKind.What })
        {
            string k = kind == CategoryKind.Who ? "who" : "what";
            if (e.IsUncategorised(kind)) { yield return $"   {k}: {ClipCategories.Uncategorised}"; continue; }
            foreach (var c in e.Of(kind)) yield return $"   {k}: {c.Name}, {c.SourceLabel}: {c.Reason}" + (c.Part is { } part ? $" [the part of the graph: {part}]" : "");
            if (kind == CategoryKind.Who)
            {
                var users = ClipUsers.Of(e);
                yield return $"   played by: {users.Text}" + (users.Notice is { } notice ? $" ({notice})" : "");
            }
        }
        foreach (var g in e.Explained) yield return $"   glossary: {g.Abbreviation} = {g.Meaning}";
        yield return $"   graph documents naming it ({e.Documents.Count}): " + (e.Documents.Count == 0 ? "none"
            : string.Join(", ", e.Documents.Select(d => $"{d.Name} 0x{d.Id:X16}")));
        yield return "   search fields: " + string.Join("; ", e.SearchFields.Select(f => FormattableString.Invariant($"{f.Text} ({f.Weight:0.0})")));
    }

    /// <summary>A text writer that stops the command once it has written its cap.</summary>
    sealed class CappedWriter(Stream s, long cap) : IDisposable
    {
        readonly StreamWriter _w = new(s, new UTF8Encoding(false));
        long _n;

        public void Line(string l)
        {
            _n += Encoding.UTF8.GetByteCount(l) + 2;
            if (_n > cap) throw new LogCapReached($"the output reached its cap of {cap} B");
            _w.WriteLine(l);
        }

        public void Dispose() => _w.Dispose();
    }
}
