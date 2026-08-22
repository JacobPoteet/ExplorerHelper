# CLAUDE.md

Guidance for working in this repo efficiently. Read this first — it front-loads the
architecture and conventions so you don't have to re-derive them each session.

## What this is

**Explorer Helper** — a Windows WPF desktop app (.NET 8, `net8.0-windows`) that adds a
"Clean this folder" entry to the Explorer right-click menu and opens a triage UI: a file
list + preview pane, quick-rename, dating-app-style keep/reject triage, and undo. Per-user,
no admin required.

Stack: WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent theme, `ui:` namespace),
CommunityToolkit.Mvvm (source-generated `[ObservableProperty]` / `[RelayCommand]`),
WebView2 (PDF preview). Dark theme is hard-set in `App.xaml`, which also defines the keep/reject
design tokens (mint `#3DD68C` / coral `#FF5C7C`); the violet accent `#7C5CFC` is applied separately
in `App.xaml.cs` via `ApplicationAccentColorManager`, so changing brand colors means touching both.

## Build / run / test

```powershell
# Build both projects (dotnet 9 SDK on CI, but the target is net8.0-windows)
dotnet build ExplorerHelper.sln -c Debug

# Run — the first existing directory in args is the folder to open; with none, a folder picker shows
dotnet run --project src/ExplorerHelper -- "C:\some\folder"
# or run the built exe directly:
src/ExplorerHelper/bin/Debug/net8.0-windows/ExplorerHelper.exe "C:\some\folder"

# Publish self-contained exe + portable zip into artifacts/ (also the CI smoke test).
# Version comes from Directory.Build.props; -Version only overrides it.
./build.ps1
./build.ps1 -Installer                  # also builds Inno Setup installer

# Cut a release: bumps the props file, commits, tags, and pushes both (issue #46).
./release.ps1 -Version 0.7.0
```

There is **no test project**. Verify changes by driving the running app (see below).

## Architecture — where things live

