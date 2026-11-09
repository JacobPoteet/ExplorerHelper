---
paths:
  - "src/ExplorerHelper/Services/FolderScanService.cs"
  - "src/ExplorerHelper/Models/FileEntry.cs"
---

# Folder stats

- **Folder stats are two-tier** (`FolderScanService`, issue #40). Windows caches no folder size
  (the shell property store returns nothing for `System.Size` on a directory), so every number costs
  an enumeration. `MainViewModel.LoadFolderSizesInBackground` runs both phases after a load:
  `CountChildren` (direct children, sub-millisecond) for every folder so the details panel has a
  number immediately, then `Scan` (whole subtree) under a `MaxDegreeOfParallelism = 4`
  `Parallel.ForEach`, reporting partial totals so the Size cell counts up instead of sitting blank.
  Completed totals are cached for the session; `Invalidate` drops a path plus its ancestors and
  descendants, and mutations (`Delete`, `CommitTriage`, `Undo`, `Refresh`) call it.
  Two traps, both verified rather than assumed:
    - `AttributesToSkip` filters entries found *during* enumeration, not the root you hand the
      enumerator. `_allEntries` includes junctions, so the scan pass skips `IsReparsePoint`
      folders itself; without that, the profile's `My Documents` junction double-counts `Documents`.
    - `IgnoreInaccessible = true` makes .NET swallow a root it can't open *without* calling
      `ContinueOnError`, so an unreadable folder reports zero entries and zero errors and renders
      as "empty". Both option sets leave it off and count errors in the enumerator instead;
      `FileEntry` shows "no access" for that case and "≥ 4.2 GB" for a partial total.
