using System.IO;
using ExplorerHelper.Models;

namespace ExplorerHelper.Services;

/// <summary>
/// The keep/reject marks made during one run of the app, keyed by full path so they outlive the
/// folder they were made in (issue #43).
/// <para>
/// Before navigation existed, flags lived only on the <see cref="FileEntry"/> objects in the
/// current folder and <c>LoadFolder</c> carried them across a reload with a local path-keyed
/// dictionary. Browsing into a subfolder would have thrown them away, so that dictionary is
/// promoted here and kept for the session instead. Nothing reaches the disk until a commit.
/// </para>
/// The entries are the live list items, so the review screen can render a mark made three folders
/// ago with its name, size and thumbnail intact. <see cref="Rebind"/> keeps that true across the
/// reloads that replace every <see cref="FileEntry"/>.
/// </summary>
public sealed class TriageSession
{
    private readonly Dictionary<string, FileEntry> _marked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every marked file, in no particular order.</summary>
    public IReadOnlyCollection<FileEntry> Marked => _marked.Values;

    /// <summary>How many distinct folders hold marks — what makes a commit cross-folder.</summary>
    public int FolderCount => _marked.Values
        .Select(FolderOf)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    /// <summary>The containing folder of a marked entry.</summary>
    public static string FolderOf(FileEntry entry) =>
        Path.GetDirectoryName(entry.FullPath) ?? string.Empty;

    /// <summary>
    /// Records a decision. Folders are never marked: the deck and the commit only ever touch
    /// files, so a folder can't end up in the reject pile.
    /// </summary>
    public void Set(FileEntry entry, TriageFlag flag)
    {
        if (entry.IsDirectory)
            return;
        entry.Flag = flag;
        if (flag == TriageFlag.None)
            _marked.Remove(entry.FullPath);
        else
            _marked[entry.FullPath] = entry;
    }

    /// <summary>Reads back a decision for a path that may not be loaded right now.</summary>
    public TriageFlag FlagFor(string path) =>
        _marked.TryGetValue(path, out var entry) ? entry.Flag : TriageFlag.None;

    /// <summary>
    /// Re-applies marks onto a freshly loaded folder and adopts the new entry objects. Every
    /// reload builds new <see cref="FileEntry"/> instances, so without this the list would show
    /// an unmarked file while the pile still held the stale object for the same path.
    /// </summary>
    public void Rebind(IEnumerable<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!_marked.TryGetValue(entry.FullPath, out var previous))
                continue;
            entry.Flag = previous.Flag;
            _marked[entry.FullPath] = entry;
        }
    }

    /// <summary>Drops the mark for a path that no longer exists (deleted, renamed, committed).</summary>
    public void Forget(string path) => _marked.Remove(path);

    /// <summary>Follows a mark to the file's new location after a rename.</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (!_marked.Remove(oldPath, out var entry))
            return;
        _marked[newPath] = entry;
    }

    /// <summary>Discards every mark and clears the flags on the entries holding them.</summary>
    public void Clear()
    {
        foreach (var entry in _marked.Values)
            entry.Flag = TriageFlag.None;
        _marked.Clear();
    }

    /// <summary>
    /// The marks a commit would act on: everything, or only the folder currently open. Ordered by
    /// folder then name so the commit and its summary read in a stable order.
    /// </summary>
    public List<FileEntry> Pending(TriageFlag flag, string? onlyFolder = null) => _marked.Values
        .Where(e => e.Flag == flag)
        .Where(e => onlyFolder is null || string.Equals(FolderOf(e), onlyFolder, StringComparison.OrdinalIgnoreCase))
        .OrderBy(FolderOf, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