Two projects. `src/ExplorerHelper` is the app; `src/ExplorerHelper.ShellExtension` is a separate
x64, framework-dependent COM handler (`IExplorerCommand`) used only by the optional Windows 11
sparse MSIX (issue #11) — see `packaging/README.md`. No `ProjectReference` links them; the CLSID
in `ExplorerHelperCommand`'s `[Guid]` and in `AppxManifest.xml` is the whole contract.

Inside `src/ExplorerHelper`:

- `src/ExplorerHelper/App.xaml.cs` — startup; picks the folder from args or a dialog. A global
  `DispatcherUnhandledException` handler shows a MessageBox but keeps running (so a silent crash
  still leaves the window up — check for an error dialog when verifying).
- `MainWindow.xaml(.cs)` — the shell: toolbar, the location bar (back/forward/up + breadcrumb),
  the file `ListView` (custom-retemplated GridView), the right column (preview → details strip →
  quick-rename bar), and the Settings popup. Most view logic (sorting indicators, quick-rename
  flow, keyboard triage, navigation) is in the code-behind. Root grid rows are title bar, toolbar,
  location bar, split, status bar — add a row and every `Grid.Row` below it shifts.
- `ViewModels/MainViewModel.cs` — the bulk of the logic: folder loading, filtering/sorting,
  triage piles, undo journal, quick-rename, and preview-details computation. Uses
  `[ObservableProperty]`; partial hooks like `OnSelectedFileChanged(value)` are how selection
  side effects are wired.
- `Controls/PreviewPane.xaml(.cs)` — the preview surface (image / video+audio via `MediaElement`
  / PDF via WebView2 / generic shell thumbnail) **and** the video scrub timeline. Shared by the
  main window and the triage card.
- `Controls/TriageView.xaml(.cs)` — the full-screen swipe/keep/reject overlay + review screen.
  Hosts its own `PreviewPane` (`CardPreview`).
- `Services/` — `AppSettings` (JSON persistence), `ContextMenuRegistrar` (registry entries),
  `RecycleBinService`, `ShellThumbnailService` (shell thumbnails), `ShellPropertyService`
  (media metadata via the shell property store), `UpdateService` (self-update from GitHub releases),
  `FolderScanService` (folder item counts and subtree sizes), `TriageSession` (keep/reject marks,
  keyed by path so they outlive the folder they were made in).
- `Models/` — `FileEntry` (the list item), `TriageFlag`, `TypeFilter`, and `PreviewDetail.cs`,
  which holds three types: `PreviewDetailRow`, `PreviewDetailToggle`, and the `PreviewDetailKinds`
  catalogue (the key list plus `DefaultEnabled`, which turns on 7 of the 9 details).
- `CommitDialog.xaml(.cs)` — the triage commit dialog. Owns the three independent switches
  (recycle rejects / move / copy, issue #23) and the session-remembered destination.
- `RenameDialog.xaml(.cs)` — the F2 modal, with the Explorer-style stem pre-selection.
- `Converters/InverseBoolToVisibilityConverter.cs` — used for empty-state hints bound to `HasItems`.

## Conventions that aren't obvious

- **Issue traceability:** features are annotated in code with `(issue #N)` comments. Match this
  when adding a feature that closes an issue.
- **Preview file handles are released before disk ops.** A live `MediaElement`/WebView2 keeps the
  file open, so `Preview.Clear()` then `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)`
  is used before delete/rename/move so the handle is freed first (issue #1). Follow this pattern
  for any new code that mutates a file that might be previewing. `DeleteSelected`,
  `ApplyQuickRename`, and `TriageView.Commit_Click` all do; `MainWindow.RenameSelected` (F2) is a
  known gap (issue #31). The failure mode is a hang or an in-use error, not an exception you'll
  see in a stack trace. Navigation is covered centrally: `MainViewModel.FolderChanging` fires
  before every folder switch and `MainWindow` clears the preview on it, so command-bound toolbar
  buttons and code-behind paths both release handles without each remembering to.
- **The version lives in `Directory.Build.props` only** (issue #46). Both projects inherit it and
  `build.ps1` defaults to it, so there is no second number to keep in step — the shell extension sat
  at 0.1.0 for five releases because it had one. `release.yml` fails a tag that disagrees with the
  props file, since a lagging version makes a build from source see the newer tag and show a
  permanent "update available" pill (issue #33). Bump it with `./release.ps1 -Version x.y.z`, which
  bumps, commits, tags and pushes together so the two can't drift apart. Note the declared version
  never reaches a *released* binary: `build.ps1` passes `-p:Version` from the tag, which overrides
  it. It only affects local builds.
- **Settings persistence:** user prefs live in `AppSettings` → `%APPDATA%\ExplorerHelper\settings.json`.
  It's forgiving of missing/corrupt files (falls back to defaults) and `Normalized()` fills in
  nulls from older files. New persisted settings: add a property, default it, and normalize it.
- **Shell interop needs backslash paths.** `SHCreateItemFromParsingName` /
  `SHGetPropertyStoreFromParsingName` fail on forward-slash paths and return null. `FileEntry.FullPath`
  (from `FileSystemInfo.FullName`) is already backslash — pass it through unchanged.
- **PROPVARIANT interop** (`ShellPropertyService`): the struct uses two `IntPtr` union slots so it's
  the right size on x86 and x64; values are coerced with the `propsys.dll` `PropVariantToXxx` helpers
  rather than parsing the union by hand. Always `PropVariantClear` after reading.
- **Background work** (thumbnails, media metadata, folder child counts) runs on `Task.Run` with a
  `CancellationTokenSource` that's cancelled when the selection changes; results are marshalled back via
  `Application.Current.Dispatcher` and dropped if the selection has moved on.
- **Folder stats are two-tier** (`FolderScanService`, issue #40). Windows caches no folder size —
  the shell property store returns nothing for `System.Size` on a directory — so every number costs
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
- **Navigation** (issue #41) is a two-stack browser model on `MainViewModel`: `NavigateTo` pushes
  the current folder onto `_backStack` and clears `_forwardStack`; `NavigateBack`/`NavigateForward`
  move between them via `Step`. `LoadFolder` compares against the outgoing `FolderPath` to tell an
  actual move from a `Refresh`/`Undo` reload, and only a move restores the remembered selection
  from `_lastSelectedByFolder`. Enter and double-click enter a folder; files still go to the shell.
- **Triage marks live in `TriageSession`, not on the folder** (issue #43). They're keyed by full
  path, so browsing away and back keeps them, and a commit can span folders. Every reload builds
  new `FileEntry` objects, so `LoadFolder` calls `_triage.Rebind(_allEntries)` to re-apply flags
  *and* adopt the new instances — skip that and the list shows an unmarked file while the review
  pile still holds the stale object for the same path. Anything that moves or removes a file has to
  tell the session: `Delete` calls `Forget`, both rename paths call `Rename`, and `CommitTriage`
  forgets only the marks it actually acted on (a keeper left in place because no destination was
  set is still pending). Because marks are now invisible from other folders, the toolbar pill
  (`HasPendingMarks`/`PendingMarksSummary`) and the commit dialog's per-folder breakdown are load-
  bearing, not decoration.
- **Self-update** (`UpdateService`): a background check hits the GitHub releases API and, if a newer
  tag exists, shows an update pill (wired through `MainViewModel`, gated by the `CheckForUpdates`
  setting). `CheckForUpdateAsync` never throws — offline/rate-limited/malformed all read as "no
  update". Only *installed* copies (detected via the Inno Setup per-user uninstall key,
  `IsInstalledCopy()`) get the seamless silent-install-and-relaunch path; portable copies are sent
  to the release page. Release tags and the `ExplorerHelper-Setup-*.exe` asset name are the contract
  the check relies on — keep them stable.

## Verifying UI changes (drive the real app)

Launch the exe with a folder containing relevant files, confirm it stays up (no crash / no error
dialog), then screenshot. **Find the window rect dynamically** — the window gets moved between
monitors and can sit at negative coordinates, so never hardcode positions or assume the primary
screen. Capture the whole virtual desktop and crop to the window's `GetWindowRect`:

```powershell
Add-Type -AssemblyName System.Drawing,System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@
$p = Get-Process ExplorerHelper | Select-Object -First 1
[Win]::ShowWindow($p.MainWindowHandle,9) | Out-Null      # SW_RESTORE
[Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 400
$r = New-Object Win+RECT; [Win]::GetWindowRect($p.MainWindowHandle,[ref]$r) | Out-Null
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$full = New-Object System.Drawing.Bitmap $vs.Width,$vs.Height
[System.Drawing.Graphics]::FromImage($full).CopyFromScreen($vs.Location,[System.Drawing.Point]::Empty,$vs.Size)
$w=$r.R-$r.L; $h=$r.B-$r.T
$crop = New-Object System.Drawing.Bitmap $w,$h
[System.Drawing.Graphics]::FromImage($crop).DrawImage($full,
  (New-Object System.Drawing.Rectangle 0,0,$w,$h),
  (New-Object System.Drawing.Rectangle ($r.L-$vs.Left),($r.T-$vs.Top),$w,$h),
  [System.Drawing.GraphicsUnit]::Pixel)
$crop.Save("$env:TEMP\eh_shot.png")
```

Notes:
- The file `ListView` is virtualized and **does not expose rows to UI Automation** — select a file
  with a real mouse click into the list + arrow keys (`SendKeys`) rather than `SelectionItemPattern`.
- The details strip and video timeline only appear when a file is selected, so select one first.
- `ffmpeg` is available for generating test media, e.g.
  `ffmpeg -f lavfi -i testsrc=size=1280x720:rate=30 -t 3 -pix_fmt yuv420p test.mp4`.
- To unit-test a `Services/*` interop file in isolation, a throwaway console csproj that
  `<Compile Include>`s the single file (absolute path) is faster than launching the whole app.

## CI

`.github/workflows/ci.yml` runs `build.ps1 -Version 0.0.0-ci` on `windows-latest` and uploads the
zip. That publish targets `src/ExplorerHelper/ExplorerHelper.csproj` by path and there's no
`ProjectReference` between the projects, so a second step builds
`ExplorerHelper.ShellExtension` explicitly — without it a break in the extension's hand-declared
COM vtables reaches `main` unnoticed (issue #37). `release.yml` fires on `v*` tags, checks the
csproj `<Version>` against the tag, derives the version from the tag, and adds the Inno installer.
`pages.yml` deploys `docs/` on changes under that path.
