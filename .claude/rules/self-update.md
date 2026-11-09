---
paths:
  - "src/ExplorerHelper/Services/UpdateService.cs"
  - ".github/workflows/release.yml"
  - "build.ps1"
---

# Self-update

- **Self-update** (`UpdateService`): a background check hits the GitHub releases API and, if a newer
  tag exists, shows an update pill (wired through `MainViewModel`, gated by the `CheckForUpdates`
  setting). `CheckForUpdateAsync` never throws: offline/rate-limited/malformed all read as "no
  update". Only *installed* copies (detected via the Inno Setup per-user uninstall key,
  `IsInstalledCopy()`) get the seamless silent-install-and-relaunch path; portable copies are sent
  to the release page. Release tags and the `ExplorerHelper-Setup-*.exe` asset name are the contract
  the check relies on, so keep them stable.
