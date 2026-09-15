using System.Net.Http;
using System.Text.RegularExpressions;

namespace LuaShareX.Services;

public partial class HubcapClient
{
    private const string ApiBaseUrl = "https://hubcapmanifest.com";
    private const string ApiKey = "smm_da41ecae4378061052ce32dc357c9ae118c5f64cf6aab072c08c606c57d7558afe894d27e1eb14f90c521752894eebed";

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(ApiBaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<Dictionary<uint, string>> GetDepotKeysAsync(uint appId)
    {
        var keys = new Dictionary<uint, string>();

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/lua/{appId}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return keys;

            var lua = await res.Content.ReadAsStringAsync();
            return ParseLuaDepotKeys(lua);
        }
        catch
        {
            return keys;
        }
    }

    public static Dictionary<uint, string> ParseLuaDepotKeys(string lua)
    {
        var keys = new Dictionary<uint, string>();

        // Pattern: addappid(depotId, 1, "key")
        var matches = DepotKeyRegex().Matches(lua);
        foreach (Match match in matches)
        {
            if (uint.TryParse(match.Groups[1].Value, out var depotId))
            {
                keys[depotId] = match.Groups[2].Value;
            }
        }

        return keys;
    }

    [GeneratedRegex(@"addappid\((\d+),\s*1,\s*""([^""]+)""\)", RegexOptions.Compiled)]
    private static partial Regex DepotKeyRegex();
}
