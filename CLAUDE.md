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
WebView2 (PDF preview). Dark theme is hard-set in `App.xaml`.

## Build / run / test

```powershell
# Build (CI uses the .sln in Release; dotnet 9 SDK on CI, but target is net8.0-windows)
dotnet build ExplorerHelper.sln -c Debug

# Run — the first existing directory in args is the folder to open; with none, a folder picker shows
dotnet run --project src/ExplorerHelper -- "C:\some\folder"
# or run the built exe directly:
src/ExplorerHelper/bin/Debug/net8.0-windows/ExplorerHelper.exe "C:\some\folder"

# Publish self-contained exe + portable zip into artifacts/ (also the CI smoke test)
./build.ps1 -Version 0.1.0
./build.ps1 -Version 0.1.0 -Installer   # also builds Inno Setup installer
```

There is **no test project**. Verify changes by driving the running app (see below).

## Architecture — where things live

- `src/ExplorerHelper/App.xaml.cs` — startup; picks the folder from args or a dialog. A global
  `DispatcherUnhandledException` handler shows a MessageBox but keeps running (so a silent crash
  still leaves the window up — check for an error dialog when verifying).
- `MainWindow.xaml(.cs)` — the shell: toolbar, the file `ListView` (custom-retemplated GridView),
  the right column (preview → details strip → quick-rename bar), and the Settings popup. Most
  view logic (sorting indicators, quick-rename flow, keyboard triage) is in the code-behind.
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
  (media metadata via the shell property store).
- `Models/` — `FileEntry` (the list item), `TriageFlag`, `TypeFilter`, `PreviewDetail`.

## Conventions that aren't obvious

- **Issue traceability:** features are annotated in code with `(issue #N)` comments. Match this
  when adding a feature that closes an issue.
- **Preview file handles are released before disk ops.** A live `MediaElement`/WebView2 keeps the
  file open, so `Preview.Clear()` then `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)`
  is used before delete/rename/move so the handle is freed first (issue #1). Follow this pattern
  for any new code that mutates a file that might be previewing.
- **Settings persistence:** user prefs live in `AppSettings` → `%APPDATA%\ExplorerHelper\settings.json`.
  It's forgiving of missing/corrupt files (falls back to defaults) and `Normalized()` fills in
  nulls from older files. New persisted settings: add a property, default it, and normalize it.
- **Shell interop needs backslash paths.** `SHCreateItemFromParsingName` /
  `SHGetPropertyStoreFromParsingName` fail on forward-slash paths and return null. `FileEntry.FullPath`
  (from `FileSystemInfo.FullName`) is already backslash — pass it through unchanged.
- **PROPVARIANT interop** (`ShellPropertyService`): the struct uses two `IntPtr` union slots so it's
  the right size on x86 and x64; values are coerced with the `propsys.dll` `PropVariantToXxx` helpers
  rather than parsing the union by hand. Always `PropVariantClear` after reading.
- **Background work** (thumbnails, media metadata) runs on `Task.Run` with a `CancellationTokenSource`
  that's cancelled when the selection changes; results are marshalled back via
  `Application.Current.Dispatcher` and dropped if the selection has moved on.

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

`.github/workflows/ci.yml` builds the solution in Release and runs `build.ps1` as a packaging
smoke test on `windows-latest`. `release.yml` and `pages.yml` handle releases and the landing page.
