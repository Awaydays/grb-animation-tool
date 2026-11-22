using System.IO;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>Keeps the package list (<see cref="PackageList.Current"/>) between runs of the tool, through a <see cref="PackageStore"/>: on an
/// install opened it puts back the clips kept on that install, and saves the list again whenever it changes. A successful Build takes its
/// clips out of the list (<see cref="PackageView.BuildAsync"/>), so the list is kept only until it is built. With no folder (an automated
/// run given no <c>--package-store</c>) it keeps nothing.</summary>
sealed class PackageKeeper
{
    readonly PackageStore? _store;
    GameInstall? _install;
    bool _quiet;

    public PackageKeeper(string? folder)
    {
        _store = folder is null ? null : new PackageStore(folder);
        PackageList.Current.Changed += (_, _) => Save();
    }

    /// <summary>The store's file, or null when nothing is kept.</summary>
    public string? FilePath => _store?.FilePath;

    /// <summary>Raised with a message when the list couldn't be saved.</summary>
    public event Action<string>? SaveFailed;

    /// <summary>A new install is being opened: the list's changes until <see cref="Restore"/> (the screens emptying it for the new install)
    /// aren't saved, so the kept file still holds the last run's list to restore from.</summary>
    public void Opening() => _quiet = true;

    /// <summary>Puts back the clips kept on <paramref name="g"/>'s game files (the list is empty by now), and from then on saves the list
    /// as it changes. Writes nothing itself: opening game files never changes the kept file. A kept file that can't be read in any way
    /// is said, never in the way of the install opening. Returns what was restored, what stays kept without being shown, or the problem.</summary>
    public PackageRestore Restore(GameInstall g)
    {
        _install = g;
        var r = PackageRestore.Nothing;
        try
        {
            if (_store is not null) r = _store.Load(g);
            foreach (var i in r.Restored) PackageList.Current.Keep(i);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while reading the package list kept between runs");
            r = new PackageRestore(PackageList.Current.Items, [], [], $"The package list kept between runs couldn't be read ({ex.GetType().Name}: {ex.Message}). File: {_store?.FilePath}");
        }
        finally { _quiet = false; }
        return r;
    }

    /// <summary>Forgets the kept clips that don't fit the game files open (the list on the screen and every other game files' list stay).
    /// Returns an error message, or null.</summary>
    public string? ForgetNotFitting()
    {
        if (_store is null || _install is not { } g) return null;
        try { _store.ForgetNotFitting(g, PackageList.Current.Items); return null; }
        catch (Exception ex)
        {
            App.LogError(ex, "while forgetting kept clips");
            return $"The kept clips couldn't be forgotten ({ex.GetType().Name}): {ex.Message}";
        }
    }

    /// <summary>A build of clips kept on <paramref name="built"/> ended after another install was opened: those clips leave the list kept
    /// for its game files, and, when the install open now has the same game files, the list on the screen too (each by its ID and its
    /// record, so a clip kept again since stays). Returns the sentence the Package screen says of it.</summary>
    public string RemoveBuilt(GameInstall built, IReadOnlyList<PackageItem> items)
    {
        try
        {
            if (_install is { } open && PackageStore.SameList(open, built))
            {
                // the list on the screen is that list: taken out there, and the save follows
                var done = PackageList.Current.Items.Where(c => items.Any(b => b.Slot.Id == c.Slot.Id && b.Record.AsSpan().SequenceEqual(c.Record))).ToList();
                _lastSaveError = null;
                PackageList.Current.RemoveItems(done);
                if (_lastSaveError is { } failed)
                    return $"Another game folder with the same game files was opened while it was built: the clips built are out of the list, but the list kept between runs couldn't be changed ({failed}).";
                // and the clips built that don't fit the install open now (kept in the file, not on the screen) leave the file too
                try { _store?.RemoveBuilt(built, items); }
                catch (Exception ex)
                {
                    App.LogError(ex, "while taking the clips built that don't fit out of the package list kept between runs");
                    return $"Another game folder with the same game files was opened while it was built: the clips built are out of the list, but those "
                        + $"that don't fit this game folder stay in the list kept between runs ({ex.GetType().Name}: {ex.Message}).";
                }
                SayIfChangedElsewhere();
                return "Another game folder with the same game files was opened while it was built: the clips built are out of the list now.";
            }
            if (_store is null) return $"Another game folder was opened while it was built; this run keeps no list, so there is nothing more to do.";
            int n = _store.RemoveBuilt(built, items);
            SayIfChangedElsewhere();
            return n == 0 ? $"Another game folder was opened while it was built: none of the clips built was left in the list kept for {built.GameFolder}."
                : $"Another game folder was opened while it was built: the {n} clip{(n == 1 ? "" : "s")} built {(n == 1 ? "is" : "are")} out of the list kept for {built.GameFolder}.";
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while taking the clips built out of the package list kept between runs");
            return $"The clips built couldn't be taken out of the list kept for {built.GameFolder} ({ex.GetType().Name}): {ex.Message}";
        }
    }

    /// <summary>A build ended on the install open: its clips leave the list (a clip kept while it ran stays), and the list kept between
    /// runs is saved. Returns why that save failed, or null.</summary>
    public string? RemoveBuiltHere(IReadOnlyList<PackageItem> items)
    {
        _lastSaveError = null;
        PackageList.Current.RemoveItems([.. items]);
        return _lastSaveError;
    }

    /// <summary>Why the last save failed (null: it didn't, or nothing was saved).</summary>
    string? _lastSaveError;

    void Save()
    {
        if (_quiet || _store is null || _install is not { } g) return;
        try
        {
            _store.Save(g, PackageList.Current.Items);
            _lastSaveError = null;
            SayIfChangedElsewhere();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while keeping the package list for the next run");
            _lastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            SaveFailed?.Invoke($"The package list couldn't be kept for the next run ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>After a write of the kept file: another running copy of the tool had changed this game folder's list in it since this one
    /// read it, so its file was kept aside: never replaced without a word. Each write says it at most once.</summary>
    void SayIfChangedElsewhere()
    {
        if (_store?.ChangedElsewhere is { } aside)
            SaveFailed?.Invoke($"Another running copy of the tool changed the package list kept between runs. This copy's list is saved in its place; "
                + $"the other copy's file is kept beside it as {Path.GetFileName(aside)}.");
    }
}
