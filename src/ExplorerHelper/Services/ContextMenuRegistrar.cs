using Microsoft.Win32;

namespace ExplorerHelper.Services;

/// <summary>
/// Registers the "Clean this folder" entry in the Explorer context menu.
/// Uses HKCU so no admin rights are required. On Windows 11 the entry appears
/// under "Show more options" (the classic menu).
/// </summary>
public static class ContextMenuRegistrar
{
    private const string MenuText = "Clean this folder";
    private const string FolderKey = @"Software\Classes\Directory\shell\ExplorerHelper";
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\ExplorerHelper";

    public static bool IsRegistered =>
        Registry.CurrentUser.OpenSubKey(FolderKey) is not null;

    public static void Register(string exePath)
    {
        // Right-clicking a folder passes the folder as %1;
        // right-clicking the background of an open folder passes it as %V.
        Write(FolderKey, exePath, "%1");
        Write(BackgroundKey, exePath, "%V");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FolderKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(BackgroundKey, throwOnMissingSubKey: false);
    }

    private static void Write(string keyPath, string exePath, string argToken)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(string.Empty, MenuText);
        key.SetValue("Icon", exePath);
        using var command = key.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{exePath}\" \"{argToken}\"");
    }
}
