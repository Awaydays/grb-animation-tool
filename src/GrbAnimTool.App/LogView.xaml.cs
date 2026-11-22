using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Logs;

namespace GrbAnimTool.App;

/// <summary>The Log screen: a clip log of the GRB Clip Logger (an optional, separate download; nothing here needs it), opened, or the
/// newest in a folder followed as the game writes it. Each clip start shows its time, the install's clip of the ID logged (its name, and
/// the clip index's readable name and who), the character and the source; an ID that isn't one of the install's clips shows as unknown,
/// with the ID. A click on a start shows its clip in the Library. The log is read, its lines parsed and their IDs looked up off the window's
/// thread (<see cref="ClipLogReader"/>), a growing log by what was added only; the list realises the rows in sight only.</summary>
public partial class LogView : UserControl
{
    GameInstall? _install;

    /// <summary>The starts and the lines that don't read, in the log's order; the notes apart; the rows the filter keeps.</summary>
    readonly List<ClipLogLine> _lines = [];
    ObservableCollection<ClipLogLine> _notes = [];
    ObservableCollection<ClipLogLine> _shown = [];
    ClipLogCounts _counts = new();

    /// <summary>The log being read, null before one is (a folder followed with no log in it yet).</summary>
    ClipLogReader? _reader;

    /// <summary>The folder whose newest log is followed, or shown for following (null: none, an automated run that names none).</summary>
    string? _folder;
    bool _following;

    /// <summary>Which log the reads are for: a read for one left (another opened, the folder changed) is dropped.</summary>
    int _source;

    /// <summary>What the last read said: the folder not there (or not listable), the file not readable now, the file read again.</summary>
    string? _folderProblem, _readProblem;
    bool _restarted;

    /// <summary>The settings file the folder is remembered in (null: not remembered), and whether the run is automated.</summary>
    string? _settingsPath;
    bool _automated;
    bool _shownOnce;

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Task? _polling;
    TaskCompletionSource? _firstRead;

