# Explorer Helper

[![Download latest](https://img.shields.io/github/v/release/JacobPoteet/ExplorerHelper?label=download&sort=semver)](https://github.com/JacobPoteet/ExplorerHelper/releases/latest)
[![CI](https://github.com/JacobPoteet/ExplorerHelper/actions/workflows/ci.yml/badge.svg)](https://github.com/JacobPoteet/ExplorerHelper/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Project site](https://img.shields.io/badge/site-jacobpoteet.github.io-7C5CFC)](https://jacobpoteet.github.io/ExplorerHelper/)

Clean and organize any folder straight from the Windows Explorer right-click menu.
Right-click a folder (or the background of an open folder), hit **Clean this folder**,
and triage its contents with previews and keyboard shortcuts.

**🌐 [Visit the project site](https://jacobpoteet.github.io/ExplorerHelper/)** for a visual
tour of the app — or jump straight to the
[latest release](https://github.com/JacobPoteet/ExplorerHelper/releases/latest).

## Features

- **Explorer context menu** — "Clean this folder" on folders and folder backgrounds (per-user, no admin required)
- **Triage mode** — the fastest way to clear out a folder: review files one at a time like a
  dating app, swipe (or `←`/`→`) to reject or keep, `↓` skips, `Backspace` rewinds. Nothing
  touches the disk while you swipe — a review screen shows both piles (swap or unmark any file),
  then one **Commit** applies everything. The commit has three independent switches: send rejects
  to the Recycle Bin (on by default, or leave them in place), and move *or* copy the keepers to a
  folder of your choice (e.g. pull the good shots off an SD card without touching it). One
  `Ctrl`+`Z` reverses the whole commit. `K`/`X`/`U` flag files straight from the list too, so you
  can triage without ever leaving the main list view
- **Previews** — images render natively, videos play inline with a **scrub timeline** (play/pause,
  elapsed/total time, and a draggable slider to jump anywhere in the clip), audio shows a speaker
  so you know a file is selected and playing, PDFs render via the built-in Edge WebView2 viewer,
  and everything else shows the same thumbnail Explorer would
- **Preview details** — a strip under the preview shows the selected file's metadata (type, size, resolution, length, frame rate, bit rate, created/modified dates). Each detail type is toggleable in **Settings**, and media-only rows appear only for files that have them
- **Shell thumbnails** in the file list for every file type Windows knows how to thumbnail
- **Keyboard triage** — `Del` sends to the Recycle Bin (never permanent deletion), `F2` renames, `Enter` opens
- **Undo** — `Ctrl`+`Z` reverses the last rename, delete, or triage commit; deleted files come straight back out of the Recycle Bin
- **Quick rename** — review files one by one and name them fast: type a name, press `Enter` to rename and jump to the next file. Collisions auto-number (`Clip`, `Clip 2`, `Clip 3`…), the extension is preserved, and a session name palette re-applies recent names in a click — built for triaging a folder of clips or screenshots
- **Quick-use buttons** — build a name in one click from your own preset buttons (add them from the `+` under the rename box, manage them in **Settings**), plus two dynamic date buttons that insert today's date or the selected file's created date. The date formats are configurable in Settings using standard .NET date/time patterns (`yyyy-MM-dd`, `hh:mm tt`, …); the button row scrolls horizontally when it fills up
- **Filter & sort** by name, size, date, or type — folders always listed first
- **Automatic updates** — the app checks GitHub for a newer release on startup (toggleable in
  Settings); when one ships, an update button appears in the toolbar. On an installed copy one
  click downloads it, installs silently, and restarts the app right where you were; the portable
  build opens the release page instead, since swapping a running loose exe isn't safe
- Multi-select delete, open in Explorer, one-click context-menu install/uninstall from inside the app

Planned work lives in the [issue tracker](https://github.com/JacobPoteet/ExplorerHelper/issues).

## Install

**[⬇ Download the latest installer](https://github.com/JacobPoteet/ExplorerHelper/releases/latest)** —
grab `ExplorerHelper-Setup-*.exe` from the release assets and run it.
It installs per-user (no admin prompt) and registers the context-menu entries; uninstalling
removes them again. A portable zip is also published with each release — with the portable
version, use the **Add context menu** button inside the app.

> **Windows 11 note:** the entry appears under **Show more options** (the classic menu),
> or immediately when you `Shift`+right-click. For a **top-level** Windows 11 menu entry (no
> "Show more options" detour), build the sparse MSIX package — see [`packaging/README.md`](packaging/README.md).

## Building locally

Requires the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer.

```powershell
# Debug run
dotnet run --project src/ExplorerHelper

# Publish self-contained exe + portable zip into artifacts/
./build.ps1 -Version 0.6.0

# Also compile the installer (requires Inno Setup 6: winget install JRSoftware.InnoSetup)
./build.ps1 -Version 0.6.0 -Installer
```

## Releasing

From an up-to-date `main` with a clean tree:

```powershell
./release.ps1 -Version 0.7.0
```

That bumps `Directory.Build.props`, commits, tags `v0.7.0`, and pushes the commit and tag. The
release workflow then builds the zip and installer and attaches them to a GitHub Release with
generated notes. Watch it with `gh run watch`.

Add `-WhatIf` to see the plan without touching anything, or `-Branch <name>` to cut from somewhere
other than `main`. The script refuses to run on the wrong branch, with uncommitted changes, out of
sync with origin, or onto a tag that already exists — all checked before it writes anything.

**The version lives in `Directory.Build.props` and nowhere else.** Both projects inherit it and
`build.ps1` defaults to it. Don't edit it by hand: `release.yml` refuses to build a tag that
disagrees with it, and tagging a commit that predates the bump is how v0.6.0 failed (issue #46).

If a release does fail the version check, the tag is pointing at the wrong commit. Land the bump,
then move it:

```powershell
git checkout main; git pull; git tag -f v0.7.0; git push origin -f v0.7.0
```

## Tech notes

- **WPF on .NET 8** (`net8.0-windows`), MVVM via CommunityToolkit.Mvvm
- Two projects: the app, plus `ExplorerHelper.ShellExtension`, an `IExplorerCommand` COM handler used
  only by the optional Windows 11 sparse MSIX package
- Thumbnails come from the Windows shell (`IShellItemImageFactory`) — the same images Explorer shows
- Media metadata in the preview details strip (resolution, length, frame rate, bit rate) is read from
  the shell property store (`IPropertyStore`), so there's no codec dependency
- Deletes go through `IFileOperation` with `FOF_ALLOWUNDO`, so everything lands in the Recycle Bin.
  The operation's progress sink captures each item's `$R…` path inside the bin, which is what lets
  in-app undo restore files without parsing the bin's localized columns
- Context menu entries live under `HKCU\Software\Classes\Directory\shell` (and `Directory\Background\shell`)

## License

[MIT](LICENSE)
