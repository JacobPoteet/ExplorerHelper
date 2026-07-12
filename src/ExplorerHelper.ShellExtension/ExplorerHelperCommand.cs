using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ExplorerHelper.ShellExtension;

/// <summary>
/// The Windows 11 top-level context-menu command (issue #11). Windows loads this class as an
/// <c>IExplorerCommand</c> through the COM server declared in the sparse MSIX package, shows
/// "Clean this folder" on folders and folder backgrounds, and calls <see cref="Invoke"/> when
/// clicked — which launches ExplorerHelper.exe pointed at that folder.
///
/// The CLSID here must match the one in the AppxManifest.xml verb declaration.
/// </summary>
[ComVisible(true)]
[Guid(ClsidString)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IExplorerCommand))]
public sealed class ExplorerHelperCommand : IExplorerCommand
{
    internal const string ClsidString = "21E1DA0A-3D1D-4678-B6F5-60FFE2D6C26B";

    private const string Title = "Clean this folder";
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    // EXPCMDSTATE / EXPCMDFLAGS
    private const uint ECS_ENABLED = 0x0;
    private const uint ECF_DEFAULT = 0x0;

    public int GetTitle(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName)
    {
        ppszName = Title;
        return S_OK;
    }

    public int GetIcon(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszIcon)
    {
        // Point Explorer at the app exe so the menu entry shows the app's own icon.
        ppszIcon = LocateApp();
        return ppszIcon is null ? E_NOTIMPL : S_OK;
    }

    public int GetToolTip(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszInfotip)
    {
        ppszInfotip = null;
        return E_NOTIMPL; // no custom tooltip
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = Guid.Empty;
        return S_OK;
    }

    public int GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = ECS_ENABLED; // always shown and enabled on the item types we register for
        return S_OK;
    }

    public int Invoke(IShellItemArray? psiItemArray, IntPtr pbc)
    {
        // Never let an exception escape into explorer.exe.
        try
        {
            var folder = GetFolderPath(psiItemArray);
            var exe = LocateApp();
            if (folder is null || exe is null)
                return S_OK;

            Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = $"\"{folder}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
        }
        catch
        {
            // Swallow — a shell extension must not crash the shell.
        }
        return S_OK;
    }

    public int GetFlags(out uint pFlags)
    {
        pFlags = ECF_DEFAULT;
        return S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        ppEnum = IntPtr.Zero;
        return E_NOTIMPL; // no submenu
    }

    /// <summary>
    /// Resolves the folder the command was invoked on. Right-clicking a folder passes that folder
    /// as the first item; right-clicking the background of an open folder passes the open folder.
    /// If a file was somehow selected, its containing directory is used.
    /// </summary>
    private static string? GetFolderPath(IShellItemArray? items)
    {
        if (items is null)
            return null;

        items.GetCount(out var count);
        if (count == 0)
            return null;

        items.GetItemAt(0, out var item);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
        if (string.IsNullOrEmpty(path))
            return null;

        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }

    /// <summary>Finds ExplorerHelper.exe, which is packaged alongside this DLL.</summary>
    private static string? LocateApp()
    {
        var dir = Path.GetDirectoryName(typeof(ExplorerHelperCommand).Assembly.Location);
        if (dir is null)
            return null;
        var exe = Path.Combine(dir, "ExplorerHelper.exe");
        return File.Exists(exe) ? exe : null;
    }
}
