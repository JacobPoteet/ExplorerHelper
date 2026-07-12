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

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        foreach (var name in _settings.QuickNameButtons)
            QuickNameButtons.Add(name);
        TodayDateFormat = _settings.TodayDateFormat;
        CreatedDateFormat = _settings.CreatedDateFormat;
    }

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

    /// <summary>Files currently flagged Keep, in folder order.</summary>
    public ObservableCollection<FileEntry> KeepPile { get; } = [];

    /// <summary>Files currently flagged Reject, in folder order.</summary>
    public ObservableCollection<FileEntry> RejectPile { get; } = [];

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

    public void LoadFolder(string path)
    {
        if (!Directory.Exists(path))
            return;

        FolderPath = path;

        // Triage flags must survive a reload (Undo and Refresh both rebuild the list),
        // so carry them across by path.
        var previousFlags = _allEntries
            .Where(e => e.Flag != TriageFlag.None)
            .ToDictionary(e => e.FullPath, e => e.Flag, StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(path);
        _allEntries = dir.EnumerateDirectories()
            .Cast<FileSystemInfo>()
            .Concat(dir.EnumerateFiles())
            .Select(info => new FileEntry(info))
            .ToList();

        foreach (var entry in _allEntries)
            if (previousFlags.TryGetValue(entry.FullPath, out var flag))
                entry.Flag = flag;

        BuildTypeFilters();
        RecomputeTriage();
        ApplyView();
        LoadThumbnailsInBackground();
    }

    [RelayCommand]
    private void Refresh()
    {
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
            var recycled = RecycleBinService.MoveToRecycleBin(entry.FullPath);
            if (recycled is not null)
            {
                restorable.Add((entry.FullPath, recycled));
                Files.Remove(entry);
                _allEntries.Remove(entry);
            }
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
        entry.Flag = flag;
        RecomputeTriage();
    }

    /// <summary>Discards every triage mark (used when the user exits without committing).</summary>
    public void ClearAllFlags()
    {
        foreach (var entry in _allEntries)
            entry.Flag = TriageFlag.None;
        RecomputeTriage();
    }

    /// <summary>Rebuilds the derived piles/counts from the entries' flags.</summary>
    private void RecomputeTriage()
    {
        KeepPile.Clear();
        RejectPile.Clear();
        long keepBytes = 0, rejectBytes = 0;
        var unmarked = 0;

        foreach (var entry in _allEntries)
        {
            if (entry.IsDirectory)
                continue;
            switch (entry.Flag)
            {
                case TriageFlag.Keep:
                    KeepPile.Add(entry);
                    keepBytes += entry.SizeBytes;
                    break;
                case TriageFlag.Reject:
                    RejectPile.Add(entry);
                    rejectBytes += entry.SizeBytes;
                    break;
                default:
                    unmarked++;
                    break;
            }
        }

        KeepCount = KeepPile.Count;
        RejectCount = RejectPile.Count;
        UnmarkedFileCount = unmarked;
        KeepPileSummary = $"{KeepCount} · {FileEntry.FormatSize(keepBytes)}";
        RejectPileSummary = $"{RejectCount} · {FileEntry.FormatSize(rejectBytes)}";
        UpdateStatus();
    }

    /// <summary>
    /// Applies the triage decisions to disk in one shot: rejects go to the Recycle Bin and —
    /// when <paramref name="keepDestination"/> is set — keepers move there (collisions
    /// auto-number). Pushes a single undo entry that reverses the whole commit. Returns an
    /// error summary, or null when every file was processed.
    /// </summary>
    public string? CommitTriage(string? keepDestination)
    {
        var rejects = _allEntries.Where(e => !e.IsDirectory && e.Flag == TriageFlag.Reject).ToList();
        var keeps = _allEntries.Where(e => !e.IsDirectory && e.Flag == TriageFlag.Keep).ToList();

        var failures = 0;

        var recycled = new List<(string Original, string Recycled)>();
        foreach (var entry in rejects)
        {
            var binPath = RecycleBinService.MoveToRecycleBin(entry.FullPath);
            if (binPath is not null)
                recycled.Add((entry.FullPath, binPath));
            else
                failures++;
        }

        var moved = new List<(string From, string To)>();
        if (!string.IsNullOrWhiteSpace(keepDestination))
        {
            foreach (var entry in keeps)
            {
                var stem = Path.GetFileNameWithoutExtension(entry.FullPath);
                var ext = Path.GetExtension(entry.FullPath);
                var target = Path.Combine(
                    keepDestination, NextAvailableName(keepDestination, stem, ext, entry.FullPath));
                if (string.Equals(target, entry.FullPath, StringComparison.OrdinalIgnoreCase))
                    continue; // destination is the folder it's already in
                try
                {
                    File.Move(entry.FullPath, target); // handles cross-volume (SD card → disk)
                    moved.Add((entry.FullPath, target));
                }
                catch
                {
                    failures++;
                }
            }
        }

        if (recycled.Count > 0 || moved.Count > 0)
        {
            var label = moved.Count > 0
                ? $"triage commit ({recycled.Count} recycled, {moved.Count} moved)"
                : $"triage commit ({recycled.Count} recycled)";
            PushUndo(label, () => ReverseCommit(moved, recycled));
        }

        // The session is done — clear every flag before the reload so none carry over.
        foreach (var entry in _allEntries)
            entry.Flag = TriageFlag.None;
        LoadFolder(FolderPath);

        var summary = $"Triage committed — {recycled.Count} recycled"
            + (moved.Count > 0 ? $", {moved.Count} moved" : $", {keeps.Count} kept in place");
        StatusText = failures == 0 ? summary : $"{summary} · {failures} failed";
        return failures == 0 ? null : $"{failures} file(s) could not be processed.";
    }

    /// <summary>Reverses a whole triage commit: moves keepers back, restores recycled rejects.</summary>
    private static string? ReverseCommit(
        IReadOnlyList<(string From, string To)> moved,
        IReadOnlyList<(string Original, string Recycled)> recycled)
    {
        var failed = 0;
        foreach (var (from, to) in moved)
            if (ReverseRename(to, from, isDirectory: false) is not null)
                failed++;
        foreach (var (original, bin) in recycled)
            if (!RecycleBinService.Restore(bin, original))
                failed++;
        var total = moved.Count + recycled.Count;
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

        PushUndo($"rename to “{newName}”", () => ReverseRename(newPath, oldPath, wasDir));
        Refresh();
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

        entry.UpdatePath(targetPath);
        PushUndo($"rename to “{targetName}”", () => ReverseRename(targetPath, oldPath, wasDir));
        UpdateStatus();
        return null;
    }

    /// <summary>
    /// First free name of the form "stem.ext", then "stem 2.ext", "stem 3.ext", … The entry's
    /// own current path counts as free, so re-applying the same name is a no-op rather than a bump.
    /// </summary>
    private static string NextAvailableName(string directory, string stem, string extension, string currentPath)
    {
        for (var n = 1; ; n++)
        {
            var candidate = (n == 1 ? stem : $"{stem} {n}") + extension;
            var candidatePath = Path.Combine(directory, candidate);

            if (string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase))
                return candidate;
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
                return candidate;
        }
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
                ? ordered.ThenByDescending(f => f.SizeBytes)
                : ordered.ThenBy(f => f.SizeBytes),
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
