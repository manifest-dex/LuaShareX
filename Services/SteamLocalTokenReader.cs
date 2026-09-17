using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuaShareX.Models;
using SteamKit2;

namespace LuaShareX.Services;

internal static class SteamLocalTokenReader
{
    public static string? Read(SteamLocalUser account, string? path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steam", "local.vdf");
        if (string.IsNullOrWhiteSpace(account.AccountName) || !File.Exists(path)) return null;
        try
        {
            var node = KeyValue.LoadAsText(path);
            if (node?.Name?.Equals("MachineUserConfigStore", StringComparison.OrdinalIgnoreCase) != true)
                return null;
            foreach (var name in new[] { "Software", "Valve", "Steam", "ConnectCache" })
                node = node?.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (node == null) return null;

            // Look up only the selected account. Steam's files are read-only inputs.
            foreach (var name in new[] { account.AccountName, account.AccountName.ToLowerInvariant() }.Distinct())
            {
                var key = CacheKey(name);
                var hex = node.Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))?.Value;
                if (string.IsNullOrEmpty(hex)) continue;
                byte[]? decoded = null;
                try
                {
                    decoded = ProtectedData.Unprotect(Convert.FromHexString(hex), Encoding.UTF8.GetBytes(name), DataProtectionScope.CurrentUser);
                    var token = Encoding.UTF8.GetString(decoded).TrimEnd('\0');
                    if (IsUsableToken(token, account.SteamId64)) return token;
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { }
                finally { if (decoded != null) CryptographicOperations.ZeroMemory(decoded); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static string CacheKey(string accountName)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in Encoding.UTF8.GetBytes(accountName))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return (~crc).ToString("x", CultureInfo.InvariantCulture) + "1";
    }

    // This is an identity/expiry preflight, not signature verification. Steam validates the JWT.
    internal static bool IsUsableToken(string token, string steamId)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            var root = doc.RootElement;
            return root.GetProperty("sub").GetString() == steamId
                && root.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && root.GetProperty("aud").EnumerateArray().Any(a => a.GetString() == "client");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
}
