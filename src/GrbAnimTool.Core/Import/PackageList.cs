using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Core.Import;

/// <summary>A clip kept for the package: the slot it replaces, its rebuilt record, what changed it (an edit, or an import of a .glb), and
/// its checks' verdict.</summary>
public sealed record PackageItem(ClipEntry Slot, byte[] Record, ClipChange Change, CheckLevel Verdict, string ReportText, DateTime KeptAt);

/// <summary>The clips kept for the package while the program runs, one per slot (keeping a clip for a slot already kept replaces it), in the
/// order they were kept. Held in memory (the viewer keeps a copy between runs: <see cref="Library.PackageStore"/>); the Package screen
/// writes them out. Safe to use from several threads.</summary>
public sealed class PackageList
{
    readonly List<PackageItem> _items = [];
    readonly object _lock = new();

    /// <summary>The program's one list, shared by its screens.</summary>
    public static PackageList Current { get; } = new();

    /// <summary>Raised after the list changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PackageItem> Items { get { lock (_lock) return [.. _items]; } }

    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>Keeps an import's result; a clip already kept for the same slot is replaced. Returns whether one was replaced.</summary>
    public bool Keep(ImportResult r) => Keep(new PackageItem(r.Slot, r.Record, ClipChange.Imported(r.Source), r.Report.Verdict, r.ReportText(), DateTime.Now));

    public bool Keep(PackageItem item)
    {
        bool replaced;
        lock (_lock)
        {
            replaced = _items.RemoveAll(x => x.Slot.Id == item.Slot.Id) > 0;
            _items.Add(item);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return replaced;
    }

    public bool Remove(ulong slotId)
    {
        bool removed;
        lock (_lock) removed = _items.RemoveAll(x => x.Slot.Id == slotId) > 0;
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Removes each of <paramref name="items"/> the list still holds as it was (the very item: one kept since for its slot, while
    /// a build ran, stays). Returns how many were removed.</summary>
    public int RemoveItems(IReadOnlyCollection<PackageItem> items)
    {
        int removed;
        lock (_lock) removed = _items.RemoveAll(x => items.Any(i => ReferenceEquals(i, x)));
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Empties the list (an install closed: its slots no longer stand for anything).</summary>
    public void Clear()
    {
        lock (_lock) _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
