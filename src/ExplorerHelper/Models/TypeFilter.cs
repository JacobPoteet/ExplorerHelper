using CommunityToolkit.Mvvm.ComponentModel;

namespace ExplorerHelper.Models;

/// <summary>
/// One entry in the file-type filter (issue #5): a distinct type present in the folder,
/// how many items have it, and whether items of that type are currently shown.
/// </summary>
public partial class TypeFilter : ObservableObject
{
    /// <summary>Match key — equals <see cref="FileEntry.Extension"/> ("Folder", "MP4", "" …).</summary>
    public string Key { get; }

    /// <summary>Human-readable label for the checkbox.</summary>
    public string Display { get; }

    public int Count { get; }

    [ObservableProperty]
    private bool _isChecked = true;

    public TypeFilter(string key, int count)
    {
        Key = key;
        Count = count;
        Display = key switch
        {
            "Folder" => "Folders",
            "" => "(no extension)",
            _ => key,
        };
    }
}
