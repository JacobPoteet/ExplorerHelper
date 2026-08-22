using System.IO;
using System.Runtime.InteropServices;

namespace ExplorerHelper.Services;

/// <summary>
/// Deletes files via the shell so they go to the Recycle Bin instead of being
/// permanently removed — a cleanup tool should always leave an undo path.
///
/// Uses <c>IFileOperation</c> (the modern replacement for <c>SHFileOperation</c>) so we can
/// capture each item's exact location inside the Recycle Bin. That captured path lets the
/// in-app undo journal (issue #9) restore the file to where it came from without having to
/// parse the Recycle Bin's localized "Original Location" column or invoke a localized verb.
/// </summary>
public static class RecycleBinService
{
    /// <summary>
    /// Outcome of a recycle. <c>Deleted</c> says whether the file left the folder;
    /// <c>RecycledPath</c> is its <c>$R…</c> location inside the bin, which the shell
    /// does not always report. The two are separate because "gone but not restorable" is a real
    /// state: the row must drop out of the list even when no undo can be offered (issue #32).
    /// </summary>
    public readonly record struct RecycleResult(bool Deleted, string? RecycledPath)
    {
        public static readonly RecycleResult Failed = new(false, null);
    }

    /// <summary>
    /// Sends a single path to the Recycle Bin. Never throws — a path that vanished between
    /// enumeration and delete (removed in Explorer, SD card pulled, share dropped) comes back as
    /// <see cref="RecycleResult.Failed"/> so callers can count it instead of unwinding a batch.
    /// </summary>
    public static RecycleResult MoveToRecycleBin(string path)
    {
        IFileOperation? op = null;
        try
        {
            op = (IFileOperation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(CLSID_FileOperation)!)!;
            op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);

            // SHCreateItemFromParsingName is PreserveSig = false, so a missing path arrives as a
            // COMException rather than a failure HRESULT.
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);

            var sink = new RecycleSink();
            op.DeleteItem(item, sink);
            op.PerformOperations();

            if (op.GetAnyOperationsAborted() || !sink.Deleted)
                return RecycleResult.Failed;
            return new RecycleResult(true, sink.RecycledPath);
        }
        catch
        {
            return RecycleResult.Failed;
        }
        finally
        {
            if (op is not null)
                Marshal.FinalReleaseComObject(op);
        }
    }

    /// <summary>
    /// Restores an item previously sent to the Recycle Bin back to its original location.
    /// Moves the <c>$R…</c> content file back and removes the sibling <c>$I…</c> metadata file so
    /// the Recycle Bin doesn't keep a dangling entry. Returns false if the original name is taken
    /// again or the content is no longer in the bin (e.g. the bin was emptied).
    /// </summary>
    public static bool Restore(string recycledPath, string originalPath)
    {
        var isDir = Directory.Exists(recycledPath);
        if (!isDir && !File.Exists(recycledPath))
            return false; // emptied from the bin, or already restored
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
            return false; // something else now occupies the original name

        var parent = Path.GetDirectoryName(originalPath);
        if (parent is not null)
            Directory.CreateDirectory(parent);

        if (isDir)
            Directory.Move(recycledPath, originalPath);
        else
            File.Move(recycledPath, originalPath);

        // The Recycle Bin stores content as "$R<id><ext>" alongside metadata "$I<id><ext>".
        // With the content gone, delete the orphaned metadata so no ghost entry lingers.
        var metadataPath = MetadataSiblingOf(recycledPath);
        if (metadataPath is not null && File.Exists(metadataPath))
        {
            try { File.Delete(metadataPath); }
            catch { /* best effort — a leftover $I is harmless */ }
        }
        return true;
    }

    /// <summary>Maps a Recycle Bin "$R…" content path to its "$I…" metadata sibling.</summary>
    private static string? MetadataSiblingOf(string recycledPath)
    {
        var dir = Path.GetDirectoryName(recycledPath);
        var name = Path.GetFileName(recycledPath);
        if (dir is null || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.Combine(dir, "$I" + name[2..]);
    }

    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOERRORUI = 0x0400;

    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>
    /// Records the outcome of each item via <c>PostDeleteItem</c>: whether the delete succeeded,
    /// and where it landed. The shell reports <c>psiNewlyCreated</c> as null in some cases (a
    /// bypassed bin, a volume with no bin), so a successful delete can still yield no path.
    /// </summary>
    private sealed class RecycleSink : IFileOperationProgressSink
    {
        public string? RecycledPath { get; private set; }
        public bool Deleted { get; private set; }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            if (hrDelete < 0)
                return 0; // failed HRESULT — leave Deleted false

            Deleted = true;
            if (psiNewlyCreated is not null)
            {
                psiNewlyCreated.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                RecycledPath = path;
            }
            return 0; // S_OK
        }

        // Remaining callbacks are unused; returning S_OK lets the operation proceed.
        public int StartOperations() => 0;
        public int FinishOperations(int hrResult) => 0;
        public int PreRenameItem(uint f, IShellItem i, string n) => 0;
        public int PostRenameItem(uint f, IShellItem i, string n, int hr, IShellItem? c) => 0;
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem? c) => 0;
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem? c) => 0;
        public int PreDeleteItem(uint f, IShellItem i) => 0;
        public int PreNewItem(uint f, IShellItem d, string n) => 0;
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int hr, IShellItem? c) => 0;
        public int UpdateProgress(uint total, uint soFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        // Declared in full vtable order; only the members we call carry real signatures.
        uint Advise(IFileOperationProgressSink pfops);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage(IntPtr pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, IntPtr pszNewName, IFileOperationProgressSink? pfopsItem);
        void RenameItems(IntPtr pUnkItems, IntPtr pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDest, IntPtr pszNewName, IFileOperationProgressSink? pfopsItem);
        void MoveItems(IntPtr punkItems, IShellItem psiDest);
        void CopyItem(IShellItem psiItem, IShellItem psiDest, IntPtr pszCopyName, IFileOperationProgressSink? pfopsItem);
        void CopyItems(IntPtr punkItems, IShellItem psiDest);
        void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestFolder, uint dwAttrs, IntPtr pszName, IntPtr pszTemplate, IFileOperationProgressSink? pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrRename, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrMove, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
