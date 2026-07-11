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
        foreach (var entry in entries)
        {
            if (RecycleBinService.MoveToRecycleBin(entry.FullPath))
            {
                Files.Remove(entry);
                _allEntries.Remove(entry);
            }
        }
        UpdateStatus();
    }

    /// <summary>Renames the entry on disk; returns an error message or null on success.</summary>
    public string? Rename(FileEntry entry, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
            return null;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "The name contains invalid characters.";

        var newPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
            return "A file or folder with that name already exists.";

        try
        {
            if (entry.IsDirectory)
                Directory.Move(entry.FullPath, newPath);
            else
                File.Move(entry.FullPath, newPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

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

        try
        {
            if (entry.IsDirectory)
                Directory.Move(entry.FullPath, targetPath);
            else
                File.Move(entry.FullPath, targetPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        entry.UpdatePath(targetPath);
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

    private void ApplyView()
    {
        IEnumerable<FileEntry> view = _allEntries;

        if (!string.IsNullOrWhiteSpace(FilterText))
            view = view.Where(f => f.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

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
