using System.IO;
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

        sb.AppendLine();

        // Depots with keys (non-redistributable)
        var gameDepots = game.Depots
            .Where(d => !IsRedistributable(d.Name))
            .ToList();
        var redistDepots = game.Depots
            .Where(d => IsRedistributable(d.Name))
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
            sb.AppendLine();
            sb.AppendLine("-- Redistributable Depots");
            foreach (var depot in redistDepots)
            {
                if (!string.IsNullOrEmpty(depot.DepotKey))
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") -- {depot.Name}");
                else
                    sb.AppendLine($"addappid({depot.DepotId}) -- {depot.Name}");
            }
        }

        return sb.ToString();
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

    private static bool IsRedistributable(string name)
    {
        return name.Contains("Redist", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VC 20", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Common Redistributable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase);
    }

    public void SaveToFile(string content, string filePath)
    {
        File.WriteAllText(filePath, content, Encoding.UTF8);
    }
}
