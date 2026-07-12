using System.Runtime.InteropServices;

namespace ExplorerHelper.ShellExtension;

/// <summary>
/// The <c>IExplorerCommand</c> contract Windows uses to render and invoke a context-menu command.
/// We implement it; the shell calls it. Every member returns an HRESULT.
/// </summary>
[ComImport]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
    [PreserveSig] int GetIcon(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszIcon);
    [PreserveSig] int GetToolTip(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszInfotip);
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
    [PreserveSig] int GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);
    [PreserveSig] int Invoke(IShellItemArray? psiItemArray, IntPtr pbc);
    [PreserveSig] int GetFlags(out uint pFlags);
    [PreserveSig] int EnumSubCommands(out IntPtr ppEnum);
}

/// <summary>Shell wrapper for the set of items a command was invoked on. We only read from it.</summary>
[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    // Only GetCount and GetItemAt are called; earlier members are declared to keep the vtable order.
    void BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppvOut);
    void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    void GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    void GetCount(out uint pdwNumItems);
    void GetItemAt(uint dwIndex, out IShellItem ppsi);
    void EnumItems(out IntPtr ppenumShellItems);
}

/// <summary>A single shell item; we call <see cref="GetDisplayName"/> to read its filesystem path.</summary>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}
