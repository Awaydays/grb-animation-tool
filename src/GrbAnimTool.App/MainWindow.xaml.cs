using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The window: the five screens as tabs, Library, Edit, Import, Package and Log. Under --screenshot (or --shot, the exports, an import,
/// an edit or a package build) it stays off-screen, loads what the command line names, does the work, renders itself to each PNG (each
/// picture showing its screen) and closes.</summary>
public partial class MainWindow : Window
{
    readonly ViewerArgs _args;
    readonly PackageKeeper _keeper;

    const int LibraryTab = 0, EditTab = 1, ImportTab = 2, PackageTab = 3, LogTab = 4;

    public MainWindow(ViewerArgs args)
    {
        _args = args;
        InitializeComponent();
        if (args.Unattended)
        {
            // pictures are rendered from the visual tree: the window is shown (WPF's 3D needs that) but far off-screen, never activated
            // and not in the taskbar, so it never takes the focus or appears
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Focusable = false;
            if (args.Size is { } size) { Width = Math.Max(size.Width, MinWidth); Height = Math.Max(size.Height, MinHeight); }
        }
        else FitToWorkArea();
        // an automated import or edit shows its screen from the start, so its 3D view is laid out and rendered like the Library's
        if (args.Imports) Tabs.SelectedIndex = ImportTab;
        else if (args.Edits) Tabs.SelectedIndex = EditTab;
        Loaded += OnLoaded;
        // the package list kept between runs: in the user's own folder for a person's run, where --package-store says for an automated one
        // (none without it)
        _keeper = new PackageKeeper(args.PackageStore ?? (args.Automated ? null : PackageStore.DefaultFolder));
        _keeper.SaveFailed += PackagePage.ShowError;
        PackagePage.ForgetRequested = _keeper.ForgetNotFitting;
        PackagePage.BuiltElsewhere = _keeper.RemoveBuilt;
        PackagePage.BuiltHere = _keeper.RemoveBuiltHere;
        // the Package screen's vanilla forge backup, remembered in the tool's own settings (an automated run's only where --settings says)
        PackagePage.UseSettings(args.Settings ?? (args.Automated ? null : ViewerSettings.DefaultPath));
        // the Log screen's folder, remembered there too; an automated run follows only the folder its command line or settings name
        LogPage.UseSettings(args.Settings ?? (args.Automated ? null : ViewerSettings.DefaultPath), args.Automated);
        // the Log screen isn't the one shown first: it reads nothing until it is (a log the command line names gets its first read)
        if (Tabs.SelectedIndex != LogTab) LogPage.Hidden();
        LibraryPage.InstallShown += (g, automated) =>
        {
            _keeper.Opening();
            ImportPage.SetInstall(g, automated);
            EditPage.SetInstall(g, automated, LibraryPage, marks: args.WorksMarksOut);
            PackagePage.SetInstall(g);
            PackagePage.ShowRestored(_keeper.Restore(g), _keeper.FilePath);
            LogPage.SetInstall(g);
        };
        LibraryPage.IndexReady += (g, index) =>
        {
            ImportPage.SetIndex(g, index);
            EditPage.SetIndex(g, index);
            PackagePage.SetIndex(g, index);
            LogPage.SetIndex(g, index);
        };
        // a line of the log clicked: its clip chosen in the Library, as a click on its row there would, and the Library shown
        LogPage.ClipOpened += c =>
        {
            Tabs.SelectedIndex = LibraryTab;
            LibraryPage.ShowFromLog(c);
        };
        LibraryPage.IndexFailed += (g, reason) =>
        {
            ImportPage.SetIndexError(g, reason);
            EditPage.SetIndexError(g, reason);
            PackagePage.SetIndexError(g, reason);
        };
        // the Package and Import screens show the index's progress while Build and the export wait for it
        LibraryPage.IndexProgress += PackagePage.SetIndexProgress;
        LibraryPage.IndexProgress += ImportPage.SetIndexProgress;
        PreviewKeyDown += (_, e) =>
        {
            if (Tabs.SelectedIndex == LibraryTab) LibraryPage.HandleKey(e);
            else if (Tabs.SelectedIndex == EditTab) EditPage.HandleKey(e);
            else if (Tabs.SelectedIndex == ImportTab) ImportPage.HandleKey(e);
        };
        CheckTheme();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    /// <summary>Ticks the theme shown in the Settings menu.</summary>
    void CheckTheme()
    {
        ThemeSystemItem.IsChecked = Theme.Choice == ThemeChoice.System;
        ThemeLightItem.IsChecked = Theme.Choice == ThemeChoice.Light;
        ThemeDarkItem.IsChecked = Theme.Choice == ThemeChoice.Dark;
    }

    /// <summary>A theme chosen in the Settings menu: shown at once, and remembered for the next run (not by an automated run, which
    /// neither reads nor writes the settings).</summary>
    void OnQuickStart(object sender, RoutedEventArgs e) => Notice.Show(Core.Library.HelpTexts.QuickStart, this, "Quick start");

    void OnAbout(object sender, RoutedEventArgs e) =>
        Notice.Show($"{Core.Export.ToolInfo.Name} {Core.Export.ToolInfo.Version}\n\nLicensed under GPL-3.0-or-later, with an additional permission for loading "
            + "the game's own Oodle library (see LICENSE and LICENSE-EXCEPTION.md beside the program).", this, "About");

    /// <summary>The Package folder menu opened: the folder set shown, None greyed while none is set.</summary>
    void OnPackageFolderOpened(object sender, RoutedEventArgs e)
    {
        PackageFolderChooseItem.ToolTip = PackagePage.PackageFolderSetting is { } f ? $"Now: {f}" : "None set: Build starts in the folder last built in.";
        PackageFolderClearItem.IsEnabled = PackagePage.PackageFolderSetting is not null;
    }

    void OnPackageFolderChoose(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder Build starts in (a folder of your own, never the game's)" };
        if (PackagePage.PackageFolderSetting is { } f && System.IO.Directory.Exists(f)) dlg.InitialDirectory = f;
        if (dlg.ShowDialog(this) != true) return;
        PackagePage.SetPackageFolder(dlg.FolderName);
    }

    void OnPackageFolderClear(object sender, RoutedEventArgs e) => PackagePage.SetPackageFolder(null);

    void OnThemeChosen(object sender, RoutedEventArgs e)
    {
        var choice = sender == ThemeDarkItem ? ThemeChoice.Dark : sender == ThemeLightItem ? ThemeChoice.Light : ThemeChoice.System;
        // a click on the item already ticked unticks it: it stays ticked
        if (choice == Theme.Choice) { CheckTheme(); return; }
        Theme.Apply(choice);
        CheckTheme();
        if (!_args.Automated) LibraryPage.SaveTheme(choice);
    }

    /// <summary>Windows' light or dark app setting changed (under Follow Windows), or its high contrast or its colours: the tool's styles
    /// and palette follow, once the Fluent theme, which follows by itself, has put in its own (a refresh queued at a low priority, once
    /// for a burst of messages).</summary>
    IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A, WM_SYSCOLORCHANGE = 0x0015, WM_THEMECHANGED = 0x031A, SPI_SETHIGHCONTRAST = 0x0043;
        bool follow = msg is WM_SYSCOLORCHANGE or WM_THEMECHANGED
            || msg == WM_SETTINGCHANGE && (wParam == SPI_SETHIGHCONTRAST || Theme.Choice == ThemeChoice.System && lParam != IntPtr.Zero
                && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet");
        if (follow && !_refreshQueued)
        {
            _refreshQueued = true;
            Dispatcher.BeginInvoke(() => { _refreshQueued = false; Theme.Refresh(); }, DispatcherPriority.ContextIdle);
        }
        return IntPtr.Zero;
    }

    bool _refreshQueued;

    /// <summary>The Edit screen takes the Library's clip when shown with none; the Package screen shows the list as it stands.</summary>
    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;   // a list or a box inside a screen
        if (Tabs.SelectedIndex == EditTab && !_args.Unattended) EditPage.Shown();
        else if (Tabs.SelectedIndex == PackageTab) PackagePage.Refresh();
        // the Log screen reads its log only while it is in sight, and catches up when it is shown again
        if (Tabs.SelectedIndex == LogTab) LogPage.Shown();
        else LogPage.Hidden();
    }

    /// <summary>Shows a screen and lets it lay out and render before going on.</summary>
    async Task ShowTab(int tab)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool shown = Tabs.SelectedIndex == tab;
        Tabs.SelectedIndex = tab;
        if (Timing && !shown)
        {
            // the first frame: the layout and the render pass after the click on the tab; then an input's turn on the window's thread
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            double frame = sw.Elapsed.TotalMilliseconds;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Console.WriteLine(FormattableString.Invariant($"timing: tab {((TabItem)Tabs.Items[tab]).Header.ToString()!.Trim()} shown: first frame {frame:F0} ms, first input handled {sw.Elapsed.TotalMilliseconds:F0} ms"));
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
    }

    /// <summary>The command line's import, for automated checks: the Import screen shown, the file and the slot chosen, the import run,
    /// then the export and the keep it asks for. Returns an error message, or null; prints what was written.</summary>
    async Task<string?> ImportForAutomationAsync()
    {
        await ShowTab(ImportTab);
        await ImportPage.ChooseGlbAsync(_args.ImportGlb!, pickNamed: false);
        if (await ImportPage.ChooseSlotAsync(_args.ImportClip!) is { } err) return err;
        if (ImportPage.Result is not { } r) return ImportPage.LastError ?? "the import made nothing";
        Console.WriteLine($"imported {_args.ImportGlb} into {r.Slot.Name}: {Core.Checks.CheckResult.Word(r.Report.Verdict)}");
        foreach (var l in r.Report.Lines()) Console.WriteLine("   " + l);
        if (_args.ImportOut is { } dir)
        {
            // the export waits for the clip index, as on the screen; files already there are refused unless --overwrite, as the screen's
            // box decides
            if (await ImportPage.ExportAsync(dir, _args.Overwrite, commandLine: true) is { } e2) return e2;
            Console.WriteLine($"wrote the record file into {dir}");
        }
        if (_args.ImportKeep)
        {
            if (await ImportPage.KeepAsync() is { } e3) return e3;
            Console.WriteLine($"kept {r.Slot.Name} for the package ({Core.Import.PackageList.Current.Count} kept)");
        }
        return null;
    }

    public LibraryView Library => LibraryPage;

    /// <summary>Shows an error in the error strip of the screen in front (an error no code caught: <see cref="ErrorReport"/>). Returns
    /// true.</summary>
    public bool ShowError(string message)
    {
        switch (Tabs.SelectedIndex)
        {
            case EditTab: EditPage.ShowError(message); break;
            case ImportTab: ImportPage.ShowError(message); break;
            case PackageTab: PackagePage.ShowError(message); break;
            case LogTab: LogPage.ShowError(message); break;
            default: LibraryPage.ShowError(message); break;
        }
        return true;
    }

    /// <summary>Opens at the designed size where it fits, else at most the work area (the screen less the taskbar), centred in it, so
    /// the title bar is never above the screen's top on a small or scaled screen.</summary>
    void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, wa.Width);
        MinHeight = Math.Min(MinHeight, wa.Height);
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_args.Unattended)
        {
            // a log the command line names is opened (or its folder followed) at once; else the Log screen waits to be shown
            if (_args.Logs) StartLog();
            await LibraryPage.StartAsync(_args);
            return;
        }
        int code = 1;
        if (Timing) Console.WriteLine(FormattableString.Invariant($"timing: the window loaded {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms after the start"));
        try
        {
            string? error = await LibraryPage.LoadForShotsAsync(_args);
            if (Timing) Console.WriteLine(FormattableString.Invariant($"timing: the install and its clip index open {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms after the start"));
            // the exports first, so a picture after them shows their confirmation
            // the log the command line names, its IDs looked up in the install just opened (its clip index there for pictures)
            if (error is null && _args.Logs) error = await LogPage.StartForShotsAsync(_args);
            if (error is null && _args.Exports) error = await LibraryPage.ExportForAutomationAsync(_args);
            if (error is null && _args.Imports) error = await ImportForAutomationAsync();
            if (error is null && _args.Edits)
            {
                await ShowTab(EditTab);
                error = await EditPage.EditForAutomationAsync(_args);
            }
            if (error is null && _args.Packages)
            {
                await ShowTab(PackageTab);
                error = await PackagePage.BuildForAutomationAsync(_args.PackageOut!, _args.PackageName);
            }
            // the Edit screen's list shows each clip's edits once they are worked out: a picture of it waits for them
            if (error is null && _args.ShotsShowEdit)
            {
                await EditPage.WaitForChoicesAsync();
                if (Timing) Console.WriteLine($"timing: the edit marks were {EditPage.MarksHad}");
            }
            if (error is not null) Console.Error.WriteLine("error: " + error);
            else
            {
                code = 0;
                Console.WriteLine($"render tier {RenderCapability.Tier >> 16}");
                foreach (var shot in _args.Shots)
                {
                    // a picture of a screen waiting (for the clip index, for the edits each clip allows) holds what it waits for back for
                    // that picture alone
                    EndWaitingForShots();
                    // a picture may switch the theme, as the Settings menu does, for itself and the pictures after it
                    if (shot.Theme is { } theme && theme != Theme.Choice) { Theme.Apply(theme); CheckTheme(); }
                    string screen = _args.ScreenFor(shot);
                    await ShowTab(screen switch
                    {
                        "edit" or ViewerArgs.EditWaiting => EditTab, "import" or ViewerArgs.ImportWaiting => ImportTab,
                        "package" or ViewerArgs.PackageWaiting => PackageTab, "log" => LogTab, _ => LibraryTab,
                    });
                    // the Edit screen's picture of a clip of the shot's (or the run's) when no edit ran: chosen as a click in its list would
                    string? e2 = screen is "edit" or ViewerArgs.EditWaiting && !_args.Edits && (shot.Clip ?? _args.Clip) is { } editClip
                        ? await ChooseForShot(editClip) : null;
                    e2 ??= screen switch
                    {
                        "import" => ImportPage.ShowForShot(shot, _args),
                        ViewerArgs.ImportWaiting => ImportPage.ShowWaitingForShot(shot, _args),
                        "edit" => EditPage.ShowForShot(shot, _args),
                        ViewerArgs.EditWaiting => EditPage.ShowWaitingForShot(shot, _args),
                        "package" => null,
                        "log" => LogPage.ShowForShot(shot),
                        ViewerArgs.PackageWaiting => PackagePage.ShowWaitingForShot(shot.Time ?? 0),
                        _ => await LibraryPage.ShowForShotAsync(shot, _args),
                    };
                    if (e2 is not null) { Console.Error.WriteLine($"error: {shot.Png}: {e2}"); code = 1; continue; }
                    // let the 3D scene and the list render before the capture: a layout pass, a render pass, then the dispatcher idle
                    for (int i = 0; i < 2; i++)
                    {
                        await Task.Delay(100);
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (SavePng(shot.Png, screen) is { } e3) { Console.Error.WriteLine($"error: {shot.Png}: {e3}"); code = 1; continue; }
                    Console.WriteLine($"wrote {shot.Png} ({screen} screen)");
                }
                EndWaitingForShots();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex);
            code = 1;
        }
        // an error the screens caught themselves during the run, or one nobody caught (each on stderr as it came, the second's own end
        // perhaps too late), fails the run, its pictures taken or not
        if (code == 0 && (Application.Current as App)?.Errors is { } errors && errors.CaughtCount + errors.Count > 0)
        {
            Console.Error.WriteLine($"error: {errors.CaughtCount + errors.Count} error(s) the tool didn't foresee came during the run (above)");
            code = 1;
        }
        Application.Current.Shutdown(code);
    }

    /// <summary>A run with its window shown and a log named (<c>--log-file</c> or <c>--log-folder</c>: an automated run, which opens no
    /// remembered game folder): the log is opened, or its folder followed, any error on the way said on the Log screen and logged (never
    /// left in a task nobody looks at).</summary>
    async void StartLog()
    {
        try
        {
            if (await LogPage.StartForShotsAsync(_args) is { } problem) LogPage.ShowError(problem);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while opening the log the command line names");
            LogPage.ShowError($"The log couldn't be opened ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Gives back what a picture of a screen waiting held back.</summary>
    void EndWaitingForShots()
    {
        PackagePage.EndWaitingForShot();
        EditPage.EndWaitingForShot();
        ImportPage.EndWaitingForShot();
    }

    /// <summary>For a picture of the Edit screen: chooses the clip (unless it is shown already), timed under GRBANIM_TIMING. Returns an
    /// error message, or null.</summary>
    async Task<string?> ChooseForShot(string clip)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var err = EditPage.ShowsClip(clip) ? null : await EditPage.ChooseClipAsync(clip);
        if (Timing) Console.WriteLine(FormattableString.Invariant($"timing: {clip} chosen on the Edit screen, its edits shown in {sw.Elapsed.TotalMilliseconds:F0} ms"));
        return err;
    }

    /// <summary>GRBANIM_TIMING=1: an automated run prints how long each screen took to show (its first frame and its first input handled)
    /// and a clip chosen on the Edit screen, for measuring.</summary>
    static readonly bool Timing = Environment.GetEnvironmentVariable("GRBANIM_TIMING") == "1";

    /// <summary>Renders the window's content (without its frame) at the screen's DPI and saves it as a PNG, unless the shown screen's 3D
    /// view isn't drawn (<see cref="PictureCheck.ViewProblem"/>, measured on the view of the screen shown: Library, Edit or Import; the
    /// Package and Log screens have none): then nothing is saved and the reason is returned, so the run fails instead of leaving a misleading picture.</summary>
    string? SavePng(string path, string screen)
    {
        UpdateLayout();
        var src = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(src.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(src.ActualHeight * dpi.DpiScaleY);
        RenderTargetBitmap Render()
        {
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(src);
            return rtb;
        }
        var shot = Render();
        var protect = ClipExport.ProtectedFolders(_args.Game, _args.Forge);
        string full = Path.GetFullPath(path);
        (FrameworkElement Area, Func<RenderTargetBitmap?> Bare)? view3d = screen switch
        {
            "edit" or ViewerArgs.EditWaiting => (EditPage.ViewArea, () => EditPage.WithoutSkeleton(Render)),
            "import" or ViewerArgs.ImportWaiting => (ImportPage.ViewArea, () => ImportPage.WithoutSkeleton(Render)),
            "package" or ViewerArgs.PackageWaiting or "log" => null,
            _ => (LibraryPage.ViewArea, () => LibraryPage.WithoutSkeleton(Render)),
        };
        if (view3d is { } v3)
        {
            var (area, bareOf) = v3;
            if (!area.IsVisible || !src.IsAncestorOf(area)) return $"the {screen} screen's 3D view isn't shown, so the picture wasn't saved.{Stale(full, protect)}";
            var bare = bareOf();
            var b = area.TransformToAncestor(src).TransformBounds(new Rect(area.RenderSize));
            var view = new PixelRect((int)Math.Ceiling(b.X * dpi.DpiScaleX), (int)Math.Ceiling(b.Y * dpi.DpiScaleY),
                (int)Math.Floor(b.Width * dpi.DpiScaleX), (int)Math.Floor(b.Height * dpi.DpiScaleY));
            if (PictureCheck.ViewProblem(Pixels(shot), bare is null ? [] : Pixels(bare), w, h, view) is { } problem)
                return $"{problem} (render tier {RenderCapability.Tier >> 16}), so the picture wasn't saved.{Stale(full, protect)}";
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(shot));
        // guarded like every output, written whole to a name of its own and moved over the path (see ClipExport.WriteFile)
        try { ClipExport.WriteFile(full, protect, enc.Save, makeFolder: true); }
        catch (ExportException ex) { return ex.Message; }
        _written.Add(full);
        return null;

        static int[] Pixels(RenderTargetBitmap r)
        {
            var px = new int[r.PixelWidth * r.PixelHeight];
            r.CopyPixels(px, r.PixelWidth * 4, 0);
            return px;
        }
    }

    /// <summary>The pictures this run saved, so a refused picture removes only a file of its name that this run wrote.</summary>
    readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For a refused picture: a PNG of the same name that this run saved earlier is deleted, so no picture of another moment
    /// stands under its name; a file from before this run is left as it was. The sentence saying which, or "".</summary>
    string Stale(string full, IReadOnlyList<string> protect)
    {
        if (_written.Remove(full))
        {
            try
            {
                ClipExport.Guard(full, protect);
                File.Delete(full);
                return " The picture this run saved under that name earlier was deleted.";
            }
            catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException)
            {
                return $" The picture this run saved under that name earlier couldn't be deleted: {ex.Message}";
            }
        }
        return File.Exists(full) ? " A file of that name from before this run is left as it was: it isn't this picture." : "";
    }
}
