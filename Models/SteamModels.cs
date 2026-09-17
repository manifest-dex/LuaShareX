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
    /// <summary>App that actually owns the depot (depotfromapp). Used for key requests.</summary>
    public uint ParentAppId { get; set; }
    /// <summary>True when Steam's own data marks this as shared redistributable content
    /// (sharedinstall flag or shared from a Tool-type app). Decided from PICS, never names.</summary>
    public bool IsRedistributable { get; set; }
}

public class SteamDlc
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public List<SteamDepot> Depots { get; set; } = [];
}

/// <summary>One account from loginusers.vdf (local Steam cache).</summary>
public class SteamLocalUser
{
    public string SteamId64 { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public bool RememberPassword { get; set; }
    public bool AutoLogin { get; set; }
    public bool MostRecent { get; set; }
    public long Timestamp { get; set; }
    public string DisplayName => string.IsNullOrEmpty(PersonaName)
        ? AccountName
        : $"{PersonaName} ({AccountName})";
}
