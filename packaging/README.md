# Windows 11 top-level context menu (sparse MSIX)

This folder builds a **sparse MSIX package** that adds Explorer Helper's **“Clean this folder”**
entry to the *top level* of the Windows 11 right-click menu (no “Show more options” detour) —
issue [#11](https://github.com/JacobPoteet/ExplorerHelper/issues/11).

The classic per-user registry entry (from the installer or the in-app **Add context menu** button)
still works and needs none of this. This package is the modern Win11 path.

## How it works

| Piece | Role |
| --- | --- |
| [`src/ExplorerHelper.ShellExtension`](../src/ExplorerHelper.ShellExtension) | A COM `IExplorerCommand` handler. Windows shows its title and calls `Invoke`, which launches `ExplorerHelper.exe` on the clicked folder. Built with `EnableComHosting`, so the output includes `ExplorerHelper.ShellExtension.comhost.dll` — the native in-proc COM server Windows loads. |
| [`AppxManifest.xml`](AppxManifest.xml) | Declares the package identity, registers the COM server (`windows.comServer` → the comhost DLL), and attaches the verb to `Directory` and `Directory\Background` via `windows.fileExplorerContextMenus`. |
| [`Build-SparsePackage.ps1`](Build-SparsePackage.ps1) | Publishes the payload, packs the signed `.msix`, creates/uses a self-signed cert, and (optionally) installs. |

“Sparse” = the signed `.msix` carries only the manifest and the `Images\` logos. The real payload
(`ExplorerHelper.exe`, the comhost DLL, and their runtime files) lives in an **external location**
folder on disk, referenced through `uap10:AllowExternalContent`.

## Prerequisites

- **Windows 11** (the top-level menu API is `desktop5`; on Windows 10 the entry falls back into the classic menu).
- **.NET SDK 8+** — to build/publish. COM hosting does **not** support self-contained output, so the
  payload is framework-dependent: the **.NET 8 Desktop Runtime** must be installed on the target machine
  (`winget install Microsoft.DotNet.DesktopRuntime.8`).
- **Windows 10/11 SDK** — provides `makeappx.exe` and `signtool.exe`
  (`winget install Microsoft.WindowsSDK.10.0.26100` or any recent version). The script auto-locates them.

## Build and install

From an **elevated** PowerShell (admin is needed once, to trust the dev certificate machine-wide):

```powershell
cd packaging
./Build-SparsePackage.ps1 -Install
```

That will:

1. Publish the app + shell extension into `artifacts/sparse/ExternalLocation`.
2. Pack `artifacts/ExplorerHelper-Sparse-<version>.msix` and sign it with a self-signed cert
   (`CN=Explorer Helper Dev`, created on first run under `Cert:\CurrentUser\My`).
3. Import that cert into `LocalMachine\TrustedPeople` and register the package with its external location.

Right-click any folder afterward → **Clean this folder** appears at the top of the menu.

### Build without installing

```powershell
./Build-SparsePackage.ps1          # produces + signs the .msix, prints the two install commands
```

Then, elevated:

```powershell
Import-Certificate -FilePath ..\artifacts\ExplorerHelperDev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path ..\artifacts\ExplorerHelper-Sparse-<version>.msix -ExternalLocation <repo>\artifacts\sparse\ExternalLocation
```

### Uninstall

```powershell
./Build-SparsePackage.ps1 -Uninstall
```

## Notes and caveats

- **Signing is required.** MSIX won't install unless the signing certificate is trusted on the machine.
  The self-signed cert here is for local/dev use; for distribution, sign with a cert from a trusted CA
  and set `Publisher=` in `AppxManifest.xml` to that cert's subject.
- **CLSID.** The `Clsid` in the manifest verbs must match `ExplorerHelperCommand`'s `[Guid]`
  (`21E1DA0A-3D1D-4678-B6F5-60FFE2D6C26B`). Change one, change the other.
- **Menu didn't update?** Restart Explorer (`taskkill /f /im explorer.exe & start explorer.exe`) or sign out/in.
- The `Images\*.png` logos are simple placeholders — replace them with real branding before shipping.
- This packaging flow is intentionally kept out of the main `build.ps1`/CI, which continue to produce the
  zero-dependency self-contained installer. It requires SDK tooling and signing that don't belong in the
  standard release pipeline.
