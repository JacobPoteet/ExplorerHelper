# Explorer Helper

[![Download latest](https://img.shields.io/github/v/release/JacobPoteet/ExplorerHelper?label=download&sort=semver)](https://github.com/JacobPoteet/ExplorerHelper/releases/latest)
[![CI](https://github.com/JacobPoteet/ExplorerHelper/actions/workflows/ci.yml/badge.svg)](https://github.com/JacobPoteet/ExplorerHelper/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Clean and organize any folder straight from the Windows Explorer right-click menu.
Right-click a folder (or the background of an open folder), hit **Clean this folder**,
and triage its contents with previews and keyboard shortcuts.

## Features

- **Explorer context menu** — "Clean this folder" on folders and folder backgrounds (per-user, no admin required)
- **Previews** — images render natively, videos/audio play inline, PDFs render via the built-in Edge WebView2 viewer, and everything else shows the same thumbnail Explorer would
- **Shell thumbnails** in the file list for every file type Windows knows how to thumbnail
- **Keyboard triage** — `Del` sends to the Recycle Bin (never permanent deletion), `F2` renames, `Enter` opens
- **Filter & sort** by name, size, date, or type — folders always listed first
- Multi-select delete, open in Explorer, one-click context-menu install/uninstall from inside the app

### Roadmap

- Bulk rename with pattern/regex and live preview
- Duplicate finder (size prefilter + hash)
- Sort into subfolders by extension/date rules
- Empty-folder sweep and folder size breakdown
- "Untouched for N months" age filter
- Undo journal for rename/move sessions
- Windows 11 top-level context menu entry (`IExplorerCommand` + sparse MSIX)

## Install

**[⬇ Download the latest installer](https://github.com/JacobPoteet/ExplorerHelper/releases/latest)** —
grab `ExplorerHelper-Setup-*.exe` from the release assets and run it.
It installs per-user (no admin prompt) and registers the context-menu entries; uninstalling
removes them again. A portable zip is also published with each release — with the portable
version, use the **Add context menu** button inside the app.

> **Windows 11 note:** the entry appears under **Show more options** (the classic menu),
> or immediately when you `Shift`+right-click.

## Building locally

Requires the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer.

```powershell
# Debug run
dotnet run --project src/ExplorerHelper

# Publish self-contained exe + portable zip into artifacts/
./build.ps1 -Version 0.1.0

# Also compile the installer (requires Inno Setup 6: winget install JRSoftware.InnoSetup)
./build.ps1 -Version 0.1.0 -Installer
```

## Releasing

CI builds every push and PR (`.github/workflows/ci.yml`). To cut a release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The release workflow builds the zip and installer with that version number and attaches
both to a GitHub Release with generated notes.

## Tech notes

- **WPF on .NET 8** (`net8.0-windows`), MVVM via CommunityToolkit.Mvvm
- Thumbnails come from the Windows shell (`IShellItemImageFactory`) — the same images Explorer shows
- Deletes go through `SHFileOperation` with `FOF_ALLOWUNDO`, so everything lands in the Recycle Bin
- Context menu entries live under `HKCU\Software\Classes\Directory\shell` (and `Directory\Background\shell`)

## License

[MIT](LICENSE)
