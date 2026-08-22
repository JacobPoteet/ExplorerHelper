using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace ExplorerHelper.Services;

/// <summary>
/// Folder statistics by walking the tree (issue #40). Windows caches no folder size to borrow:
/// the shell property store returns nothing for <c>System.Size</c> or <c>System.ItemCount</c> on a
/// plain NTFS directory, and Explorer's own Size column shows the modified date instead. Every
/// number here costs an enumeration, so the app reads them in two tiers:
/// <list type="bullet">
/// <item><see cref="CountChildren"/> — direct children only, for the Size column of every folder
/// row. Measured at 0.07 ms on a 13-item folder and 2.5 ms on System32's 4,991 children.</item>
/// <item><see cref="Scan"/> — the whole subtree, only for the folder the user selected. A 333k-entry
/// tree takes 8-14 s depending on how warm the directory cache is, so this is on-demand,
/// cancellable, and reports partial totals as it goes.</item>
/// </list>
/// Both walk with <see cref="FileSystemEnumerable{TResult}"/> and a transform that reads sizes off
/// the <c>WIN32_FIND_DATA</c> the OS already returned; allocating a <see cref="FileInfo"/> per entry
/// instead costs a second stat call and runs 3-5x slower.
/// </summary>
public static class FolderScanService
{
    /// <summary>
    /// Totals for one subtree. <c>Skipped</c> counts entries the walk could not read (permission
    /// denied, a disconnected drive), which makes the rest a lower bound rather than a wrong answer.
    /// </summary>
    public readonly record struct FolderStats(long Bytes, int FileCount, int FolderCount, int Skipped)
    {
        public int ItemCount => FileCount + FolderCount;

        /// <summary>True when the walk could not read everything, so the totals are a floor.</summary>
        public bool IsPartial => Skipped > 0;

        public FolderStats AddFile(long bytes) =>
            this with { Bytes = Bytes + bytes, FileCount = FileCount + 1 };

        public FolderStats AddFolder() => this with { FolderCount = FolderCount + 1 };
    }

    // Deep scans are the expensive ones, so completed results are reused for the session. There is
    // deliberately no on-disk cache: a stale size shown with confidence is worse than a short
    // spinner, and the folders this app opens (a camera dump, a Downloads folder) scan in ~25 ms.
    private static readonly ConcurrentDictionary<string, FolderStats> DeepCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drops a cached subtree total along with its ancestors and descendants: a file deleted three
    /// levels down changes the total of every folder above it, and deleting a folder outright
    /// strands the totals cached for everything inside it. Call after the app writes under the path.
    /// </summary>
    public static void Invalidate(string path)
    {
        foreach (var key in DeepCache.Keys)
            if (key.Equals(path, StringComparison.OrdinalIgnoreCase)
                || IsUnder(path, key)
                || IsUnder(key, path))
                DeepCache.TryRemove(key, out _);
    }

    /// <summary>Empties the session cache (Refresh).</summary>
    public static void ClearCache() => DeepCache.Clear();

