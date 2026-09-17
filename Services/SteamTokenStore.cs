using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuaShareX.Models;

namespace LuaShareX.Services;

internal sealed class SteamTokenStore
{
    internal sealed record Account(string Name, ulong SteamId, string ProtectedToken);
    internal sealed class Document
    {
        public string? LastAccount { get; set; }
        public List<Account> Accounts { get; set; } = [];
    }

    private readonly object _gate = new();
    private readonly string _directory;
    private string StorePath => Path.Combine(_directory, "accounts.json");
    private Document _document = new();
    private bool _unreadable;

    public SteamTokenStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaShareX");
        try
        {
            if (File.Exists(StorePath))
                _document = JsonSerializer.Deserialize<Document>(File.ReadAllText(StorePath)) ?? throw new JsonException();
            if (_document.Accounts == null || _document.Accounts.Any(a => a == null || string.IsNullOrWhiteSpace(a.Name)))
                throw new JsonException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _document = new();
            _unreadable = true; // Do not overwrite an unreadable store with an empty one.
        }
    }

    public string? LastAccount { get { lock (_gate) return _document.LastAccount; } }
    public List<SteamLocalUser> Accounts
    {
        get { lock (_gate) return _document.Accounts.Select(a => new SteamLocalUser
            { AccountName = a.Name, SteamId64 = a.SteamId.ToString(), RememberPassword = true }).ToList(); }
    }

    public (string Token, ulong SteamId)? Get(string name)
    {
        lock (_gate)
        {
            var account = _document.Accounts.FirstOrDefault(a => Same(a.Name, name));
            if (account == null) return null;
            byte[]? bytes = null;
            try
            {
                bytes = ProtectedData.Unprotect(Convert.FromBase64String(account.ProtectedToken), Entropy(account.Name), DataProtectionScope.CurrentUser);
                return (Encoding.UTF8.GetString(bytes), account.SteamId);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return null; }
            finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public void Save(string name, ulong steamId, string token)
    {
        lock (_gate)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            string encrypted;
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy(name), DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var next = new Document { LastAccount = name, Accounts = _document.Accounts.Where(a => !Same(a.Name, name)).ToList() };
            next.Accounts.Add(new Account(name, steamId, encrypted));
            Persist(next);
        }
    }

    public void Remove(string name)
    {
        lock (_gate)
            Persist(new Document
            {
                LastAccount = Same(_document.LastAccount, name) ? null : _document.LastAccount,
                Accounts = _document.Accounts.Where(a => !Same(a.Name, name)).ToList()
            });
    }

    // The old token.txt is removed only after it has been successfully encrypted and persisted.
    public void MigrateLegacy(List<SteamLocalUser> localUsers)
    {
        var path = Path.Combine(_directory, "token.txt");
        if (!File.Exists(path) || _unreadable) return;
        try
        {
            var text = File.ReadAllText(path).Trim();
            string? name = null;
            string? token = text;
            ulong steamId = 0;
            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                token = root.TryGetProperty("refresh", out var r) ? r.GetString() : null;
                name = root.TryGetProperty("account", out var n) ? n.GetString() : null;
                if (root.TryGetProperty("steamid", out var s)) s.TryGetUInt64(out steamId);
            }
            if (string.IsNullOrEmpty(token)) return;
            var local = localUsers.FirstOrDefault(a => SteamLocalTokenReader.IsUsableToken(token, a.SteamId64));
            name ??= local?.AccountName;
            if (steamId == 0 && local != null) ulong.TryParse(local.SteamId64, out steamId);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (Get(name) == null) Save(name, steamId, token);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidOperationException) { }
    }

    private void Persist(Document next)
    {
        if (_unreadable) throw new IOException("Saved accounts could not be read.");
        Directory.CreateDirectory(_directory);
        var temporary = StorePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, StorePath, true);
        _document = next;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("LuaShareX:" + name.ToLowerInvariant());
}
