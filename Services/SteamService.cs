using System.IO;
using System.Text.RegularExpressions;
using LuaShareX.Models;

namespace LuaShareX.Services;

public partial class SteamService
{
    public string? SteamInstallPath { get; private set; }
    public string? CurrentSteamId { get; private set; }

    public bool IsLoaded { get; private set; }

    public event Action? OnLoaded;
    public event Action<string>? OnError;

    public void DetectSteam()
    {
        var possiblePaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(Path.Combine(path, "steam.exe")))
            {
                SteamInstallPath = path;
                break;
            }
        }

        if (SteamInstallPath == null)
        {
            OnError?.Invoke("Steam not found. Please install Steam.");
            return;
        }

        // Read loginusers.vdf to get current Steam ID
        var loginUsersPath = Path.Combine(SteamInstallPath, "config", "loginusers.vdf");
        if (File.Exists(loginUsersPath))
        {
            var content = File.ReadAllText(loginUsersPath);
            var match = SteamIdRegex().Match(content);
            if (match.Success)
            {
                CurrentSteamId = match.Groups[1].Value;
            }
        }
    }

    public List<SteamGame> GetInstalledGames()
    {
        var games = new List<SteamGame>();

        if (SteamInstallPath == null) return games;

        var libraryFoldersPath = Path.Combine(SteamInstallPath, "config", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath)) return games;

        var libraryPaths = ParseLibraryFolders(libraryFoldersPath);

        foreach (var libPath in libraryPaths)
        {
            var steamAppsDir = Path.Combine(libPath, "steamapps");
            if (!Directory.Exists(steamAppsDir)) continue;

            foreach (var acfFile in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var game = ParseAppManifest(acfFile);
                    if (game != null)
                    {
                        games.Add(game);
                    }
                }
                catch { }
            }
        }

        IsLoaded = true;
        OnLoaded?.Invoke();
        return games;
    }

    private List<string> ParseLibraryFolders(string path)
    {
        var paths = new List<string>();

        // Always include the main Steam directory
        if (SteamInstallPath != null)
            paths.Add(SteamInstallPath);

        var content = File.ReadAllText(path);

        // Match "path" "value" entries
        var matches = LibraryPathRegex().Matches(content);
        foreach (Match match in matches)
        {
            var libPath = match.Groups[1].Value.Replace("\\\\", "\\");
            if (!paths.Contains(libPath) && Directory.Exists(libPath))
            {
                paths.Add(libPath);
            }
        }

        return paths;
    }

    private SteamGame? ParseAppManifest(string filePath)
    {
        var content = File.ReadAllText(filePath);

        var appIdMatch = AppIdRegex().Match(content);
        var nameMatch = NameRegex().Match(content);

        if (!appIdMatch.Success || !nameMatch.Success) return null;

        if (!uint.TryParse(appIdMatch.Groups[1].Value, out var appId)) return null;

        var game = new SteamGame
        {
            AppId = appId,
            Name = nameMatch.Groups[1].Value
        };

        // Parse installed depots
        var depotMatches = DepotIdRegex().Matches(content);
        foreach (Match depotMatch in depotMatches)
        {
            if (uint.TryParse(depotMatch.Groups[1].Value, out var depotId))
            {
                game.Depots.Add(new SteamDepot
                {
                    DepotId = depotId,
                    Name = $"Depot {depotId}"
                });
            }
        }

        return game;
    }

    public void Disconnect()
    {
        IsLoaded = false;
    }

    [GeneratedRegex(@"\t""(\d{17})""\s*\n\s*\{", RegexOptions.Compiled)]
    private static partial Regex SteamIdRegex();

    [GeneratedRegex(@"""path""\s+""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex(@"""appid""\s+""(\d+)""", RegexOptions.Compiled)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"""name""\s+""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^\t\t\t""(\d+)""\s*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex DepotIdRegex();
}
