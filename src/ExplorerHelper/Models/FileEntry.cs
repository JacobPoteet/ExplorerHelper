using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ExplorerHelper.Services;

namespace ExplorerHelper.Models;

public partial class FileEntry : ObservableObject
{
    public string FullPath { get; private set; }
    public bool IsDirectory { get; }
    public string Extension { get; }
    public long SizeBytes { get; }

    /// <summary>
    /// True for a junction, symlink or other reparse point. Its contents live somewhere else, so
    /// the subtree scan skips it rather than counting the target's bytes under this folder too.
    /// </summary>
    public bool IsReparsePoint { get; }
    public DateTime Modified { get; }

    /// <summary>When the file/folder was created — used by the dynamic "created date" quick button.</summary>
    public DateTime Created { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    /// <summary>
    /// Direct children of a folder, from one shallow enumeration after the list loads (issue #40).
    /// Costs well under a millisecond, so the details panel has a number to show while the far
    /// slower subtree walk is still running. Null until counted, and for files.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemsDisplay))]
    private int? _childCount;

    /// <summary>
    /// Subtree totals for a folder: bytes, file and folder counts (issue #40). Set repeatedly while
    /// the scan runs, so the Size cell counts up rather than sitting blank for seconds. Null until
    /// the walk reports something, and for files.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    [NotifyPropertyChangedFor(nameof(ItemsDisplay))]
    private FolderScanService.FolderStats? _folderStats;

    /// <summary>True while the subtree walk is in flight, which makes the size a running total.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    [NotifyPropertyChangedFor(nameof(ItemsDisplay))]
    private bool _isScanning;

    /// <summary>Triage decision (keep/reject) — session-only; applied to disk on commit.</summary>
    [ObservableProperty]
    private TriageFlag _flag;

    public FileEntry(FileSystemInfo info)
    {
        FullPath = info.FullName;
        _name = info.Name;
        IsDirectory = info is DirectoryInfo;
        Extension = IsDirectory ? "Folder" : info.Extension.TrimStart('.').ToUpperInvariant();
        SizeBytes = info is FileInfo file ? file.Length : 0;
        Modified = info.LastWriteTime;
        Created = info.CreationTime;
        IsReparsePoint = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// Reflects a rename that already happened on disk, keeping the entry in place in the
    /// list (no re-sort) so a review-and-rename pass doesn't shuffle items around.
    /// The thumbnail and size are unchanged — only the location changes.
    /// </summary>
    public void UpdatePath(string newFullPath)
    {
        FullPath = newFullPath;
        Name = Path.GetFileName(newFullPath);
    }

    public string SizeDisplay => IsDirectory ? FolderSizeDisplay : FormatSize(SizeBytes);

    /// <summary>
    /// A folder's size: blank before the walk reports anything, a running total with an ellipsis
    /// while it works, and a plain size once done. A walk that could not read everything reports a
    /// floor ("≥ 4.2 GB") rather than a total that looks complete but is not.
    /// </summary>
    private string FolderSizeDisplay
    {
        get
        {
            if (IsReparsePoint)
                return "link"; // contents belong to the target; counting them here double-counts
            if (FolderStats is not { } stats)
                return IsScanning ? "…" : string.Empty;
            // Nothing readable and nothing found: the walk was refused, which must not read as 0 B.
            if (stats is { IsPartial: true, ItemCount: 0 } && !IsScanning)
                return "no access";
            var size = FormatSize(stats.Bytes);
            if (IsScanning)
                return $"{size}…";
            return stats.IsPartial ? $"≥ {size}" : size;
        }
    }

    /// <summary>
    /// A folder's item count for the details panel: the subtree total once the walk reports one,
    /// falling back to the direct-child count so selecting a folder shows something straight away.
    /// Null when neither number has arrived, and for files.
    /// </summary>
    public string? ItemsDisplay
    {
        get
        {
            if (!IsDirectory || IsReparsePoint)
                return null;
            if (FolderStats is { ItemCount: > 0 } stats)
            {
                var total = $"{stats.ItemCount:n0} item{(stats.ItemCount == 1 ? "" : "s")}";
                var split = $"{stats.FileCount:n0} file{(stats.FileCount == 1 ? "" : "s")}, " +
                            $"{stats.FolderCount:n0} folder{(stats.FolderCount == 1 ? "" : "s")}";
                return IsScanning ? $"{total} ({split})…" : $"{total} ({split})";
            }
            if (FolderStats is { ItemCount: 0 } done && !IsScanning)
                return done.IsPartial ? "no access" : "empty";
            return ChildCount switch
            {
                // Null means the folder refused to be read, which is not the same as being empty.
                null => IsScanning ? null : "no access",
                0 => "empty",
                1 => "1 direct child…",
                var n => $"{n:n0} direct children…",
            };
        }
    }

    /// <summary>
    /// What the Size column sorts on. A folder uses its subtree total so the column can rank
    /// subfolders by weight; one not measured yet sorts below every measured folder rather than
    /// tying at zero with all the others.
    /// </summary>
    public long SortSizeBytes => IsDirectory ? FolderStats?.Bytes ?? -1 : SizeBytes;

    public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// The containing folder's name, used to group the triage piles once marks span more than one
    /// folder (issue #43). Falls back to the full directory path at a drive root, which has none.
    /// </summary>
    public string FolderName
    {
        get
        {
            var directory = Path.GetDirectoryName(FullPath);
            if (string.IsNullOrEmpty(directory))
                return string.Empty;
            var name = Path.GetFileName(directory);
            return string.IsNullOrEmpty(name) ? directory : name;
        }
    }

    /// <summary>Type shown in the Type column and used as the file-type filter key label.</summary>
    public string TypeDisplay => IsDirectory ? "Folder" : Extension.Length == 0 ? "—" : Extension;

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
