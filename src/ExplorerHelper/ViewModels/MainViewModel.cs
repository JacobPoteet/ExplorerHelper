using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExplorerHelper.Models;
using ExplorerHelper.Services;

namespace ExplorerHelper.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<FileEntry> Files { get; } = [];

    /// <summary>Names applied during this session, most-recent first — the quick-rename palette.</summary>
    public ObservableCollection<string> RecentNames { get; } = [];

    private const int MaxRecentNames = 12;

    // --- Quick-name preset buttons (issue #14) ----------------------------------------
    // Persisted preset strings the user can drop into the rename box in one click, plus the
    // two date formats used by the dynamic "today" / "created" buttons. All live in AppSettings.

    private readonly AppSettings _settings;

    /// <summary>User-defined preset strings shown as one-click buttons under the rename box.</summary>
    public ObservableCollection<string> QuickNameButtons { get; } = [];

    // --- Preview details (issue #20) --------------------------------------------------
    // The Settings toggles pick which detail rows appear under the preview; PreviewDetails is
    // the derived list the panel binds to, rebuilt whenever the selection, its metadata, or the
    // toggles change. Media metadata (resolution/length/frame rate/bit rate) is read off the
    // Windows shell on a background thread and merged in when it arrives.

    /// <summary>Per-detail on/off switches shown in Settings, in display order.</summary>
    public ObservableCollection<PreviewDetailToggle> PreviewDetailToggles { get; } = [];

    /// <summary>The label/value rows shown under the preview for the current selection.</summary>
    public ObservableCollection<PreviewDetailRow> PreviewDetails { get; } = [];

    private ShellPropertyService.MediaProperties? _currentMedia;
    private CancellationTokenSource? _detailsCts;

    /// <summary>.NET custom date format for the "today's date" dynamic button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayFormatPreview))]
    private string _todayDateFormat = "yyyy-MM-dd";

    /// <summary>.NET custom date format for the "file created date" dynamic button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatedFormatPreview))]
    private string _createdDateFormat = "yyyy-MM-dd";

    /// <summary>Live sample of the today-date format, shown next to the settings field.</summary>
    public string TodayFormatPreview => FormatDate(DateTime.Now, TodayDateFormat);

    /// <summary>Live sample of the created-date format (uses now as the sample date).</summary>
    public string CreatedFormatPreview => FormatDate(DateTime.Now, CreatedDateFormat);

    // --- Self-update ------------------------------------------------------------------
    // On startup (when enabled) the latest GitHub release is checked in the background; a
    // newer version surfaces as a toolbar pill. Clicking it downloads the installer and
    // hands off to a silent install that relaunches the app in the same folder.

    /// <summary>Toolbar pill text: "v0.5.0 available", then download progress.</summary>
    [ObservableProperty]
    private string? _updateBadgeText;

    /// <summary>True once a newer release is known; shows the update pill.</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    /// <summary>Whether to check GitHub for a newer release on startup (Settings toggle).</summary>
    [ObservableProperty]
    private bool _checkForUpdates = true;

    private UpdateInfo? _availableUpdate;
    private bool _updateInProgress;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        foreach (var name in _settings.QuickNameButtons)
            QuickNameButtons.Add(name);
        TodayDateFormat = _settings.TodayDateFormat;
        CreatedDateFormat = _settings.CreatedDateFormat;
        CheckForUpdates = _settings.CheckForUpdates;
        if (CheckForUpdates)
            _ = CheckForUpdatesInBackgroundAsync();

        // Null (never configured) → defaults; an explicit empty list means the user hid them all.
        var enabledDetails = _settings.EnabledPreviewDetails is { } saved
            ? new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(PreviewDetailKinds.DefaultEnabled, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, label) in PreviewDetailKinds.All)
        {
            var toggle = new PreviewDetailToggle(key, label, enabledDetails.Contains(key));
            toggle.PropertyChanged += OnPreviewDetailToggleChanged;
            PreviewDetailToggles.Add(toggle);
        }
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _settings.Save();
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        var info = await UpdateService.CheckForUpdateAsync();
        if (info is null)
            return;
        _availableUpdate = info;
        UpdateBadgeText = $"v{info.Version.ToString(3)} available";
        IsUpdateAvailable = true;
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (_availableUpdate is null || _updateInProgress)
            return;

        // Portable copies can't be swapped out from under themselves safely — send those
        // to the release page; installed copies get the seamless silent update.
        if (!UpdateService.IsInstalledCopy())
        {
            Process.Start(new ProcessStartInfo(_availableUpdate.ReleasePageUrl) { UseShellExecute = true });
            return;
        }

        _updateInProgress = true;
        try
        {
            var progress = new Progress<double>(p => UpdateBadgeText = $"Downloading… {p:P0}");
            var installer = await UpdateService.DownloadInstallerAsync(_availableUpdate, progress);
            UpdateBadgeText = "Restarting…";
            UpdateService.ApplyUpdate(installer, Directory.Exists(FolderPath) ? FolderPath : null);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateBadgeText = $"v{_availableUpdate.Version.ToString(3)} available";
            StatusText = $"Update failed — {ex.Message}";
            _updateInProgress = false;
        }
    }

    partial void OnSelectedFileChanged(FileEntry? oldValue, FileEntry? newValue)
    {
        // A folder's size and item count land seconds after the selection does, so follow the
        // selected entry's own notifications to keep the details rows current (issue #40).
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedEntryPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedEntryPropertyChanged;

        // New selection: drop stale media metadata, show what we know instantly, fetch the rest.
        _currentMedia = null;
        RebuildPreviewDetails();
        LoadMediaPropertiesInBackground(newValue);
    }

    /// <summary>
    /// Refreshes the details rows when the selected folder's scan reports a number. The scan runs
    /// on a worker, so this hops to the UI thread before touching the bound collection.
    /// </summary>
    private void OnSelectedEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FileEntry.SizeDisplay) or nameof(FileEntry.ItemsDisplay)))
            return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            RebuildPreviewDetails();
        else
            dispatcher.BeginInvoke(RebuildPreviewDetails);
    }

    private void OnPreviewDetailToggleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewDetailToggle.IsChecked))
            return;
        _settings.EnabledPreviewDetails = PreviewDetailToggles.Where(t => t.IsChecked).Select(t => t.Key).ToList();
        _settings.Save();
        RebuildPreviewDetails();
    }

    /// <summary>Rebuilds the visible detail rows from the current selection, its cached media
    /// metadata, and the enabled toggles — skipping any detail with no value for this file.</summary>
    private void RebuildPreviewDetails()
    {
        PreviewDetails.Clear();
        if (SelectedFile is not { } entry)
            return;
        foreach (var toggle in PreviewDetailToggles)
        {
            if (!toggle.IsChecked)
                continue;
            var value = FormatDetail(toggle.Key, entry, _currentMedia);
            if (!string.IsNullOrEmpty(value))
                PreviewDetails.Add(new PreviewDetailRow(toggle.Label, value));
        }
    }

    private void LoadMediaPropertiesInBackground(FileEntry? entry)
    {
        _detailsCts?.Cancel();
        if (entry is null || entry.IsDirectory)
            return;

        var cts = new CancellationTokenSource();
        _detailsCts = cts;
        var path = entry.FullPath;
        var token = cts.Token;

        Task.Run(() =>
        {
            var media = ShellPropertyService.Read(path);
            if (token.IsCancellationRequested)
                return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.Invoke(() =>
            {
                // Ignore late results for a selection the user has already moved off of.
                if (token.IsCancellationRequested || SelectedFile?.FullPath != path)
                    return;
                _currentMedia = media;
                RebuildPreviewDetails();
            });
        }, token);
    }

    private static string? FormatDetail(string key, FileEntry entry, ShellPropertyService.MediaProperties? media) => key switch
    {
        PreviewDetailKinds.Type => entry.TypeDisplay,
        PreviewDetailKinds.Size => entry.SizeDisplay,
        PreviewDetailKinds.Items => entry.ItemsDisplay,
        PreviewDetailKinds.Dimensions => media?.Dimensions is { } d ? $"{d.Width} × {d.Height}" : null,
        PreviewDetailKinds.Duration => media?.Duration is { } t ? FormatDuration(t) : null,
        PreviewDetailKinds.FrameRate => media?.FrameRate is { } f ? $"{f:0.##} fps" : null,
        PreviewDetailKinds.Bitrate => media?.Bitrate is { } b ? FormatBitrate(b) : null,
        PreviewDetailKinds.Created => entry.Created.ToString("yyyy-MM-dd HH:mm"),
        PreviewDetailKinds.Modified => entry.ModifiedDisplay,
        _ => null,
    };

    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes}:{t.Seconds:00}";

    private static string FormatBitrate(ulong bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? $"{bitsPerSecond / 1_000_000.0:0.#} Mbps"
        : $"{bitsPerSecond / 1000.0:0} kbps";

    partial void OnTodayDateFormatChanged(string value)
    {
        _settings.TodayDateFormat = value;
        _settings.Save();
    }

    partial void OnCreatedDateFormatChanged(string value)
    {
        _settings.CreatedDateFormat = value;
        _settings.Save();
    }

    /// <summary>Adds a preset button (trimmed, case-insensitive de-dupe) and persists it.</summary>
    public void AddQuickButton(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        if (QuickNameButtons.Any(b => string.Equals(b, text, StringComparison.OrdinalIgnoreCase)))
            return;
        QuickNameButtons.Add(text);
        PersistQuickButtons();
    }

    /// <summary>Removes a preset button and persists the change.</summary>
    public void RemoveQuickButton(string text)
    {
        if (QuickNameButtons.Remove(text))
            PersistQuickButtons();
    }

    private void PersistQuickButtons()
    {
        _settings.QuickNameButtons = [.. QuickNameButtons];
        _settings.Save();
    }

    /// <summary>
    /// Formats a date with a user-supplied .NET custom format string, degrading gracefully:
    /// an invalid format never throws — it just falls back to a sensible default so a typo in
    /// settings can't crash the rename bar.
    /// </summary>
    public static string FormatDate(DateTime date, string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return date.ToString("yyyy-MM-dd");
        try
        {
            return date.ToString(format);
        }
        catch (FormatException)
        {
            return date.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>The distinct file types present in the folder, each toggleable on/off (issue #5).</summary>
    public ObservableCollection<TypeFilter> TypeFilters { get; } = [];

    // --- Triage state ------------------------------------------------------------------
    // Flags live on the entries; these piles/counts are derived views kept in sync by
    // RecomputeTriage so the deck header, review screen, and status bar can bind to them.

    /// <summary>
    /// Every mark made this session, keyed by path so it survives navigating to another folder
    /// (issue #43). The piles below are derived views onto it.
    /// </summary>
    private readonly TriageSession _triage = new();

    /// <summary>Files currently flagged Keep, folder then name.</summary>
    public ObservableCollection<FileEntry> KeepPile { get; } = [];

    /// <summary>Files currently flagged Reject, folder then name.</summary>
    public ObservableCollection<FileEntry> RejectPile { get; } = [];

    /// <summary>Toolbar text while marks are pending, e.g. "37 marks in 5 folders".</summary>
    [ObservableProperty]
    private string _pendingMarksSummary = string.Empty;

    /// <summary>
    /// True while uncommitted marks exist, showing the toolbar pill. Marks now accumulate across
    /// folders, so something has to stay visible while you browse or you can commit a reject you
    /// made six folders ago and forgot about.
    /// </summary>
    [ObservableProperty]
    private bool _hasPendingMarks;

    /// <summary>True once marks span more than one folder, which is when scope starts to matter.</summary>
    [ObservableProperty]
    private bool _marksSpanFolders;

    /// <summary>Keep/reject totals for one folder, shown in the commit dialog's breakdown.</summary>
    public sealed record TriageFolderSummary(string Folder, string Display, int KeepCount, int RejectCount);

    /// <summary>Pending marks grouped by the folder they were made in, for the commit dialog.</summary>
    public List<TriageFolderSummary> SummarizeMarksByFolder() => _triage.Marked
        .GroupBy(TriageSession.FolderOf, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new TriageFolderSummary(
            g.Key,
            string.Equals(g.Key, FolderPath, StringComparison.OrdinalIgnoreCase)
                ? $"{Path.GetFileName(g.Key)} (current folder)"
                : g.Key,
            g.Count(e => e.Flag == TriageFlag.Keep),
            g.Count(e => e.Flag == TriageFlag.Reject)))
        .ToList();

    [ObservableProperty]
    private int _keepCount;

    [ObservableProperty]
    private int _rejectCount;

    /// <summary>Files (not folders) with no flag yet — the "still to triage" number.</summary>
    [ObservableProperty]
    private int _unmarkedFileCount;

    [ObservableProperty]
    private string _keepPileSummary = string.Empty;

    [ObservableProperty]
    private string _rejectPileSummary = string.Empty;

    // Set while toggling many type filters at once so ApplyView runs once, not per item.
    private bool _suspendApplyView;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private FileEntry? _selectedFile;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _sortMode = "Name";

    /// <summary>Sort direction of the active column. Toggled by clicking the same header again.</summary>
    public bool SortDescending { get; private set; }

    [ObservableProperty]
    private string _statusText = "No folder loaded";

    /// <summary>
    /// LIFO journal of reversible actions — renames and deletes (issue #9). Each entry knows how
    /// to undo itself on disk; <see cref="Undo"/> reloads the folder afterwards so the list always
    /// matches reality. <see cref="CanUndo"/> drives the toolbar button and Ctrl+Z.
    /// </summary>
    private readonly Stack<UndoOperation> _undoStack = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    private sealed record UndoOperation(string Label, Func<string?> Reverse);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMenuButtonText))]
    private bool _contextMenuRegistered = ContextMenuRegistrar.IsRegistered;

    public string ContextMenuButtonText =>
        ContextMenuRegistered ? "Remove context menu" : "Add context menu";

    private List<FileEntry> _allEntries = [];
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _folderStatsCts;

    partial void OnFilterTextChanged(string value) => ApplyView();

    /// <summary>
    /// Sorts by the given column key. Clicking the active column again reverses direction;
    /// switching to a different column starts ascending.
    /// </summary>
    public void SortBy(string key)
    {
        if (SortMode == key)
            SortDescending = !SortDescending;
        else
        {
            SortMode = key;
            SortDescending = false;
        }
        ApplyView();
    }

    // --- Navigation (issue #41) --------------------------------------------------------
    // Entering a subfolder used to mean handing the path to explorer.exe and leaving the app.
    // These two stacks are the browser model: NavigateTo pushes where you were, Back and Forward
    // move between them without disturbing each other's history.

    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();

    /// <summary>Last selected item per folder, so Up then Enter lands where you left off.</summary>
    private readonly Dictionary<string, string> _lastSelectedByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One clickable path segment in the breadcrumb bar.</summary>
    public sealed record BreadcrumbSegment(string Label, string Path);

    /// <summary>The current folder split into clickable segments, root first.</summary>
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateForwardCommand))]
    private bool _canGoForward;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateUpCommand))]
    private bool _canGoUp;

    /// <summary>
    /// Raised just before the folder switches, so the view can drop preview file handles first.
    /// A live MediaElement or WebView2 holds the outgoing file open (issue #1).
    /// </summary>
    public event EventHandler? FolderChanging;

    /// <summary>Enters a folder, remembering the current one so Back returns to it.</summary>
    public void NavigateTo(string path)
    {
        if (!Directory.Exists(path) || string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase))
            return;
        FolderChanging?.Invoke(this, EventArgs.Empty);
        RememberSelection();
        if (!string.IsNullOrEmpty(FolderPath))
            _backStack.Push(FolderPath);
        // A new destination ends the forward trail, the same way a browser does it.
        _forwardStack.Clear();
        LoadFolder(path);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void NavigateBack() => Step(_backStack, _forwardStack);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NavigateForward() => Step(_forwardStack, _backStack);

    /// <summary>Moves one entry off <paramref name="from"/>, pushing the current folder onto the other.</summary>
    private void Step(Stack<string> from, Stack<string> to)
    {
        if (from.Count == 0)
            return;
        FolderChanging?.Invoke(this, EventArgs.Empty);
        RememberSelection();
        var target = from.Pop();
        if (!string.IsNullOrEmpty(FolderPath))
            to.Push(FolderPath);
        // A folder deleted while it sat in the history would otherwise leave the view stranded.
        if (Directory.Exists(target))
            LoadFolder(target);
        else
            UpdateNavigationState();
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void NavigateUp()
    {
        if (ParentFolder is { } parent)
            NavigateTo(parent);
    }

    /// <summary>The containing folder, or null at a drive root.</summary>
    private string? ParentFolder =>
        string.IsNullOrEmpty(FolderPath) ? null : Path.GetDirectoryName(FolderPath);

    private void RememberSelection()
    {
        if (!string.IsNullOrEmpty(FolderPath) && SelectedFile is { } selected)
            _lastSelectedByFolder[FolderPath] = selected.FullPath;
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
        CanGoUp = ParentFolder is { Length: > 0 };
    }

    /// <summary>Rebuilds the breadcrumb from the current path, root segment first.</summary>
    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (string.IsNullOrEmpty(FolderPath))
            return;

        var segments = new List<BreadcrumbSegment>();
        var current = FolderPath;
        while (!string.IsNullOrEmpty(current))
        {
            // A drive or UNC root has no file name of its own, so it labels itself ("C:\").
            var label = Path.GetFileName(current);
            segments.Add(new BreadcrumbSegment(
                string.IsNullOrEmpty(label) ? current : label, current));
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        segments.Reverse();
        foreach (var segment in segments)
            Breadcrumbs.Add(segment);
    }

    public void LoadFolder(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Refresh and Undo reload the folder you're already in; only an actual move should
        // restore a remembered selection or reset where the list is scrolled.
        var enteringNewFolder = !string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase);

        FolderPath = path;

        var dir = new DirectoryInfo(path);
        _allEntries = dir.EnumerateDirectories()
            .Cast<FileSystemInfo>()
            .Concat(dir.EnumerateFiles())
            .Select(info => new FileEntry(info))
            .ToList();

        // Marks are keyed by path and live in the session, so they survive a reload *and* a trip
        // through other folders. Rebinding also swaps the piles onto these fresh entry objects.
        _triage.Rebind(_allEntries);

        BuildTypeFilters();
        RecomputeTriage();
        ApplyView();
        BuildBreadcrumbs();
        UpdateNavigationState();

        if (enteringNewFolder)
            RestoreRememberedSelection(path);

        LoadFolderSizesInBackground();
        LoadThumbnailsInBackground();
    }

    /// <summary>
    /// Re-selects whatever was selected the last time this folder was open, so stepping Up and
    /// back down doesn't dump you at the top of the list. Falls back to the first row.
    /// </summary>
    private void RestoreRememberedSelection(string path)
    {
        if (_lastSelectedByFolder.TryGetValue(path, out var remembered))
        {
            SelectedFile = Files.FirstOrDefault(
                f => string.Equals(f.FullPath, remembered, StringComparison.OrdinalIgnoreCase));
        }
        SelectedFile ??= Files.FirstOrDefault();
    }

    [RelayCommand]
    private void Refresh()
    {
        // Refresh means "re-read the disk", so cached subtree totals go too (issue #40).
        FolderScanService.ClearCache();
        if (!string.IsNullOrEmpty(FolderPath))
            LoadFolder(FolderPath);
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (Directory.Exists(FolderPath))
            Process.Start("explorer.exe", $"\"{FolderPath}\"");
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedFile is null)
            return;
        Process.Start(new ProcessStartInfo(SelectedFile.FullPath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ToggleContextMenu()
    {
        if (ContextMenuRegistered)
        {
            ContextMenuRegistrar.Unregister();
        }
        else
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
                return;
            ContextMenuRegistrar.Register(exe);
        }
        ContextMenuRegistered = ContextMenuRegistrar.IsRegistered;
    }

    /// <summary>Moves the given entries to the Recycle Bin and removes them from the list.</summary>
    public void Delete(IReadOnlyList<FileEntry> entries)
    {
        // Remember where each item landed in the Recycle Bin so undo can put it back (issue #9).
        var restorable = new List<(string Original, string Recycled)>();
        foreach (var entry in entries)
        {
            var result = RecycleBinService.MoveToRecycleBin(entry.FullPath);
            if (!result.Deleted)
                continue; // still on disk — leave the row alone

            // Gone from the folder, so the row goes too. The bin path is what undo needs, and the
            // shell doesn't always report it; without one the file is simply not undoable (issue #32).
            if (result.RecycledPath is { } recycled)
                restorable.Add((entry.FullPath, recycled));
            FolderScanService.Invalidate(entry.FullPath);
            // A deleted file can't be committed later, so its mark goes with it.
            _triage.Forget(entry.FullPath);
            Files.Remove(entry);
            _allEntries.Remove(entry);
        }

        if (restorable.Count > 0)
        {
            var label = restorable.Count == 1
                ? $"delete “{Path.GetFileName(restorable[0].Original)}”"
                : $"delete {restorable.Count} items";
            PushUndo(label, () => ReverseDelete(restorable));
        }
        RecomputeTriage(); // deleted entries drop out of the piles; also refreshes the status bar
    }

    // --- Triage (flag then commit) ----------------------------------------------------

    /// <summary>
    /// Flags a file for triage. Folders are ignored — the deck and the commit only ever
    /// touch files, so a folder can never be flagged into the reject pile.
    /// </summary>
    public void SetFlag(FileEntry entry, TriageFlag flag)
    {
        if (entry.IsDirectory || entry.Flag == flag)
            return;
        _triage.Set(entry, flag);
        RecomputeTriage();
    }

    /// <summary>Discards every triage mark, in every folder (used when exiting without committing).</summary>
    public void ClearAllFlags()
    {
        _triage.Clear();
        RecomputeTriage();
    }

    /// <summary>Rebuilds the derived piles and counts from the session's marks.</summary>
    private void RecomputeTriage()
    {
        KeepPile.Clear();
        RejectPile.Clear();
        long keepBytes = 0, rejectBytes = 0;

        foreach (var entry in _triage.Pending(TriageFlag.Keep))
        {
            KeepPile.Add(entry);
            keepBytes += entry.SizeBytes;
        }
        foreach (var entry in _triage.Pending(TriageFlag.Reject))
        {
            RejectPile.Add(entry);
            rejectBytes += entry.SizeBytes;
        }

        // "Still to decide" stays a question about the folder in front of you, not the whole session.
        var unmarked = _allEntries.Count(e => !e.IsDirectory && e.Flag == TriageFlag.None);

        KeepCount = KeepPile.Count;
        RejectCount = RejectPile.Count;
        UnmarkedFileCount = unmarked;
        KeepPileSummary = $"{KeepCount} · {FileEntry.FormatSize(keepBytes)}";
        RejectPileSummary = $"{RejectCount} · {FileEntry.FormatSize(rejectBytes)}";

        var total = KeepCount + RejectCount;
        var folders = _triage.FolderCount;
        HasPendingMarks = total > 0;
        MarksSpanFolders = folders > 1;
        PendingMarksSummary = folders > 1
            ? $"{total} mark{(total == 1 ? "" : "s")} in {folders} folders"
            : $"{total} mark{(total == 1 ? "" : "s")}";
        UpdatePileGrouping();
        UpdateStatus();
    }

    /// <summary>
    /// Groups the review piles by folder once marks span more than one, and drops the grouping
    /// again when they don't - a lone "Photos" header over every item is noise in the common case.
    /// </summary>
    private void UpdatePileGrouping()
    {
        Apply(System.Windows.Data.CollectionViewSource.GetDefaultView(KeepPile));
        Apply(System.Windows.Data.CollectionViewSource.GetDefaultView(RejectPile));

        void Apply(System.ComponentModel.ICollectionView view)
        {
            if (view.GroupDescriptions is not { } groups)
                return;
            if (MarksSpanFolders && groups.Count == 0)
                groups.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileEntry.FolderName)));
            else if (!MarksSpanFolders && groups.Count > 0)
                groups.Clear();
        }
    }

    /// <summary>
    /// Applies the triage decisions to disk in one shot: rejects go to the Recycle Bin and —
    /// when <paramref name="keepDestination"/> is set — keepers move there (collisions
    /// auto-number). Pushes a single undo entry that reverses the whole commit. Returns an
    /// error summary, or null when every file was processed.
    /// </summary>
    public string? CommitTriage(
        string? keepDestination,
        bool copyKeepers,
        bool deleteRejects,
        bool currentFolderOnly = false)
    {
        // Marks come from the session, not the current folder, so a commit can span everything
        // decided this run (issue #43). currentFolderOnly narrows it back to what's on screen.
        var scope = currentFolderOnly ? FolderPath : null;
        var rejects = _triage.Pending(TriageFlag.Reject, scope);
        var keeps = _triage.Pending(TriageFlag.Keep, scope);

        var failures = 0;
        var committed = new List<string>();

        var recycled = new List<(string Original, string Recycled)>();
        var rejectsDeleted = 0; // includes files the shell deleted without reporting a bin path
        if (deleteRejects)
        {
            foreach (var entry in rejects)
            {
                // MoveToRecycleBin never throws, so one unreachable file is a counted failure
                // rather than an abort that would skip PushUndo and strand everything already
                // recycled in this loop with no way back (issue #32).
                var result = RecycleBinService.MoveToRecycleBin(entry.FullPath);
                if (!result.Deleted)
                {
                    failures++;
                    continue;
                }

                rejectsDeleted++;
                committed.Add(entry.FullPath);
                if (result.RecycledPath is { } binPath)
                    recycled.Add((entry.FullPath, binPath));
            }
        }

        var moved = new List<(string From, string To)>();
        var copied = new List<string>();
        if (!string.IsNullOrWhiteSpace(keepDestination))
        {
            // One directory read up front instead of two existence checks per collision per file —
            // moving hundreds of same-stem keepers was quadratic in syscalls, all on the UI thread
            // with the triage overlay still up (issue #35).
            var takenNames = Directory.Exists(keepDestination)
                ? new HashSet<string>(
                    new DirectoryInfo(keepDestination).EnumerateFileSystemInfos().Select(i => i.Name),
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in keeps)
            {
                var stem = Path.GetFileNameWithoutExtension(entry.FullPath);
                var ext = Path.GetExtension(entry.FullPath);
                var target = Path.Combine(
                    keepDestination,
                    NextAvailableName(keepDestination, stem, ext, entry.FullPath, takenNames));
                if (string.Equals(target, entry.FullPath, StringComparison.OrdinalIgnoreCase))
                    continue; // destination is the folder it's already in
                try
                {
                    if (copyKeepers)
                    {
                        File.Copy(entry.FullPath, target);
                        copied.Add(target);
                    }
                    else
                    {
                        File.Move(entry.FullPath, target); // handles cross-volume (SD card → disk)
                        moved.Add((entry.FullPath, target));
                    }
                    committed.Add(entry.FullPath);
                }
                catch
                {
                    failures++;
                }
            }
        }

        if (recycled.Count > 0 || moved.Count > 0 || copied.Count > 0)
        {
            var parts = new List<string>();
            if (recycled.Count > 0) parts.Add($"{recycled.Count} recycled");
            if (moved.Count > 0) parts.Add($"{moved.Count} moved");
            if (copied.Count > 0) parts.Add($"{copied.Count} copied");
            var label = $"triage commit ({string.Join(", ", parts)})";
            PushUndo(label, () => ReverseCommit(moved, recycled, copied));
        }

        // Recycling and moving both change what the source and destination trees add up to, and
        // the sources can now be several folders (issue #43).
        foreach (var folder in rejects.Concat(keeps)
                     .Select(TriageSession.FolderOf)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            FolderScanService.Invalidate(folder);
        if (!string.IsNullOrWhiteSpace(keepDestination))
            FolderScanService.Invalidate(keepDestination);

        // Only the marks this commit actually acted on are cleared. A keeper left in place because
        // no destination was set, or a reject the shell refused, is still pending and must stay
        // marked rather than being silently dropped.
        foreach (var path in committed)
            _triage.Forget(path);
        if (!deleteRejects && string.IsNullOrWhiteSpace(keepDestination))
        {
            // Nothing was asked of the disk at all: treat it as the user clearing the decisions.
            foreach (var entry in rejects.Concat(keeps))
                _triage.Set(entry, TriageFlag.None);
        }
        LoadFolder(FolderPath);

        var summaryParts = new List<string>();
        summaryParts.Add(deleteRejects ? $"{rejectsDeleted} recycled" : $"{rejects.Count} rejected files left in place");
        if (moved.Count > 0) summaryParts.Add($"{moved.Count} moved");
        if (copied.Count > 0) summaryParts.Add($"{copied.Count} copied");
        if (moved.Count == 0 && copied.Count == 0) summaryParts.Add($"{keeps.Count} kept in place");
        var summary = "Triage committed — " + string.Join(", ", summaryParts);
        StatusText = failures == 0 ? summary : $"{summary} · {failures} failed";
        return failures == 0 ? null : $"{failures} file(s) could not be processed.";
    }

    /// <summary>Reverses a whole triage commit: moves keepers back, deletes copies, restores recycled rejects.</summary>
    private static string? ReverseCommit(
        IReadOnlyList<(string From, string To)> moved,
        IReadOnlyList<(string Original, string Recycled)> recycled,
        IReadOnlyList<string> copied)
    {
        var failed = 0;
        foreach (var (from, to) in moved)
            if (ReverseRename(to, from, isDirectory: false) is not null)
                failed++;
        foreach (var path in copied)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                failed++;
            }
        }
        foreach (var (original, bin) in recycled)
            if (!RecycleBinService.Restore(bin, original))
                failed++;
        var total = moved.Count + recycled.Count + copied.Count;
        return failed == 0 ? null : $"{failed} of {total} item(s) could not be restored.";
    }

    // --- Undo journal (issue #9) -----------------------------------------------------

    private void PushUndo(string label, Func<string?> reverse)
    {
        _undoStack.Push(new UndoOperation(label, reverse));
        CanUndo = true;
    }

    /// <summary>Reverses the most recent rename or delete, then reloads the folder to match disk.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;

        var op = _undoStack.Pop();
        CanUndo = _undoStack.Count > 0;

        var error = op.Reverse();
        FolderScanService.ClearCache(); // an undo can move or restore anything, anywhere

        if (!string.IsNullOrEmpty(FolderPath))
            LoadFolder(FolderPath); // rebuilds the list; UpdateStatus runs inside
        StatusText = error is null ? $"Undone: {op.Label}" : $"Undo failed — {error}";
    }

    /// <summary>Moves a renamed item back to its previous path. Returns an error message or null.</summary>
    private static string? ReverseRename(string currentPath, string previousPath, bool isDirectory)
    {
        if (File.Exists(previousPath) || Directory.Exists(previousPath))
            return $"“{Path.GetFileName(previousPath)}” already exists.";
        if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            return "the file is no longer where it was.";
        try
        {
            if (isDirectory)
                Directory.Move(currentPath, previousPath);
            else
                File.Move(currentPath, previousPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        return null;
    }

    /// <summary>Restores a batch of recycled items to their original locations.</summary>
    private static string? ReverseDelete(IReadOnlyList<(string Original, string Recycled)> items)
    {
        var failed = 0;
        foreach (var (original, recycled) in items)
            if (!RecycleBinService.Restore(recycled, original))
                failed++;
        return failed == 0 ? null : $"{failed} of {items.Count} item(s) could not be restored.";
    }

    /// <summary>Renames the entry on disk; returns an error message or null on success.</summary>
    public string? Rename(FileEntry entry, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
            return null;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "The name contains invalid characters.";

        var oldPath = entry.FullPath;
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
            return "A file or folder with that name already exists.";

        var wasDir = entry.IsDirectory;
        try
        {
            if (wasDir)
                Directory.Move(oldPath, newPath);
            else
                File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        // Point the entry at its new path before reloading. Refresh() carries triage flags across
        // by path, so a stale FullPath here would key the snapshot on the old name and silently
        // drop the file's keep/reject flag (issue #30).
        // The mark is keyed by path, so it has to follow the file (issue #30 for the reload case).
        _triage.Rename(oldPath, newPath);
        entry.UpdatePath(newPath);
        PushUndo($"rename to “{newName}”", () => ReverseRename(newPath, oldPath, wasDir));
        Refresh();

        // Refresh() replaces every FileEntry, so re-select the renamed file under its new path —
        // otherwise F2 drops the selection (and the preview) on the file just renamed.
        SelectedFile = Files.FirstOrDefault(
            e => string.Equals(e.FullPath, newPath, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>
    /// Renames the entry to <paramref name="stem"/> (extension preserved), auto-numbering to
    /// avoid collisions — "Clip", then "Clip 2", "Clip 3", … The rename happens in place so the
    /// item keeps its list position, and the stem is remembered in the palette. Returns an error
    /// message, or null on success (including the no-op where the file already has that name).
    /// </summary>
    public string? QuickRename(FileEntry entry, string stem)
    {
        stem = stem.Trim();
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        var dir = Path.GetDirectoryName(entry.FullPath)!;
        var ext = Path.GetExtension(entry.FullPath); // preserves original case; "" for folders

        // Be forgiving if the user typed the extension anyway — the UI already shows it fixed.
        if (ext.Length > 0 && stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            stem = stem[..^ext.Length].TrimEnd();

        if (string.IsNullOrWhiteSpace(stem))
            return null;
        if (stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "The name contains invalid characters.";

        RememberName(stem);

        var targetName = NextAvailableName(dir, stem, ext, entry.FullPath);
        var targetPath = Path.Combine(dir, targetName);

        // Already named this (case-insensitive) — nothing to do on disk.
        if (string.Equals(targetPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
            return null;

        var oldPath = entry.FullPath;
        var wasDir = entry.IsDirectory;
        try
        {
            if (wasDir)
                Directory.Move(oldPath, targetPath);
            else
                File.Move(oldPath, targetPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        _triage.Rename(oldPath, targetPath);
        entry.UpdatePath(targetPath);
        PushUndo($"rename to “{targetName}”", () => ReverseRename(targetPath, oldPath, wasDir));
        UpdateStatus();
        return null;
    }

    /// <summary>
    /// First free name of the form "stem.ext", then "stem 2.ext", "stem 3.ext", … The entry's
    /// own current path counts as free, so re-applying the same name is a no-op rather than a bump.
    /// Probes the filesystem, so it costs one existence check per collision — fine for a single
    /// rename, which is why the commit loop uses the overload that takes a set of taken names.
    /// </summary>
    private static string NextAvailableName(string directory, string stem, string extension, string currentPath)
        => NextAvailableName(directory, stem, extension, currentPath,
            candidatePath => !File.Exists(candidatePath) && !Directory.Exists(candidatePath));

    /// <summary>
    /// Same numbering, but probing an in-memory set of names already in the destination instead of
    /// the disk. Keeps the triage commit at one directory read rather than two syscalls per
    /// collision per file (issue #35). Names handed out are added to <paramref name="taken"/> so
    /// two keepers with the same stem don't both claim it.
    /// </summary>
    private static string NextAvailableName(
        string directory, string stem, string extension, string currentPath, HashSet<string> taken)
    {
        var name = NextAvailableName(directory, stem, extension, currentPath,
            candidatePath => !taken.Contains(Path.GetFileName(candidatePath)));
        taken.Add(name);
        return name;
    }

    /// <summary>
    /// Shared numbering loop. Capped so a pathological folder degrades to a unique suffixed name
    /// instead of spinning on the UI thread with no exit (issue #35) — 10,000 same-stem files is
    /// already far past anything a rename bar is for.
    /// </summary>
    private static string NextAvailableName(
        string directory, string stem, string extension, string currentPath, Func<string, bool> isFree)
    {
        const int maxAttempts = 10_000;
        for (var n = 1; n <= maxAttempts; n++)
        {
            var candidate = (n == 1 ? stem : $"{stem} {n}") + extension;
            var candidatePath = Path.Combine(directory, candidate);

            if (string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase))
                return candidate;
            if (isFree(candidatePath))
                return candidate;
        }
        return $"{stem}_{Guid.NewGuid().ToString("n")[..8]}{extension}";
    }

    private void RememberName(string stem)
    {
        // Move-to-front, case-insensitive de-dupe, capped length.
        var existing = RecentNames.FirstOrDefault(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            RecentNames.Remove(existing);
        RecentNames.Insert(0, stem);
        while (RecentNames.Count > MaxRecentNames)
            RecentNames.RemoveAt(RecentNames.Count - 1);
    }

    /// <summary>Rebuilds the type-filter list from the folder's entries — all types start shown.</summary>
    private void BuildTypeFilters()
    {
        foreach (var filter in TypeFilters)
            filter.PropertyChanged -= TypeFilterChanged;
        TypeFilters.Clear();

        var groups = _allEntries
            .GroupBy(e => e.Extension)
            .OrderBy(g => g.Key == "Folder" ? 0 : 1) // folders first, then extensions A→Z
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var filter = new TypeFilter(group.Key, group.Count());
            filter.PropertyChanged += TypeFilterChanged;
            TypeFilters.Add(filter);
        }
    }

    private void TypeFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TypeFilter.IsChecked) && !_suspendApplyView)
            ApplyView();
    }

    [RelayCommand]
    private void ShowAllTypes() => SetAllTypes(true);

    [RelayCommand]
    private void HideAllTypes() => SetAllTypes(false);

    private void SetAllTypes(bool isChecked)
    {
        // Toggle everything, then refresh the view a single time.
        _suspendApplyView = true;
        foreach (var filter in TypeFilters)
            filter.IsChecked = isChecked;
        _suspendApplyView = false;
        ApplyView();
    }

    private void ApplyView()
    {
        IEnumerable<FileEntry> view = _allEntries;

        if (!string.IsNullOrWhiteSpace(FilterText))
            view = view.Where(f => f.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        // Type filter: keep only entries whose type is currently checked.
        var hidden = TypeFilters.Where(t => !t.IsChecked).Select(t => t.Key).ToHashSet();
        if (hidden.Count > 0)
            view = view.Where(f => !hidden.Contains(f.Extension));

        // Folders always first, then the active column in the chosen direction.
        var ordered = view.OrderBy(f => !f.IsDirectory ? 1 : 0);
        ordered = SortMode switch
        {
            "Size" => SortDescending
                ? ordered.ThenByDescending(f => f.SortSizeBytes)
                : ordered.ThenBy(f => f.SortSizeBytes),
            "Date" => SortDescending
                ? ordered.ThenByDescending(f => f.Modified)
                : ordered.ThenBy(f => f.Modified),
            "Type" => SortDescending
                ? ordered.ThenByDescending(f => f.Extension).ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(f => f.Extension).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => SortDescending
                ? ordered.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        view = ordered;

        Files.Clear();
        foreach (var entry in view)
            Files.Add(entry);

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var folders = Files.Count(f => f.IsDirectory);
        var files = Files.Count - folders;
        var totalSize = Files.Where(f => !f.IsDirectory).Sum(f => f.SizeBytes);
        var triage = KeepCount + RejectCount > 0
            ? $" · triage: ✓ {KeepCount} keep, ✗ {RejectCount} reject"
            : string.Empty;
        StatusText = $"{files} files, {folders} folders — {FileEntry.FormatSize(totalSize)}{triage}";
    }

    /// <summary>
    /// Measures every folder in the list so the Size column shows what each one actually holds
    /// (issue #40), in two phases because the two numbers cost wildly different amounts.
    /// <para>
    /// Phase 1 counts direct children, well under a millisecond each, so selecting a folder shows
    /// a number immediately. Phase 2 walks each subtree for its byte total, which runs from
    /// milliseconds to seconds depending on what's down there; it reports partial totals as it
    /// goes, so a large folder counts up in place instead of sitting blank.
    /// </para>
    /// Reparse points are skipped: a junction's bytes belong to its target, and counting them
    /// here would report them twice in the same column.
    /// </summary>
    private void LoadFolderSizesInBackground()
    {
        _folderStatsCts?.Cancel();
        _folderStatsCts = new CancellationTokenSource();
        var token = _folderStatsCts.Token;
        var folders = _allEntries.Where(e => e.IsDirectory && !e.IsReparsePoint).ToList();
        if (folders.Count == 0)
            return;

        Task.Run(() =>
        {
            // Same cross-thread property set the thumbnail pass uses — WPF marshals the change
            // notification to the UI thread for a plain (non-collection) property.
            foreach (var entry in folders)
            {
                if (token.IsCancellationRequested)
                    return;
                entry.ChildCount = FolderScanService.CountChildren(entry.FullPath);
            }

            try
            {
                // Bounded, so one enormous subfolder can't hold up the rest of the column, and so
                // opening a folder full of folders doesn't put the disk under dozens of walks.
                Parallel.ForEach(
                    folders,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    entry =>
                    {
                        entry.IsScanning = true;
                        try
                        {
                            entry.FolderStats = FolderScanService.Scan(
                                entry.FullPath, token, partial => entry.FolderStats = partial);
                        }
                        finally
                        {
                            entry.IsScanning = false;
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                // Navigating away or refreshing mid-scan is routine, not a failure.
            }
        }, token);
    }

    private void LoadThumbnailsInBackground()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;
        var entries = _allEntries.ToList();

        Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested)
                    return;
                // Frozen BitmapSource is safe to hand to the UI thread via binding.
                entry.Thumbnail = ShellThumbnailService.GetThumbnail(entry.FullPath, 96);
            }
        }, token);
    }
}