    public LogView()
    {
        InitializeComponent();
        // the clip index's readable name and who after the clip's name, as every clip list has them
        IndexColumns.Insert(List, readable: ClipListPanel.ReadableWidth, who: ClipListPanel.WhoWidth, what: null, at: 2);
        List.ItemsSource = _shown;
        NotesList.ItemsSource = _notes;
        _timer.Tick += (_, _) => { if (_polling is null) Poll(); };
        // a click on a line: the line pressed is the one opened, and only when the button is let go on it (the list's own handling marks
        // both handled); the list holds still meanwhile (Apply). Enter opens the line selected.
        List.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnLinePressed), handledEventsToo: true);
        List.AddHandler(MouseUpEvent, new MouseButtonEventHandler(OnLineReleased), handledEventsToo: true);
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && List.SelectedItem is ClipLogLine l) { Open(l); e.Handled = true; }
        };
        ShowSource();
        UpdateCount();
    }

    /// <summary>Raised when a line is clicked whose clip is one of the install's: the window shows it in the Library.</summary>
    public event Action<ClipEntry>? ClipOpened;

    // ---- what the window gives the screen ----

    /// <summary>Takes the settings file the followed folder is remembered in (null: none) and whether the run is automated. A person's run
    /// follows the remembered folder, else the logger's own in the Documents folder, from the first time the screen is shown. An automated
    /// run follows nothing of itself: the folder its settings name is only put in place (Follow ticked by hand follows it), and only
    /// <c>--log-file</c> or <c>--log-folder</c> start a read.</summary>
    public void UseSettings(string? path, bool automated)
    {
        _settingsPath = path;
        _automated = automated;
        string? kept = path is null ? null : ViewerSettings.Load(path).LogFolder;
        // (a folder of spaces only, a hand edit, is none)
        _folder = string.IsNullOrWhiteSpace(kept) ? null : kept;
        // the logger's own folder is asked of Windows when the screen is first shown, off the window's thread (a Documents folder moved
        // to a share that doesn't answer never holds the window up)
        _findDefault = !automated && _folder is null;
        FollowBox.IsChecked = !automated && (_folder is not null || _findDefault);
        ShowSource();
    }

    /// <summary>Whether the logger's own folder is still to be found and followed (a person's run that remembers no folder).</summary>
    bool _findDefault;

    async void FollowDefault()
    {
        try
        {
            string? found = await Task.Run(() => ClipLog.DefaultFolder);
            if (!_findDefault) return;
            _findDefault = false;
            // a log opened or a folder chosen meanwhile stays
            if (_folder is not null || _reader is not null || _following) return;
            _folder = found;
            if (found is null) FollowBox.IsChecked = false;
            if (found is not null && FollowBox.IsChecked == true) Follow(found);
            else ShowSource();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while finding the folder of the clip logs");
            ShowError($"The logger's folder couldn't be found ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Whether the screen is in sight: its reads pause while it isn't (<see cref="Hidden"/>). A screen on its own (no window's
    /// tabs) is.</summary>
    bool _inSight = true;

    /// <summary>The screen is shown: a person's run starts following its folder the first time (nothing is read before); a log read
    /// before goes on, taking at once what was added while the screen was hidden.</summary>
    public void Shown()
    {
        _inSight = true;
        if (!_shownOnce)
        {
            _shownOnce = true;
            if (_findDefault && _reader is null && !_following) { FollowDefault(); return; }
            if (!_automated && FollowBox.IsChecked == true && _folder is not null && _reader is null && !_following) { Follow(_folder); return; }
        }
        if (_reader is not null || _following)
        {
            _timer.Start();
            Poll();
        }
    }

    /// <summary>Another screen is shown: the reads pause (no folder listed, no file opened) until this one is shown again.</summary>
    public void Hidden()
    {
        _inSight = false;
        _timer.Stop();
    }

    /// <summary>Takes the install the Library opened: every line's ID is looked up again in its clips, in the background.</summary>
    public void SetInstall(GameInstall g)
    {
        _install = g;
        List.Tag = null;
        Reresolve();
    }

    /// <summary>Takes the install's clip index: the readable name and who fill in on every line shown.</summary>
    public void SetIndex(GameInstall g, ClipIndex index)
    {
        if (ReferenceEquals(g, _install)) List.Tag = index;
    }

    // ---- the log: opened, or followed ----

    async void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a clip log of the GRB Clip Logger",
                Filter = "Clip logs (*.csv)|*.csv|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            string? at = _reader is { } r ? Path.GetDirectoryName(r.Path) : _folder;
            if (await ThereAsync(at)) dlg.InitialDirectory = at!;
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) OpenFile(dlg.FileName);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while opening a clip log");
            ShowError($"The log couldn't be opened ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Whether <paramref name="folder"/> is there, asked off the window's thread and given up after two seconds (a share that
    /// doesn't answer): for a dialog's first folder only.</summary>
    static async Task<bool> ThereAsync(string? folder)
    {
        if (folder is null) return false;
        var ask = Task.Run(() => Directory.Exists(folder));
        return await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(2))) == ask && ask.Result;
    }

    /// <summary>Opens a log (read only; kept read as it grows), in place of the one shown; following stops.</summary>
    internal void OpenFile(string path)
    {
        _following = false;
        FollowBox.IsChecked = false;
        Begin(new ClipLogReader(Path.GetFullPath(path)));
    }

    /// <summary>Follows the newest log in <paramref name="folder"/>, in place of the one shown. Nothing is made in the folder: one that
    /// isn't there is said, and looked for again at each read.</summary>
    internal void Follow(string folder)
    {
        _findDefault = false;
        try { _folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError($"The folder \"{folder}\" can't be followed: {ex.Message}");
            return;
        }
        _following = true;
        FollowBox.IsChecked = true;
        Begin(null);
    }

    /// <summary>The read error last shown for the log being read (shown once, not at each read).</summary>
    string? _errorShown;

    void Begin(ClipLogReader? reader)
    {
        _source++;
        _errorShown = null;
        HideError();
        Clear();
        _reader = reader;
        (_folderProblem, _readProblem, _restarted) = (null, null, false);
        // a wait on the log left ends (nothing more comes for it)
        _firstRead?.TrySetResult();
        _firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // the first read now, whether the screen is in sight or not (a log the command line names); the reads after it only while it is
        if (_inSight) _timer.Start();
        ShowSource();
        Poll();
    }

    void OnFollowToggled(object sender, RoutedEventArgs e)
    {
        if (FollowBox.IsChecked == true)
        {
            if (_folder is null) { FollowBox.IsChecked = false; ShowError("No folder to follow: choose one with Change folder."); return; }
            Follow(_folder);
        }
        else
        {
            // the log shown stays, read as it grows; a newer one is no longer switched to (a read already under way that found one is
            // dropped in Apply; every other read's lines still come)
            _following = false;
            ShowSource();
        }
    }

    async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder whose newest clip log is followed" };
            if (await ThereAsync(_folder)) dlg.InitialDirectory = _folder!;
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            UseFolder(dlg.FolderName);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while choosing the folder of the clip logs");
            ShowError($"The folder couldn't be chosen ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>A folder chosen: remembered in the settings, and followed.</summary>
    internal void UseFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        string? error = null;
        if (_settingsPath is { } p)
        {
            var protect = new List<string>();
            if (_install is { } g) protect.AddRange(Core.Export.ClipExport.ProtectedFolders(g));
            // a settings file that can't be read now isn't written over (the other settings in it would be lost)
            if (!ViewerSettings.TryLoad(p, out var kept, out var why))
                error = $"The folder couldn't be remembered for the next run: the settings file couldn't be read ({why}), so it was left as it is.";
            else
            {
                // guarded as every settings save is: never in the install's folders nor the game folder the settings name
                try { (kept with { LogFolder = folder }).Save(p, kept.GameFolder is { Length: > 0 } gf ? [.. protect, gf] : protect); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    error = $"The folder couldn't be remembered for the next run: {ex.Message}" + (why is null ? "" : $" {why}");
                }
                // a settings file that wasn't settings, kept aside before this save: said
                error ??= why;
            }
        }
        Follow(folder);
        // said after the follow starts (which clears the error strip)
        if (error is not null) ShowError(error);
    }

    /// <summary>For pictures and tests: the log the command line names opened, or its folder followed, and its first read waited for.
    /// Returns an error message, or null.</summary>
    public async Task<string?> StartForShotsAsync(ViewerArgs args)
    {
        if (args.LogFile is { } f) OpenFile(f);
        else if (args.LogFolder is { } d) Follow(d);
        else return null;
        await WaitForReadAsync();
        // a folder named that isn't there, can't be read or holds no log fails the run, as a log named that can't be read does
        if (args.LogFolder is { } named && args.LogFile is null && _reader is null)
            return _folderProblem == "missing" ? $"the log folder {_folder ?? named} isn't there"
                : _folderProblem is { } fp ? $"the log folder {_folder ?? named} can't be read: {fp}"
                : $"the log folder {_folder ?? named} holds no clip log";
        return _readProblem is { } p && _lines.Count == 0 && _notes.Count == 0 ? $"the log couldn't be read: {p}" : null;
    }

    /// <summary>Waits for the first read of the log shown to be on the screen.</summary>
    internal async Task WaitForReadAsync()
    {
        if (_firstRead is { } t) await t.Task;
        while (_polling is { } p) await p;
    }

    /// <summary>For a picture: the filter the shot asks for (its who part Player: the player's starts only; none: all); a search or a
    /// what part is refused (the screen has neither). Returns an error message, or null.</summary>
    public string? ShowForShot(ShotSpec shot)
    {
        if (shot.Who is { } who && who != ClipCategories.Player) return $"the Log screen's picture shows all characters or the player only, not {who}";
        if (shot.Search is { Length: > 0 } || shot.What is { Length: > 0 }) return "the Log screen's picture takes no search and no what part";
        ShowBox.SelectedIndex = shot.Who is null ? 0 : 1;
        return null;
    }

    /// <summary>Stops reading (the tests' screens, which outlive their test).</summary>
    internal void Stop()
    {
        _firstRead?.TrySetResult();
        _timer.Stop();
        _source++;
        _reader = null;
        _following = false;
    }

    /// <summary>How often the log is read again while it is shown.</summary>
    internal TimeSpan PollInterval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    // ---- reading, in the background ----

    /// <summary>What one read gave: the log read (another when the folder's newest changed), what it read, split into the starts and
    /// lines that don't read and the notes, with their counts, and the folder's state.</summary>
    sealed record PollResult(ClipLogReader? Reader, ClipLogRead? Read, bool Switched, string? FolderProblem, List<ClipLogLine> Lines, List<ClipLogLine> Notes,
        ClipLogCounts Counts, GameInstall? ResolvedWith);

    /// <summary>One read now, after the one running (for tests: the reads are otherwise the timer's).</summary>
    internal async Task PollAsync()
    {
        while (_polling is { } running) await running;
        Poll();
        if (_polling is { } mine) await mine;
    }

    /// <summary>Starts a read unless one is running; any error on the way said on the screen.</summary>
    void Poll()
    {
        if (_polling is not null) return;
        if (_reader is null && !_following) return;
        // kept only while it runs: a read that ended before its await has run its finally already (which may have started a newer read,
        // kept there), and a task kept after it would stop every read after it
        var t = PollOnceAsync();
        if (!t.IsCompleted) _polling = t;
    }

    /// <summary>For tests: the read done on the window's thread, ended before the await looks at it.</summary>
    internal bool ReadInlineForTests { get; set; }

    /// <summary>For tests: whether a read is running.</summary>
    internal bool Reading => _polling is not null;

    async Task PollOnceAsync()
    {
        int source = _source;
        var reader = _reader;
        string? folder = _following ? _folder : null;
        var g = _install;
        try
        {
            // (a report posted after the read ended is dropped: the bar goes with the read)
            var progress = new Progress<double>(p => { if (source == _source && _polling is not null) ShowLoading(p); });
            var r = await (ReadInlineForTests ? Task.FromResult(ReadOnce(reader, folder, g, progress)) : Task.Run(() => ReadOnce(reader, folder, g, progress)));
            if (source == _source)
                // a fault in putting the lines on the screen is the screen's, not the read's
                try { Apply(r); }
                catch (Exception ex)
                {
                    App.LogError(ex, "while showing the clip log's lines");
                    ShowError($"The log's lines couldn't be shown ({ex.GetType().Name}): {ex.Message}");
                }
        }
        catch (Exception ex)
        {
            if (source == _source)
            {
                // a read the tool foresees failing (a file or folder that can't be read now) is said with the log; any other is logged too
                App.LogError(ex, "while reading the clip log");
                _readProblem = $"{ex.GetType().Name}: {ex.Message}";
                // an error the tool didn't foresee is shown once for this log and this message, not again at each of the timer's reads
                string shown = $"The clip log couldn't be read ({ex.GetType().Name}): {ex.Message}";
                if (!App.Foreseen(ex) && shown != _errorShown)
                {
                    _errorShown = shown;
                    ShowError(shown);
                }
                ShowSource();
            }
        }
        finally
        {
            _polling = null;
            if (source == _source)
            {
                LoadPanel.Visibility = Visibility.Collapsed;
                _firstRead?.TrySetResult();
            }
            // another log was asked for while this read ran (its result dropped): its first read now, not at the timer's next turn
            else Poll();
        }
    }

    /// <summary>The read itself, off the window's thread: the folder's newest log (switched to when it is another than the one read),
    /// the lines it completed since the last read, their IDs looked up in the install's clips, split and counted.</summary>
    static PollResult ReadOnce(ClipLogReader? reader, string? folder, GameInstall? g, IProgress<double> progress)
    {
        string? folderProblem = null;
        bool switched = false;
        if (folder is not null)
        {
            string? newest = null;
            try
            {
                if (!Directory.Exists(folder)) folderProblem = "missing";
                else newest = ClipLogFolder.Newest(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { folderProblem = ex.Message; }
            if (newest is not null && (reader is null || !string.Equals(reader.Path, newest, StringComparison.OrdinalIgnoreCase)))
            {
                reader = new ClipLogReader(newest);
                switched = true;
            }
        }
        if (reader is null) return new PollResult(null, null, switched, folderProblem, [], [], new ClipLogCounts(), g);
        var read = reader.Read(progress);
        ClipLog.Resolve(read.Lines, g);
        var lines = new List<ClipLogLine>(read.Lines.Count);
        var notes = new List<ClipLogLine>();
        // the header is counted as a line, and shown as neither
        foreach (var l in read.Lines)
            if (l.Kind != ClipLogKind.Header) (l.Kind == ClipLogKind.Note ? notes : lines).Add(l);
        return new PollResult(reader, read, switched, folderProblem, lines, notes, ClipLogCounts.Of(read.Lines), g);
    }

    /// <summary>A read's lines onto the screen: added to the list (the list kept at its end when it was there and the folder is
    /// followed), or, for another log or one read again from its start, in place of what was shown.</summary>
    void Apply(PollResult r)
    {
        _folderProblem = r.FolderProblem;
        if (r.Reader is null || r.Read is null) { ShowSource(); return; }
        // a read that switched to a newer log after Follow was unticked: dropped (its reader is its own, so nothing is lost), unless no
        // log is shown yet (the folder's first read): that log stays shown
        if (r.Switched && !_following && _reader is not null) { ShowSource(); return; }
        bool fresh = r.Switched || r.Read.Restarted || !ReferenceEquals(r.Reader, _reader);
        if (fresh) Clear();
        if (r.Switched) _restarted = false;
        if (r.Read.Restarted) _restarted = true;
        _reader = r.Reader;
        _readProblem = r.Read.Problem;
        // a read that failed in a way the reader doesn't foresee: logged and shown once (the reader gives the error once, and doesn't
        // read that file again until it changes)
        if (r.Read.Error is { } failed)
        {
            App.LogError(failed, "while reading the clip log");
            ShowError($"The clip log couldn't be read ({failed.GetType().Name}): {failed.Message}");
        }
        bool first = _firstRead is { Task.IsCompleted: false };
        bool atEnd = _following && (first || fresh || ListAtEnd());
        _lines.AddRange(r.Lines);
        _headers += r.Read.Lines.Count - r.Lines.Count - r.Notes.Count;
        Merge(r.Counts);
        var kept = r.Lines.Where(Keeps).ToList();
        // a log's first read (opened, switched to, or read again from its start) makes the lists anew, once, with nothing to keep in them;
        // the lines a growing log adds go in where they are, so a list scrolled up or a line selected stays as it was
        // many lines at once (a first read, or what came while the screen was hidden) go in as one new list, not one change each; the line
        // selected and where the list was scrolled kept
        bool anew = fresh || first;
        if (kept.Count > 500)
        {
            var selected = anew ? null : List.SelectedItem;
            double offset = !anew && Find<ScrollViewer>(List) is { } was ? was.VerticalOffset : 0;
            _shown = new ObservableCollection<ClipLogLine>(_shown.Concat(kept));
            List.ItemsSource = _shown;
            if (selected is not null) List.SelectedItem = selected;
            if (offset > 0 && !atEnd && Find<ScrollViewer>(List) is { } sv) sv.ScrollToVerticalOffset(offset);
        }
        else foreach (var l in kept) _shown.Add(l);
        if (anew && r.Notes.Count > 500) { _notes = new ObservableCollection<ClipLogLine>(_notes.Concat(r.Notes)); NotesList.ItemsSource = _notes; }
        else foreach (var n in r.Notes) _notes.Add(n);
        // the list kept at its end, but held still while a line is pressed (the line under the pointer stays the one pressed), and taken
        // to its end when it is let go
        if (atEnd && _shown.Count > 0 && kept.Count > 0)
        {
            if (PressHeld) _endPending = true;
            else ShowEnd();
        }
        // a log followed shows its newest notes, as its newest lines; a log opened starts at its top
        if (_following && r.Notes.Count > 0 && _notes.Count > 0) NotesList.Dispatcher.BeginInvoke(ScrollNotesToEnd, DispatcherPriority.Background);
        // lines looked up in an install since replaced: looked up again in the new one
        if (!ReferenceEquals(r.ResolvedWith, _install) && r.Lines.Count > 0) Reresolve();
        ShowSource();
        UpdateCount();
    }

    void ScrollNotesToEnd()
    {
        if (Find<ScrollViewer>(NotesList) is { } sv) sv.ScrollToEnd();
    }

    void Merge(ClipLogCounts c)
    {
        _counts.Lines += c.Lines;
        _counts.Starts += c.Starts;
        _counts.PlayerStarts += c.PlayerStarts;
        _counts.Found += c.Found;
        _counts.NotFound += c.NotFound;
        _counts.Notes += c.Notes;
        _counts.Unreadable += c.Unreadable;
        _counts.ReachedLimit |= c.ReachedLimit;
        _counts.UnknownIds.UnionWith(c.UnknownIds);
    }

    /// <summary>The header lines read: counted as lines, kept in neither list.</summary>
    int _headers;

    /// <summary>How many times the lines were cleared: a lookup of the lines made before (<see cref="Reresolve"/>) is dropped.</summary>
    int _cleared;

    void Clear()
    {
        _cleared++;
        // a press, and the end it held back, belong to the lines shown before
        (_pressed, _endPending) = (null, false);
        _lines.Clear();
        _headers = 0;
        _shown = [];
        _notes = [];
        List.ItemsSource = _shown;
        NotesList.ItemsSource = _notes;
        _counts = new ClipLogCounts();
        UpdateCount();
    }

    /// <summary>Takes the list to its last row: its scroll viewer to its end at the next layout (or, before the list is laid out, the last
    /// row brought into view once it is).</summary>
    void ShowEnd()
    {
        if (_shown.Count == 0) return;
        // to the bottom only: the list stays where it was scrolled sideways (ScrollToEnd would take it back to its left edge too)
        if (Find<ScrollViewer>(List) is { } sv) sv.ScrollToBottom();
        else List.ScrollIntoView(_shown[^1]);
    }

    /// <summary>Whether the list is scrolled to its last row (or not laid out yet).</summary>
    bool ListAtEnd() => Find<ScrollViewer>(List) is not { } sv || sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 1;

    static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (VisualTreeHelper.GetChild(d, i) is var c && (c as T ?? Find<T>(c)) is { } t) return t;
        return null;
    }

    /// <summary>The lines looked up again in the install open, in the background; the rows and the counts follow.</summary>
    int _resolveRun;

    async void Reresolve()
    {
        int run = ++_resolveRun;
        int source = _source, cleared = _cleared;
        var g = _install;
        var lines = _lines.ToArray();
        var notes = _notes.ToArray();
        try
        {
            var (found, counts) = await Task.Run(() =>
            {
                var f = new ClipEntry?[lines.Length];
                var c = new ClipLogCounts();
                for (int i = 0; i < lines.Length; i++)
                {
                    f[i] = lines[i].Kind == ClipLogKind.Start && g is not null ? g.FindClip(lines[i].ClipId) : null;
                    c.Add(lines[i], LookupOf(lines[i], f[i]));
                }
                foreach (var n in notes) c.Add(n);
                return (f, c);
            });
            // another log, or this one read again, since the lines were taken: its own lines were looked up as they came
            if (run != _resolveRun || source != _source || cleared != _cleared) return;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Kind != ClipLogKind.Start) continue;
                lines[i].Clip = found[i];
                lines[i].Lookup = LookupOf(lines[i], found[i]);
            }
            // the lines added meanwhile were looked up in this install already (or look it up again themselves)
            for (int i = lines.Length; i < _lines.Count; i++) counts.Add(_lines[i]);
            for (int i = notes.Length; i < _notes.Count; i++) counts.Add(_notes[i]);
            counts.Lines += _headers;
            _counts = counts;
            // the rows shown take their new names (the same rows, so the list stays where it is)
            List.Items.Refresh();
            UpdateCount();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while looking the clip log's IDs up in the game files");
            ShowError($"The log's clip IDs couldn't be looked up in the game files ({ex.GetType().Name}): {ex.Message}");
        }

        ClipLookup LookupOf(ClipLogLine l, ClipEntry? c) =>
            l.Kind != ClipLogKind.Start || g is null ? ClipLookup.NotLooked : c is null ? ClipLookup.NotFound : ClipLookup.Found;
    }

    // ---- the filter, the lines and their clicks ----

    bool _playerOnly;

    bool Keeps(ClipLogLine l) => !_playerOnly || l.Kind == ClipLogKind.Start && l.Player;

    void OnShowChanged(object sender, SelectionChangedEventArgs e)
    {
        bool player = ShowBox.SelectedIndex == 1;
        if (player == _playerOnly || List is null) return;
        _playerOnly = player;
        var selected = List.SelectedItem;
        _shown = new ObservableCollection<ClipLogLine>(_lines.Where(Keeps));
        List.ItemsSource = _shown;
        if (selected is ClipLogLine l && Keeps(l)) { List.SelectedItem = l; List.ScrollIntoView(l); }
        UpdateCount();
    }

    /// <summary>The line pressed with the left button, and when: the list holds still while it is (<see cref="PressHeld"/>), and only that
    /// line opens when the button is let go on it.</summary>
    ClipLogLine? _pressed;
    DateTime _pressedAt;

    /// <summary>Whether the list's end was to be shown while a line was pressed (shown when it is let go).</summary>
    bool _endPending;

    /// <summary>How long a press holds the list still at most: a button let go elsewhere (outside the window, say) holds it no longer.</summary>
    static readonly TimeSpan MostPress = TimeSpan.FromSeconds(10);

    bool PressHeld => _pressed is not null && DateTime.UtcNow - _pressedAt < MostPress;

    /// <summary>The line of the row an input event came from, or null (a header, the scroll bar, the list's empty foot).</summary>
    ClipLogLine? LineAt(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != List; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListViewItem { DataContext: ClipLogLine l }) return l;
        return null;
    }

    void OnLinePressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pressed = LineAt(e.OriginalSource);
        _pressedAt = DateTime.UtcNow;
    }

    void OnLineReleased(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pressed = PressHeld ? _pressed : null;
        var released = LineAt(e.OriginalSource);
        _pressed = null;
        if (_endPending)
        {
            _endPending = false;
            // the end held back is shown only while the log is still followed
            if (_following) ShowEnd();
        }
        // the line opened is the one pressed, and only when the button is let go on that same line
        if (pressed is not null && ReferenceEquals(pressed, released)) Open(pressed);
    }

    /// <summary>A line clicked: its clip shown in the Library, when the ID is one of the install's clips (a note, an unknown ID or a line
    /// that doesn't read opens nothing).</summary>
    internal void Open(ClipLogLine l)
    {
        if (!l.Opens) return;
        try { ClipOpened?.Invoke(l.Clip!); }
        catch (Exception ex)
        {
            App.LogError(ex, $"while showing {l.Clip!.Name} from the clip log");
            ShowError($"{l.Clip.Name} couldn't be shown ({ex.GetType().Name}): {ex.Message}");
        }
    }

    // ---- what the screen says ----

    void ShowLoading(double p)
    {
        LoadPanel.Visibility = Visibility.Visible;
        LoadBar.Value = p;
        LoadText.Text = FormattableString.Invariant($"Reading the log, {p * 100:F0}%");
    }

    /// <summary>Which log is shown and how it stands: the file followed and its folder, a folder that isn't there (said plainly; the tool
    /// makes nothing there), a folder with no log yet, a log that can't be read now or was read again from its start.</summary>
    void ShowSource()
    {
        string? folder = _folder;
        string text;
        if (_following && folder is not null)
        {
            text = _folderProblem == "missing"
                ? $"The folder {folder} isn't there. The clip logger makes it the first time its entry is ticked; the tool creates nothing there and keeps checking for it."
                : _folderProblem is { } why ? $"The folder {folder} can't be read: {why}"
                : _reader is { } r ? $"Following {Path.GetFileName(r.Path)}, the newest log in the folder, as the game writes it."
                : $"No clip log in {folder} yet: the newest is followed as soon as the logger starts one.";
        }
        else if (_reader is { } r) text = $"Log: {r.Path}" + (_folder is null ? "" : "  (not following the folder)");
        else text = "No log open: open one, or follow the newest in the logger's folder.";
        // (a followed folder's first read that failed has no log yet, and says so too)
        if ((_reader is not null || _following) && _readProblem is { } p) text += $" The log can't be read now: {p}.";
        if (_restarted) text += " The file was replaced or cut short, so it was read again from its start.";
        SourceText.Text = text;
        SourceText.ToolTip = _reader is { } shown ? $"{text}\n{shown.Path}" : text;
        FolderText.Text = folder is not null ? $"Folder: {folder}"
            : _findDefault ? $"Folder: the logger's own ({ClipLog.FolderName} in the Documents folder)" : "Folder: none (choose one with Change folder)";
        FolderText.ToolTip = FolderText.Text;
    }

    void UpdateCount()
    {
        var c = _counts;
        string unknown = c.NotFound == 0 ? "" : $", {c.NotFound:N0} of an unknown clip ({c.UnknownIds.Count:N0} ID{(c.UnknownIds.Count == 1 ? "" : "s")})";
        string named = _install is null ? " (open the game folder in the Library to name their clips)" : $": {c.Found:N0} named{unknown}";
        CountText.Text = c.Lines == 0 ? (_reader is null ? "" : "The log holds no lines yet.")
            : $"{_shown.Count:N0} shown · {c.Starts:N0} clip starts, {c.PlayerStarts:N0} of them the player's{named} · {c.Notes:N0} notes"
              + (c.Unreadable > 0 ? $" · {c.Unreadable:N0} unreadable line{(c.Unreadable == 1 ? "" : "s")}" : "") + $" · {c.Lines:N0} lines";
        LimitText.Text = c.ReachedLimit ? "This log reached the logger's 32 MB limit: the starts after it were counted, not written (the logger's summary at its end says how many)." : "";
        LimitText.Visibility = c.ReachedLimit ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- for tests ----

    /// <summary>Shows these lines as a read of a log would (for the theme's tests: no file).</summary>
    internal void ShowLinesForTests(IReadOnlyList<ClipLogLine> lines)
    {
        Clear();
        _lines.AddRange(lines.Where(l => l.Kind is ClipLogKind.Start or ClipLogKind.Unreadable));
        Merge(ClipLogCounts.Of(lines));
        _shown = new ObservableCollection<ClipLogLine>(_lines.Where(Keeps));
        _notes = new ObservableCollection<ClipLogLine>(lines.Where(l => l.Kind == ClipLogKind.Note));
        List.ItemsSource = _shown;
        NotesList.ItemsSource = _notes;
        UpdateCount();
    }

    internal IReadOnlyList<ClipLogLine> Lines => _lines;
    internal IReadOnlyList<ClipLogLine> ShownLines => _shown;
    internal IReadOnlyList<ClipLogLine> NoteLines => _notes;
    internal ClipLogCounts Counts => _counts;
    internal string? Followed => _following ? _reader?.Path : null;

    // ---- errors ----

    /// <summary>Shows a message in the screen's error strip.</summary>
    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    void OnDismissError(object sender, RoutedEventArgs e) => HideError();
}
