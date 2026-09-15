using System.IO;
using System.Text;
using LuaShareX.Models;

namespace LuaShareX.Services;

public class LuaExportService
{
    public string Export(SteamGame game)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"-- App Name: {game.Name}");
        sb.AppendLine($"-- Created by LuaShareX (https://github.com/thecloudyy/LuaShareX)");
        sb.AppendLine();

        // Main app
        if (!string.IsNullOrEmpty(game.Token))
            sb.AppendLine($"addappid({game.AppId}, 1, \"{game.Token}\") -- {game.Name}");
        else
            sb.AppendLine($"addappid({game.AppId}) -- {game.Name}");

        sb.AppendLine();

        // Depots
        var depotsWithKeys = game.Depots.Where(d => !string.IsNullOrEmpty(d.DepotKey)).ToList();
        var depotsWithoutKeys = game.Depots.Where(d => string.IsNullOrEmpty(d.DepotKey)).ToList();

        if (depotsWithKeys.Count > 0 || depotsWithoutKeys.Count > 0)
        {
            sb.AppendLine("-- Depots Section");

            foreach (var depot in depotsWithKeys)
            {
                if (depot.IsShared)
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") -- {depot.Name} (Shared from {depot.SharedFrom})");
                else
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") -- {depot.Name}");
            }

            foreach (var depot in depotsWithoutKeys)
            {
                sb.AppendLine($"addappid({depot.DepotId}) -- {depot.Name}");
            }

            sb.AppendLine();
        }

        // DLCs
        if (game.Dlcs.Count > 0)
        {
            sb.AppendLine("-- DLC Section");
            foreach (var dlc in game.Dlcs)
            {
                if (dlc.Depots.Count > 0)
                {
                    sb.AppendLine($"-- {dlc.Name} (AppID: {dlc.AppId})");
                    sb.AppendLine($"addappid({dlc.AppId})");
                    foreach (var depot in dlc.Depots)
                    {
                        if (!string.IsNullOrEmpty(depot.DepotKey))
                            sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depot.DepotKey}\") -- {depot.Name}");
                        else
                            sb.AppendLine($"addappid({depot.DepotId}) -- {depot.Name}");
                    }
                }
                else
                {
                    sb.AppendLine($"addappid({dlc.AppId}) -- {dlc.Name}");
                }
            }
        }

        return sb.ToString();
    }

    public string ExportMultiple(List<SteamGame> games)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- LuaShareX Export ({games.Count} games)");
        sb.AppendLine($"-- Created by LuaShareX (https://github.com/thecloudyy/LuaShareX)");
        sb.AppendLine($"-- Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

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
}
