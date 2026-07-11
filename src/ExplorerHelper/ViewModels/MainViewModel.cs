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

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private FileEntry? _selectedFile;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _sortMode = "Name";

    [ObservableProperty]
    private string _statusText = "No folder loaded";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMenuButtonText))]
    private bool _contextMenuRegistered = ContextMenuRegistrar.IsRegistered;

    public string ContextMenuButtonText =>
        ContextMenuRegistered ? "Remove context menu" : "Add context menu";

    public static readonly string[] SortModes = ["Name", "Size", "Date", "Type"];

    public string[] SortModesList => SortModes;

    private List<FileEntry> _allEntries = [];
    private CancellationTokenSource? _thumbnailCts;

    partial void OnFilterTextChanged(string value) => ApplyView();

    partial void OnSortModeChanged(string value) => ApplyView();

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

    private void ApplyView()
    {
        IEnumerable<FileEntry> view = _allEntries;

        if (!string.IsNullOrWhiteSpace(FilterText))
            view = view.Where(f => f.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        // Folders first, then the chosen ordering
        view = SortMode switch
        {
            "Size" => view.OrderBy(f => !f.IsDirectory ? 1 : 0).ThenByDescending(f => f.SizeBytes),
            "Date" => view.OrderBy(f => !f.IsDirectory ? 1 : 0).ThenByDescending(f => f.Modified),
            "Type" => view.OrderBy(f => !f.IsDirectory ? 1 : 0).ThenBy(f => f.Extension).ThenBy(f => f.Name),
            _ => view.OrderBy(f => !f.IsDirectory ? 1 : 0).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };

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
