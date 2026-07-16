using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace ExplorerHelper.Services;

/// <summary>A newer release on GitHub: its version, installer asset, and release page.</summary>
public sealed record UpdateInfo(Version Version, string InstallerUrl, string ReleasePageUrl);

/// <summary>
/// Self-update from GitHub releases. Checks the latest release tag against the running
/// version, downloads the Inno Setup installer asset, and hands off to a silent install
/// that relaunches the app when it finishes. Installed copies (detected via the
/// installer's per-user uninstall key) get the seamless path; portable copies are sent
/// to the release page instead, since replacing a running loose exe is not worth the risk.
/// </summary>
public static class UpdateService
{
    private const string Repo = "JacobPoteet/ExplorerHelper";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>Uninstall key Inno Setup writes for our AppId (per-user, HKCU).</summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7E4C1D2-9A3F-4E8B-A6D5-2F0C7E91B384}_is1";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ExplorerHelper", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The running app version, normalized to three components (matches release tags).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        }
    }

    /// <summary>
    /// Returns the latest release if it is newer than the running version, else null.
    /// Never throws — offline, rate-limited, or malformed responses all read as "no update".
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return null;
            latest = new Version(latest.Major, Math.Max(latest.Minor, 0), Math.Max(latest.Build, 0));
            if (latest <= CurrentVersion)
                return null;

            var releasePage = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";

            string? installerUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.StartsWith("ExplorerHelper-Setup-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (installerUrl is null)
                return null;

            return new UpdateInfo(latest, installerUrl, releasePage);
        }
        catch
        {
            // The update check must never surface an error — it just means no update news today.
            return null;
        }
    }

    /// <summary>
    /// True when this exe is running out of the folder the installer's uninstall entry points
    /// at — i.e. an installed copy that the silent installer can safely replace in place.
    /// </summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
                return false;
            var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            return exeDir is not null
                && string.Equals(installDir, Path.TrimEndingDirectorySeparator(exeDir),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads the installer asset to %TEMP%, reporting 0..1 progress, and
    /// returns the downloaded file's path.</summary>
    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ExplorerHelper-Setup-{update.Version.ToString(3)}.exe");

        using var response = await Http.GetAsync(update.InstallerUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
                progress?.Report((double)read / total.Value);
        }
        return path;
    }

    /// <summary>
    /// Hands off to the downloaded installer and returns; the caller must shut the app down
    /// immediately afterwards. A hidden cmd waits ~2 s for our exit, runs the installer
    /// silently (/CLOSEAPPLICATIONS mops up any straggling file locks via the Restart
    /// Manager), then relaunches the updated exe — with the current folder, so the user
    /// lands back where they were.
    /// </summary>
    public static void ApplyUpdate(string installerPath, string? folderToReopen)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the running exe path.");
        var relaunchArg = folderToReopen is null ? string.Empty : $" \"{folderToReopen}\"";

        // ping is the classic always-available sleep for a hidden console; "call" guarantees
        // control returns to run the next segment; the trailing "start" relaunches even if
        // the installer reports a non-zero exit, so a failed update still leaves the user
        // with a running (old) app.
        var script = $"ping -n 3 127.0.0.1 >nul & " +
                     $"call \"{installerPath}\" /SILENT /CLOSEAPPLICATIONS & " +
                     $"start \"\" \"{exe}\"{relaunchArg}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // /S: strip exactly the outer quotes, so the inner quoted paths survive intact.
            Arguments = $"/S /c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
