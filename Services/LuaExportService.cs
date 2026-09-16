using System.IO;
using System.IO.Compression;
using System.Text;
using LuaShareX.Models;

namespace LuaShareX.Services;

public class LuaExportService
{
    private const string BrandName = "LuaShareX";
    private const string BrandUrl = "https://luasharex.vercel.app";

    public string Export(SteamGame game)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"-- App Name: {game.Name}");
        sb.AppendLine($"-- Created by {BrandName} ({BrandUrl})");

        // Main app with token
        if (!string.IsNullOrEmpty(game.Token))
            sb.AppendLine($"addappid({game.AppId}, 1, \"{game.Token}\")");
        else
            sb.AppendLine($"addappid({game.AppId})");

        // Depots split by Steam's own classification (sharedinstall flag /
        // shared from a Tool-type app), resolved in SteamService. No names.
        var gameDepots = game.Depots
            .Where(d => !d.IsRedistributable)
            .ToList();
        var redistDepots = game.Depots
            .Where(d => d.IsRedistributable)
            .ToList();

        if (gameDepots.Count > 0)
        {
            sb.AppendLine("-- Depots Section");
            foreach (var depot in gameDepots)
            {
                if (!string.IsNullOrEmpty(depot.DepotKey))
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") -- {depot.Name}");
                else
                    sb.AppendLine($"addappid({depot.DepotId}) -- {depot.Name}");
            }
        }

        if (redistDepots.Count > 0)
        {
            sb.AppendLine("-- Redistributable Depots");
            foreach (var depot in redistDepots)
            {
                if (!string.IsNullOrEmpty(depot.DepotKey))
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") -- {depot.Name}");
                else
                    sb.AppendLine($"addappid({depot.DepotId}) -- {depot.Name}");
            }
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
