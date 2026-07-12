using System.IO;
using System.Text.Json;
using ExplorerHelper.Models;

namespace ExplorerHelper.Services;

/// <summary>
/// User preferences that outlive a single window: the quick-rename preset buttons and the
/// date formats used by the two dynamic date buttons (issue #14). Persisted as JSON under
/// <c>%APPDATA%\ExplorerHelper\settings.json</c> — per-user, no admin required, and forgiving
/// of a missing or corrupt file (falls back to defaults so the app always starts).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Preset strings shown as one-click buttons under the quick-rename box.</summary>
    public List<string> QuickNameButtons { get; set; } = [];

    /// <summary>.NET custom date/time format for the "today" dynamic button (e.g. yyyy-MM-dd).</summary>
    public string TodayDateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>.NET custom date/time format for the file's created-date dynamic button.</summary>
    public string CreatedDateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Which preview detail rows are shown under the preview (issue #20). Stored as the detail
    /// keys from <see cref="PreviewDetailKinds"/>; a null value means "never configured" and
    /// falls back to <see cref="PreviewDetailKinds.DefaultEnabled"/>.
    /// </summary>
    public List<string>? EnabledPreviewDetails { get; set; }

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ExplorerHelper");
            return Path.Combine(dir, "settings.json");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Reads settings from disk, returning defaults if the file is absent or unreadable.</summary>
    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                    return loaded.Normalized();
            }
        }
        catch
        {
            // A corrupt or unreadable settings file must never block startup — fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Writes the current settings to disk, swallowing IO errors (best-effort persistence).</summary>
    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            // Persisting preferences is best-effort; losing them is preferable to crashing the app.
        }
    }

    /// <summary>Guards against nulls from an older/hand-edited file and blank date formats.</summary>
    private AppSettings Normalized()
    {
        QuickNameButtons ??= [];
        if (string.IsNullOrWhiteSpace(TodayDateFormat))
            TodayDateFormat = "yyyy-MM-dd";
        if (string.IsNullOrWhiteSpace(CreatedDateFormat))
            CreatedDateFormat = "yyyy-MM-dd";
        EnabledPreviewDetails ??= [.. PreviewDetailKinds.DefaultEnabled];
        return this;
    }
}
