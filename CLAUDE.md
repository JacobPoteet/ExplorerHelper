# Explorer Helper

Guidance for working in this repo. It front-loads the architecture and conventions so you don't
have to re-derive them each session.

Follows the workflow standard in the user CLAUDE.md: squash merges, `/ship` to land a PR,
`/release` to cut one, bugs fixed when found. Deviations are listed at the end.

## What this is

**Explorer Helper** is a Windows WPF desktop app (.NET 8, `net8.0-windows`) that adds a
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

# Run: the first existing directory in args is the folder to open; with none, a folder picker shows
dotnet run --project src/ExplorerHelper -- "C:\some\folder"
# or run the built exe directly:
src/ExplorerHelper/bin/Debug/net8.0-windows/ExplorerHelper.exe "C:\some\folder"

# Publish self-contained exe + portable zip into artifacts/ (also the CI smoke test).
# Version comes from Directory.Build.props; -Version only overrides it.
./build.ps1
./build.ps1 -Installer                  # also builds Inno Setup installer

# Cut a release in two steps (see "Cutting a release"): a bump PR, then the tag.
./release.ps1 -Version 0.7.0
./release.ps1 -Version 0.7.0 -Tag
```

There is **no test project**. See "Verify a change".

## Architecture: where things live

Two projects. `src/ExplorerHelper` is the app; `src/ExplorerHelper.ShellExtension` is a separate
x64, framework-dependent COM handler (`IExplorerCommand`) used only by the optional Windows 11
sparse MSIX (issue #11); see `packaging/README.md`. No `ProjectReference` links them; the CLSID
in `ExplorerHelperCommand`'s `[Guid]` and in `AppxManifest.xml` is the whole contract.

Inside `src/ExplorerHelper`:

- `src/ExplorerHelper/App.xaml.cs`: startup; picks the folder from args or a dialog. A global
  `DispatcherUnhandledException` handler shows a MessageBox but keeps running (so a silent crash
  still leaves the window up; check for an error dialog when verifying).
- `MainWindow.xaml(.cs)`, the shell: toolbar, the location bar (back/forward/up + breadcrumb),
  the file `ListView` (custom-retemplated GridView), the right column (preview → details strip →
  quick-rename bar), and the Settings popup. Most view logic (sorting indicators, quick-rename
  flow, keyboard triage, navigation) is in the code-behind. Root grid rows are title bar, toolbar,
  location bar, split, status bar; add a row and every `Grid.Row` below it shifts.
- `ViewModels/MainViewModel.cs`: the bulk of the logic: folder loading, filtering/sorting,
  triage piles, undo journal, quick-rename, and preview-details computation. Uses
  `[ObservableProperty]`; partial hooks like `OnSelectedFileChanged(value)` are how selection
  side effects are wired.
- `Controls/PreviewPane.xaml(.cs)`: the preview surface (image / video+audio via `MediaElement`
  / PDF via WebView2 / generic shell thumbnail) **and** the video scrub timeline. Shared by the
  main window and the triage card.
- `Controls/TriageView.xaml(.cs)`: the full-screen swipe/keep/reject overlay + review screen.
  Hosts its own `PreviewPane` (`CardPreview`).
- `Services/`: `AppSettings` (JSON persistence), `ContextMenuRegistrar` (registry entries),
  `RecycleBinService`, `ShellThumbnailService` (shell thumbnails), `ShellPropertyService`
  (media metadata via the shell property store), `UpdateService` (self-update from GitHub releases),
  `FolderScanService` (folder item counts and subtree sizes), `TriageSession` (keep/reject marks,
  keyed by path so they outlive the folder they were made in).
- `Models/`: `FileEntry` (the list item), `TriageFlag`, `TypeFilter`, and `PreviewDetail.cs`,
  which holds three types: `PreviewDetailRow`, `PreviewDetailToggle`, and the `PreviewDetailKinds`
  catalogue (the key list plus `DefaultEnabled`, which turns on 7 of the 9 details).
- `CommitDialog.xaml(.cs)`: the triage commit dialog. Owns the three independent switches
  (recycle rejects / move / copy, issue #23) and the session-remembered destination.
- `RenameDialog.xaml(.cs)`: the F2 modal, with the Explorer-style stem pre-selection.
- `Converters/InverseBoolToVisibilityConverter.cs`: used for empty-state hints bound to `HasItems`.

## Conventions that aren't obvious

Subsystem rules live in `.claude/rules/` and load with the files they cover: `shell-interop.md`, `folder-stats.md`, `self-update.md` and `versioning.md`.

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
- **Settings persistence:** user prefs live in `AppSettings` → `%APPDATA%\ExplorerHelper\settings.json`.
  It's forgiving of missing/corrupt files (falls back to defaults) and `Normalized()` fills in
  nulls from older files. New persisted settings: add a property, default it, and normalize it.
- **Navigation** (issue #41) is a two-stack browser model on `MainViewModel`: `NavigateTo` pushes
  the current folder onto `_backStack` and clears `_forwardStack`; `NavigateBack`/`NavigateForward`
  move between them via `Step`. `LoadFolder` compares against the outgoing `FolderPath` to tell an
  actual move from a `Refresh`/`Undo` reload, and only a move restores the remembered selection
  from `_lastSelectedByFolder`. Enter and double-click enter a folder; files still go to the shell.
- **Triage marks live in `TriageSession`, not on the folder** (issue #43). They're keyed by full
  path, so browsing away and back keeps them, and a commit can span folders. Every reload builds
  new `FileEntry` objects, so `LoadFolder` calls `_triage.Rebind(_allEntries)` to re-apply flags
  *and* adopt the new instances. Skip that and the list shows an unmarked file while the review
  pile still holds the stale object for the same path. Anything that moves or removes a file has to
  tell the session: `Delete` calls `Forget`, both rename paths call `Rename`, and `CommitTriage`
  forgets only the marks it actually acted on (a keeper left in place because no destination was
  set is still pending). Because marks are now invisible from other folders, the toolbar pill
  (`HasPendingMarks`/`PendingMarksSummary`) and the commit dialog's per-folder breakdown are load-
  bearing, not decoration.

## Verify a change

There is no test project. Build (`dotnet build ExplorerHelper.sln -c Debug`), then for a visual or
UI change tell Jacob which screen and control to look at and what he should see, and let him review
it in the running app. Do not launch, drive or screenshot the app unless he asks, and never stop
or restart a copy he has running. When he asks, `.claude/rules/ui-verification.md` and
`tools/Capture-Window.ps1` cover driving and capturing the window.

## CI

`.github/workflows/ci.yml` runs `build.ps1 -Version 0.0.0-ci` on `windows-latest` and uploads the
zip. That publish targets `src/ExplorerHelper/ExplorerHelper.csproj` by path and there's no
`ProjectReference` between the projects, so a second step builds
`ExplorerHelper.ShellExtension` explicitly; without it a break in the extension's hand-declared
COM vtables reaches `main` unnoticed (issue #37). `release.yml` fires on `v*` tags, checks the
csproj `<Version>` against the tag, derives the version from the tag, and adds the Inno installer.
`pages.yml` deploys `docs/` on changes under that path.

## Cutting a release

Run `/release`; it uses `release.ps1` for the bump and the tag.

1. `./release.ps1 -Version x.y.z` from `main` checks `main` is clean and in sync and the tag is new,
   then bumps `Directory.Build.props` on `release/vx.y.z`, pushes it and opens a PR labelled
   `release`. `-WhatIf` prints the plan.
2. Squash-merge the PR with `/ship`, then pull `main`.
3. Run the wiki pass.
4. `./release.ps1 -Version x.y.z -Tag` refuses unless `main`'s props file says `x.y.z`, then tags
   and pushes `vx.y.z`.

The tag push runs `release.yml`, which checks the version, builds the zip and the Inno installer,
and attaches both. The update check depends on the tag form and the `ExplorerHelper-Setup-*.exe`
asset name. `docs/` (the project page) deploys through `pages.yml`, separately.

## Documentation

The wiki holds the deep documentation: per-file behaviour, measured numbers, dead ends, the
Decision Log. Its path is in the gitignored `.claude/wiki-path.local`. The repo keeps `README.md`,
this file, `packaging/README.md`, and the project page in `docs/`.

## Deviations from the standard

| Deviation | Why |
| --- | --- |
| `docs/` holds the GitHub Pages project page | A published page, not documentation |
| `(issue #N)` comments in code | Feature traceability, used across the codebase |
