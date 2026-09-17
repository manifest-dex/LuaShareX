using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using LuaShareX.Models;

namespace LuaShareX.Services;

public class LuaExportService
{
    private const string BrandName = "LuaShareX";
    private const string BrandUrl = "https://luasharex.vercel.app";

    private static string Comment(string s) =>
        s.Replace("\r", "").Replace("\n", "");

    private static bool IsAppToken(string token) =>
        ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value != 0;

    private static bool IsDepotKey(string key) =>
        key.Length == 64 && key.All(Uri.IsHexDigit);

    private static void AddDepotLines(StringBuilder sb, IEnumerable<SteamDepot> depots)
    {
        foreach (var depot in depots)
        {
            var name = Comment(string.IsNullOrEmpty(depot.Name) ? $"Depot {depot.DepotId}" : depot.Name);
            if (IsDepotKey(depot.DepotKey))
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

        var baseDepot = game.Depots.FirstOrDefault(d => d.DepotId == game.AppId);
        var baseDepotKey = IsDepotKey(game.BaseDepotKey) ? game.BaseDepotKey : baseDepot?.DepotKey ?? "";
        if (IsDepotKey(baseDepotKey))
            sb.AppendLine($"addappid({game.AppId}, 1, \"{baseDepotKey}\") --Mainappid {name}");
        else
            sb.AppendLine($"addappid({game.AppId}) --Mainappid {name}");
        if (!string.IsNullOrEmpty(baseDepot?.ManifestId))
            sb.AppendLine($"setManifestid({game.AppId}, \"{baseDepot.ManifestId}\", {baseDepot.ManifestSize})");
        if (IsAppToken(game.Token))
            sb.AppendLine($"addtoken({game.AppId}, \"{game.Token}\")");

        // Directly owned depots first…
        AddDepotLines(sb, game.Depots.Where(d => d.DepotId != game.AppId && !d.IsShared && !d.IsRedistributable));

        // …then everything Steam marks as shared/redistributable.
        var shared = game.Depots.Where(d => d.DepotId != game.AppId && (d.IsShared || d.IsRedistributable)).ToList();
        if (shared.Count > 0)
        {
            sb.AppendLine("--Share Depots");
            AddDepotLines(sb, shared);
        }

        foreach (var dlc in game.Dlcs.OrderBy(d => d.AppId))
        {
            var dlcName = Comment(string.IsNullOrEmpty(dlc.Name) ? $"AppID {dlc.AppId}" : dlc.Name);
            sb.AppendLine($"addappid({dlc.AppId}) --Dlcname {dlcName}");
            if (IsAppToken(dlc.Token))
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
