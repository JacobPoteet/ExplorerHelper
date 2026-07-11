using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ExplorerHelper.Models;

public partial class FileEntry : ObservableObject
{
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Extension { get; }
    public long SizeBytes { get; }
    public DateTime Modified { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    public FileEntry(FileSystemInfo info)
    {
        FullPath = info.FullName;
        _name = info.Name;
        IsDirectory = info is DirectoryInfo;
        Extension = IsDirectory ? "Folder" : info.Extension.TrimStart('.').ToUpperInvariant();
        SizeBytes = info is FileInfo file ? file.Length : 0;
        Modified = info.LastWriteTime;
    }

    public string SizeDisplay => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");

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