    /// <summary>True when <paramref name="path"/> sits inside <paramref name="ancestor"/>.</summary>
    private static bool IsUnder(string path, string ancestor)
    {
        if (!path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
            return false;
        // Guard against "C:\Foo" matching "C:\FooBar" — the next character has to be a separator.
        return path.Length > ancestor.Length
            && (ancestor.EndsWith(Path.DirectorySeparatorChar)
                || path[ancestor.Length] == Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Counts the direct children of a folder. Uses the same hidden/system exclusions as
    /// <c>MainViewModel.LoadFolder</c> so the count matches the list you get after navigating in.
    /// Returns null when the folder cannot be read, which a caller must not render as "empty".
    /// </summary>
    public static int? CountChildren(string path)
    {
        try
        {
            using var enumerator = new StatsEnumerator(path, ShallowOptions);
            var count = 0;
            while (enumerator.MoveNext())
                count++;
            // A root that wouldn't open yields nothing at all, which is not the same as a folder
            // that is genuinely empty.
            return enumerator.Skipped > 0 && count == 0 ? null : count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the whole subtree for a size and item count. Honours <paramref name="token"/> between
    /// entries and returns what it had at that point, so a cancelled scan costs nothing extra and
    /// its partial result never reaches the cache. <paramref name="progress"/> fires on the calling
    /// thread at most once per <paramref name="progressInterval"/> (200 ms by default), letting the
    /// caller show a running total instead of freezing a row for seconds; marshal it to the
    /// dispatcher yourself.
    /// </summary>
    public static FolderStats Scan(
        string path,
        CancellationToken token = default,
        Action<FolderStats>? progress = null,
        TimeSpan? progressInterval = null)
    {
        if (DeepCache.TryGetValue(path, out var cached))
            return cached;

        var interval = progressInterval ?? TimeSpan.FromMilliseconds(200);
        var stats = new FolderStats();
        var stopwatch = Stopwatch.StartNew();
        var nextReport = interval;

        // A root that won't open (deleted between the listing and the scan, or permission denied)
        // reaches ContinueOnError rather than throwing, so it comes back as Skipped instead of
        // escaping into the Parallel.ForEach the caller runs this in. The try still stands for
        // everything else the constructor can reject, such as a malformed path.
        StatsEnumerator enumerator;
        try
        {
            enumerator = new StatsEnumerator(path, DeepOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FolderStats(0, 0, 0, 1);
        }

        using (enumerator)
        {
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext())
                        break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Whatever is left is unreachable; report the total so far as a floor.
                    return stats with { Skipped = enumerator.Skipped + 1 };
                }

                if (token.IsCancellationRequested)
                    return stats with { Skipped = enumerator.Skipped };

                var length = enumerator.Current;
                stats = length < 0 ? stats.AddFolder() : stats.AddFile(length);

                if (progress is not null && stopwatch.Elapsed >= nextReport)
                {
                    nextReport = stopwatch.Elapsed + interval;
                    progress(stats with { Skipped = enumerator.Skipped });
                }
            }

            stats = stats with { Skipped = enumerator.Skipped };
        }

        DeepCache[path] = stats;
        return stats;
    }

    /// <summary>
    /// Matches the default <c>EnumerateDirectories</c>/<c>EnumerateFiles</c> behaviour the file list
    /// uses, so a folder's item count agrees with the list you see after entering it. See
    /// <see cref="DeepOptions"/> for why IgnoreInaccessible stays off.
    /// </summary>
    private static EnumerationOptions ShallowOptions => new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>
    /// Subtree walk. Hidden and system files count toward the size (a total that silently omits them
    /// is wrong), but reparse points never do: following a junction double-counts its target, and a
    /// symlink loop never terminates. Seeding a walk from a list of directories you gathered
    /// yourself does not get this protection — <see cref="EnumerationOptions.AttributesToSkip"/>
    /// filters entries found during enumeration, not the root handed to the enumerator.
    /// <para>
    /// IgnoreInaccessible is off on purpose. Turning it on makes .NET swallow a root it cannot open
    /// without ever consulting <see cref="StatsEnumerator.ContinueOnError"/> — verified against
    /// <c>C:\System Volume Information</c>, which then reports zero entries and zero errors, so an
    /// unreadable folder renders as an empty one. Off, the same failure arrives as an error the
    /// enumerator counts and continues past.
    /// </para>
    /// </summary>
    private static EnumerationOptions DeepOptions => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Yields the length of each entry, or -1 for a directory, and counts what it could not read.
    /// Returning true from <see cref="ContinueOnError"/> keeps the walk going past an unreadable
    /// subdirectory while still recording that something was missed, so a short total reports
    /// itself as a floor instead of passing for the whole answer.
    /// </summary>
    private sealed class StatsEnumerator(string directory, EnumerationOptions options)
        : FileSystemEnumerator<long>(directory, options)
    {
        public int Skipped { get; private set; }

        protected override long TransformEntry(ref FileSystemEntry entry) =>
            entry.IsDirectory ? -1L : entry.Length;

        protected override bool ContinueOnError(int error)
        {
            Skipped++;
            return true;
        }
    }
}
