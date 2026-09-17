using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace LuaShareX.Services;

/// <summary>Metadata for a newer GitHub release.</summary>
public sealed record UpdateInfo(
    string Version,
    string Name,
    string Notes,
    string AssetName,
    string DownloadUrl,
    long Size);

/// <summary>
/// Self-updater backed by GitHub Releases. No third-party updater framework:
/// check the latest release, download the win-x64 zip, then hand off to a
/// small batch script that waits for this process to exit, copies the files
/// over the app folder and restarts. All failures are silent to the caller
/// (the UI toasts them) and never break normal app startup.
/// </summary>
public class UpdateService
{
    public const string Owner = "manifest-dex";
    public const string Repo = "LuaShareX";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LuaShareX", "1.1.3"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return Normalize(v ?? new Version(1, 0, 0));
        }
    }

    internal static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    internal static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(1, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(t, out var v))
        {
            version = Normalize(v);
            return true;
        }
        return false;
    }

    /// <summary>Returns update metadata when the latest release is newer than this build, else null.</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        if (!res.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        if (!TryParseTag(tagEl.GetString(), out var latest)) return null;
        if (latest <= CurrentVersion) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;

        string? pickName = null, pickUrl = null;
        long pickSize = 0;
        string? fallbackName = null, fallbackUrl = null;
        long fallbackSize = 0;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(url))
                continue;
            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
            fallbackName ??= name;
            fallbackUrl ??= url;
            fallbackSize = fallbackSize == 0 ? size : fallbackSize;
            if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            {
                pickName = name;
                pickUrl = url;
                pickSize = size;
                break;
            }
        }

        pickName ??= fallbackName;
        pickUrl ??= fallbackUrl;
        if (pickSize == 0) pickSize = fallbackSize;
        if (pickUrl == null) return null;

        var relName = root.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var rb) ? rb.GetString() ?? "" : "";
        return new UpdateInfo(latest.ToString(), relName, notes, pickName!, pickUrl, pickSize);
    }

    /// <summary>Downloads the release asset into the local updates folder. Returns the zip path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LuaShareX", "updates");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, update.AssetName);

        using var res = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? update.Size;

        await using var net = await res.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await net.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        progress?.Report(1);
        return path;
    }

    /// <summary>
    /// Hands off to an updater script (waits for this process to exit, expands
    /// the zip over the app folder, restarts the app, deletes itself) and returns.
    /// The caller is expected to shut the app down right after.
    /// </summary>
    public static void InstallAndRestart(string zipPath)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var workDir = Path.GetDirectoryName(zipPath)!;
        var pid = Environment.ProcessId;
        var script = Path.Combine(workDir, "update.cmd");

        var bat =
            "@echo off\r\n" +
            $"set \"STAGE={Path.Combine(workDir, "staged")}\"\r\n" +
            $"set \"APPDIR={appDir}\"\r\n" +
            $"set \"ZIP={zipPath}\"\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if %errorlevel%==0 ( timeout /t 1 /nobreak >NUL & goto wait )\r\n" +
            "if exist \"%STAGE%\" rd /s /q \"%STAGE%\"\r\n" +
            "powershell -NoProfile -NonInteractive -Command \"Expand-Archive -LiteralPath '%ZIP%' -DestinationPath '%STAGE%' -Force\"\r\n" +
            "robocopy \"%STAGE%\" \"%APPDIR%\" /E /NFL /NDL /NJH /NJS /R:2 /W:1\r\n" +
            "start \"\" \"%APPDIR%\\LuaShareX.exe\"\r\n" +
            "rd /s /q \"%STAGE%\"\r\n" +
            "(goto) 2>nul & del \"%~f0\"\r\n";
        File.WriteAllText(script, bat);

        Process.Start(new ProcessStartInfo
        {
            FileName = script,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = appDir,
        });
    }
}
