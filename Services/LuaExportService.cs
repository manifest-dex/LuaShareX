using System.IO;
using System.IO.Compression;
using System.Text;
using LuaShareX.Models;

namespace LuaShareX.Services;

public class LuaExportService
{
    private const string BrandName = "LuaShareX";
    private const string BrandUrl = "https://luasharex.vercel.app";

    private static string Comment(string s) =>
        s.Replace("\r", "").Replace("\n", "");

    private static void AddDepotLines(StringBuilder sb, IEnumerable<SteamDepot> depots)
    {
        foreach (var depot in depots)
        {
            var name = Comment(string.IsNullOrEmpty(depot.Name) ? $"Depot {depot.DepotId}" : depot.Name);
            if (!string.IsNullOrEmpty(depot.DepotKey))
                sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") --{name}");
            else
                sb.AppendLine($"addappid({depot.DepotId}) --{name}");
            if (!string.IsNullOrEmpty(depot.ManifestId))
                sb.AppendLine($"setManifestid({depot.DepotId}, \"{depot.ManifestId}\", {depot.ManifestSize})");
        }
    }

    public string Export(SteamGame game)
    {
        var sb = new StringBuilder();
        var name = Comment(string.IsNullOrEmpty(game.Name) ? $"App {game.AppId}" : game.Name);

        sb.AppendLine($"-- Downloaded using {BrandName} ({BrandUrl})");
        sb.AppendLine($"-- Original file: {game.AppId}.lua");
        sb.AppendLine($"--Gamename {name}");

        // Main app line: short stplug-in key preferred (see EnsureExportDataAsync);
        // the long ownership-ticket blob is only used when no short key exists.
        if (!string.IsNullOrEmpty(game.Token))
            sb.AppendLine($"addappid({game.AppId}, 1, \"{game.Token}\") --Mainappid {name}");
        else
            sb.AppendLine($"addappid({game.AppId}) --Mainappid {name}");

        // Directly owned depots first…
        AddDepotLines(sb, game.Depots.Where(d => !d.IsShared && !d.IsRedistributable));

        // …then everything Steam marks as shared/redistributable.
        var shared = game.Depots.Where(d => d.IsShared || d.IsRedistributable).ToList();
        if (shared.Count > 0)
        {
            sb.AppendLine("--Share Depots");
            AddDepotLines(sb, shared);
        }

        foreach (var dlc in game.Dlcs.OrderBy(d => d.AppId))
        {
            var dlcName = Comment(string.IsNullOrEmpty(dlc.Name) ? $"AppID {dlc.AppId}" : dlc.Name);
            sb.AppendLine($"addappid({dlc.AppId}) --Dlcname {dlcName}");
            if (!string.IsNullOrEmpty(dlc.Token))
                sb.AppendLine($"addtoken({dlc.AppId}, \"{dlc.Token}\")");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public string ExportMultiple(List<SteamGame> games)
    {
        var sb = new StringBuilder();
        foreach (var game in games)
        {
            sb.AppendLine(Export(game));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void SaveToFile(string content, string filePath)
    {
        File.WriteAllText(filePath, content, Encoding.UTF8);
    }

    /// <summary>
    /// Packs one &lt;appid&gt;.lua per game into a zip archive.
    /// </summary>
    public void SaveMultipleToZip(List<SteamGame> games, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var game in games)
        {
            var entry = zip.CreateEntry($"{game.AppId}.lua", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(Export(game));
        }
    }
}
