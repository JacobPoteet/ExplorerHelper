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

    /// <summary>The distinct file types present in the folder, each toggleable on/off (issue #5).</summary>
    public ObservableCollection<TypeFilter> TypeFilters { get; } = [];

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
        var dir = new DirectoryInfo(path);
        _allEntries = dir.EnumerateDirectories()
            .Cast<FileSystemInfo>()
            .Concat(dir.EnumerateFiles())
            .Select(info => new FileEntry(info))
            .ToList();

        BuildTypeFilters();
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
        UpdateStatus();
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
        StatusText = $"{files} files, {folders} folders — {FileEntry.FormatSize(totalSize)}";
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
