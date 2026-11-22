using System.Text;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Library;

/// <summary>Which clips can be swapped into which: an equal swap takes only a source of the target's length with the identical track list,
/// every hash and every kind in order (the edit census's rule). The clips are grouped once by that key; a clip whose record doesn't read to
/// its end, whose length isn't valid or one of whose curves the shared table can't resolve is in no group.</summary>
public sealed class SwapSources
{
    readonly Dictionary<string, List<ClipEntry>> _groups;
    readonly Dictionary<ulong, string> _keyOf;

    SwapSources(Dictionary<string, List<ClipEntry>> groups, Dictionary<ulong, string> keyOf)
    {
        _groups = groups;
        _keyOf = keyOf;
    }

    /// <summary>The clip's swap key: its last tick, then each track's hash and curve kind in order; null when the clip can't take part.</summary>
    public static string? Key(ClipRecord? c, SharedCurveTable? table)
    {
        if (c is null || !c.WalkedToEnd || !c.DurationValid || c.Curves.Count != c.Tracks.Count) return null;
        var key = new StringBuilder().Append(c.LastTick).Append(':');
        for (int i = 0; i < c.Tracks.Count; i++)
        {
            var u = SharedCurveTable.Resolve(c.Curves[i], table);
            if (u is null) return null;
            key.Append(c.Tracks[i].ToString("X8")).Append((int)u.Kind).Append(',');
        }
        return key.ToString();
    }

    /// <summary>Groups <paramref name="clips"/> by their swap key.</summary>
    public static SwapSources Build(IEnumerable<ClipEntry> clips, SharedCurveTable? table)
    {
        var groups = new Dictionary<string, List<ClipEntry>>(StringComparer.Ordinal);
        var keyOf = new Dictionary<ulong, string>();
        foreach (var e in clips)
        {
            string? k;
            try { k = Key(e.Record, table); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { k = null; }
            if (k is null) continue;
            keyOf[e.Id] = k;
            if (!groups.TryGetValue(k, out var l)) groups[k] = l = [];
            l.Add(e);
        }
        return new SwapSources(groups, keyOf);
    }

    /// <summary>The clips <paramref name="target"/> can take an equal swap from: every other clip of its group, by index.</summary>
    public IReadOnlyList<ClipEntry> For(ClipEntry target) =>
        _keyOf.TryGetValue(target.Id, out var k) ? _groups[k].Where(c => c.Id != target.Id).OrderBy(c => c.Index).ToList() : [];

    /// <summary>How many clips have at least one swap source.</summary>
    public int Swappable => _groups.Values.Where(g => g.Count > 1).Sum(g => g.Count);
}
