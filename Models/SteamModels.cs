namespace LuaShareX.Models;

public class SteamGame
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public bool IsSelected { get; set; }
    public List<SteamDepot> Depots { get; set; } = [];
    public List<SteamDlc> Dlcs { get; set; } = [];
}

public class SteamDepot
{
    public uint DepotId { get; set; }
    public string Name { get; set; } = "";
    public string DepotKey { get; set; } = "";
    public bool IsShared { get; set; }
    public string SharedFrom { get; set; } = "";
}

public class SteamDlc
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public List<SteamDepot> Depots { get; set; } = [];
}
