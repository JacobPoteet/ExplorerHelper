---
paths:
  - "src/ExplorerHelper/Services/Shell*.cs"
  - "src/ExplorerHelper/Services/RecycleBinService.cs"
  - "src/ExplorerHelper/Services/ContextMenuRegistrar.cs"
  - "src/ExplorerHelper.ShellExtension/**"
  - "src/ExplorerHelper/Controls/PreviewPane.xaml.cs"
---

# Shell interop

- **Shell interop needs backslash paths.** `SHCreateItemFromParsingName` /
  `SHGetPropertyStoreFromParsingName` fail on forward-slash paths and return null. `FileEntry.FullPath`
  (from `FileSystemInfo.FullName`) is already backslash; pass it through unchanged.
- **PROPVARIANT interop** (`ShellPropertyService`): the struct uses two `IntPtr` union slots so it's
  the right size on x86 and x64; values are coerced with the `propsys.dll` `PropVariantToXxx` helpers
  rather than parsing the union by hand. Always `PropVariantClear` after reading.
- **Background work** (thumbnails, media metadata, folder child counts) runs on `Task.Run` with a
  `CancellationTokenSource` that's cancelled when the selection changes; results are marshalled back via
  `Application.Current.Dispatcher` and dropped if the selection has moved on.
