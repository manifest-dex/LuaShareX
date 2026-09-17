using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LuaShareX.Models;
using LuaShareX.Services;

namespace LuaShareX.Tests;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "LuaShareX-tests-" + Guid.NewGuid().ToString("N"));
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        try
        {
            Run("Selected account is the only local token returned", LocalTokens);
            Run("Token identity, expiry, audience and malformed data are checked", TokenValidation);
            Run("Multiple saved accounts survive restart and individual logout", AccountStore);
            Run("Legacy login migrates to encrypted storage", LegacyMigration);
            Run("Unreadable storage is preserved", CorruptStore);
            Run("Retired sessions suppress callbacks, saves and Guard requests", RetiredSession);
            Run("Lua export keeps app tokens separate from depot keys", LuaExportTokens);
            if (args.Contains("--render")) RenderLogin();
            Console.WriteLine($"PASS: {_passed} checks. Only synthetic account data was used; no Steam connection was opened.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            // The path is a newly created child of the system temp directory.
            Directory.Delete(Root, true);
        }
    }

    private static void Run(string name, Action test) { test(); _passed++; Console.WriteLine("PASS: " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void LuaExportTokens()
    {
        var game = new SteamGame
        {
            AppId = 280160,
            Name = "Aragami",
            BaseDepotKey = "60689065685acbd4becba24f4d7ede49cb7af83215cb48666732aa32d6b09133",
            Token = ulong.MaxValue.ToString(),
            Dlcs =
            [
                new SteamDlc { AppId = 564400, Name = "Masks", Token = "3200000004000000deadbeef" },
                new SteamDlc { AppId = 771720, Name = "Nightfall", Token = "42" }
            ]
        };

        var lua = new LuaExportService().Export(game);
        Check(lua.Contains("addappid(280160, 1, \"60689065685acbd4becba24f4d7ede49cb7af83215cb48666732aa32d6b09133\") --Mainappid Aragami"),
            "A valid base-AppID depot key was not exported.");
        Check(lua.Contains("addtoken(280160, \"18446744073709551615\")"), "A valid UInt64 app token was not exported.");
        Check(lua.Contains("addtoken(771720, \"42\")"), "A valid DLC token was not exported.");
        Check(!lua.Contains("3200000004000000deadbeef"), "An ownership-ticket blob was exported as an app token.");
        Check(!new LuaExportService().Export(new SteamGame { AppId = 1, Token = "0" }).Contains("addtoken"),
            "A zero PICS token was exported.");
    }
    private static string Jwt(string id, long? expiry = null, string audience = "client")
    {
        var payload = JsonSerializer.Serialize(new { sub = id, exp = expiry ?? DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), aud = new[] { "derive", audience } });
        return "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
    }

    private static SteamLocalUser User(string name, string id) => new() { AccountName = name, SteamId64 = id };
    private static string ProtectForSteam(string name, string token) => Convert.ToHexString(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(name), DataProtectionScope.CurrentUser));

    private static void LocalTokens()
    {
        Check(SteamLocalTokenReader.CacheKey("123456789") == "cbf439261", "CRC32 must match the standard test vector.");
        var alice = User("test_alice", "76561198000000001");
        var bob = User("test_bob", "76561198000000002");
        var aliceToken = Jwt(alice.SteamId64);
        var bobToken = Jwt(bob.SteamId64);
        var path = Path.Combine(Root, "local.vdf");
        var text = "\"MachineUserConfigStore\" { \"Software\" { \"valve\" { \"Steam\" { \"ConnectCache\" {\n"
            + $"\"{SteamLocalTokenReader.CacheKey(alice.AccountName)}\" \"{ProtectForSteam(alice.AccountName, aliceToken)}\"\n"
            + $"\"{SteamLocalTokenReader.CacheKey(bob.AccountName)}\" \"{ProtectForSteam(bob.AccountName, bobToken)}\"\n"
            + "} } } } }";
        File.WriteAllText(path, text);
        Check(SteamLocalTokenReader.Read(alice, path) == aliceToken, "Alice token lookup failed.");
        Check(SteamLocalTokenReader.Read(bob, path) == bobToken, "Bob token lookup failed.");
        Check(SteamLocalTokenReader.Read(User(alice.AccountName, bob.SteamId64), path) == null, "Mismatched identity was accepted.");
        Check(SteamLocalTokenReader.Read(User("missing", alice.SteamId64), path) == null, "Another account's token was returned.");
        Check(File.ReadAllText(path) == text, "Steam cache was modified.");
        File.WriteAllText(path, "invalid file");
        Check(SteamLocalTokenReader.Read(alice, path) == null, "Malformed VDF should not authenticate.");
    }

    private static void TokenValidation()
    {
        const string id = "76561198000000001";
        Check(SteamLocalTokenReader.IsUsableToken(Jwt(id), id), "Valid client JWT rejected.");
        Check(!SteamLocalTokenReader.IsUsableToken(Jwt(id, 1), id), "Expired token accepted.");
        Check(!SteamLocalTokenReader.IsUsableToken(Jwt(id, audience: "web"), id), "Web-only token accepted.");
        foreach (var input in new[] { "not-jwt", "x.%%%%.z", "x.e30.z", "x.bnVsbA.z" })
            Check(!SteamLocalTokenReader.IsUsableToken(input, id), "Malformed token accepted.");
    }

    private static void AccountStore()
    {
        var directory = Path.Combine(Root, "accounts");
        var store = new SteamTokenStore(directory);
        store.Save("Alice", 1, "synthetic-alice-secret");
        store.Save("Bob", 2, "synthetic-bob-secret");
        var persisted = File.ReadAllText(Path.Combine(directory, "accounts.json"));
        Check(!persisted.Contains("synthetic-"), "A token was written as plaintext.");
        store = new SteamTokenStore(directory);
        Check(store.Accounts.Count == 2 && store.LastAccount == "Bob", "Accounts did not survive restart.");
        Check(store.Get("alice")?.Token == "synthetic-alice-secret", "Case-insensitive lookup failed.");
        store.Save("ALICE", 1, "synthetic-renewed");
        Check(store.Accounts.Count == 2 && store.Get("bob")?.Token == "synthetic-bob-secret", "Renewal modified a different account.");
        store.Remove("Alice");
        store = new SteamTokenStore(directory);
        Check(store.Accounts.Count == 1 && store.Get("Alice") == null && store.Get("Bob") != null, "Logout cleared another account.");
        Check(store.LastAccount == null, "Forgotten account remained the automatic login target.");
    }

    private static void LegacyMigration()
    {
        var directory = Path.Combine(Root, "legacy");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "token.txt");
        File.WriteAllText(path, JsonSerializer.Serialize(new { account = "Alice", steamid = 1UL, refresh = "legacy-synthetic-secret" }));
        var store = new SteamTokenStore(directory);
        store.MigrateLegacy([]);
        Check(store.Get("Alice")?.Token == "legacy-synthetic-secret" && !File.Exists(path), "Legacy migration failed.");
        Check(!File.ReadAllText(Path.Combine(directory, "accounts.json")).Contains("legacy-synthetic-secret"), "Legacy token was not encrypted.");
    }

    private static void CorruptStore()
    {
        var directory = Path.Combine(Root, "corrupt");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "accounts.json");
        File.WriteAllText(path, "broken");
        var store = new SteamTokenStore(directory);
        try { store.Save("Alice", 1, "test"); throw new Exception("Corrupt store was overwritten."); }
        catch (IOException) { }
        Check(File.ReadAllText(path) == "broken", "Corrupt original was lost.");
    }

    private static void RetiredSession()
    {
        var store = new SteamTokenStore(Path.Combine(Root, "session"));
        var session = new SteamSession(new ToastService(), store);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(SteamSession);
        type.GetField("_accountName", flags)!.SetValue(session, "Alice");
        type.GetField("_steamId64", flags)!.SetValue(session, 1UL);
        type.GetField("<IsSteamKitConnected>k__BackingField", flags)!.SetValue(session, true);
        ((List<uint>)type.GetField("_ownedAppIds", flags)!.GetValue(session)!).Add(123);
        ((Dictionary<uint, string>)type.GetField("_depotKeys", flags)!.GetValue(session)!).Add(1, "old-account-key");
        int delivered = 0;
        session.OnLoginNeeded += _ => delivered++;
        session.Disconnect();
        type.GetMethod("UiInvoke", flags)!.Invoke(session, [(Action)(() => delivered++)]);
        type.GetMethod("SaveTokens", flags)!.Invoke(session, ["stale-secret", null, "Alice", 1UL]);
        Check(delivered == 0 && store.Accounts.Count == 0, "A retired callback or token save leaked into the new session.");
        Check(session.RequestGuardCodeAsync("test").IsCanceled, "Retired Guard prompt did not cancel.");
        var next = new SteamSession(new ToastService(), store);
        Check(next.OwnedAppCount == 0 && next.GetDepotKey(1) == null && next.AccountName == null, "New account inherited session data.");
        next.Disconnect();
        var empty = new SteamSession(new ToastService(), new SteamTokenStore(Path.Combine(Root, "empty")));
        var prompted = false;
        empty.OnLoginNeeded += _ => prompted = true;
        empty.StartSteamKitAuth().GetAwaiter().GetResult();
        Check(prompted && type.GetField("_steamClient", flags)!.GetValue(empty) == null,
            "A missing saved login should open the picker without connecting to Steam.");
        empty.Disconnect();
    }

    private static void RenderLogin()
    {
        // Load real WPF resources and XAML with a synthetic DataContext; never start App.Run/AutoStart.
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow { DataContext = new LoginPreview() };
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(880, 660));
        content.Arrange(new Rect(0, 0, 880, 660));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(880, 660, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var path = Path.Combine(AppContext.BaseDirectory, "login-preview.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("Rendered login preview: " + path);
    }

    public sealed class LoginPreview
    {
        public bool ShowLoginPrompt => true;
        public bool ShowQrLogin => false;
        public bool ShowGuardPrompt => false;
        public bool ContentVisible => false;
        public List<SteamLocalUser> LocalUsers { get; } = [new() { AccountName = "test_alice", PersonaName = "Alice" }, new() { AccountName = "test_bob", PersonaName = "Bob" }];
        public SteamLocalUser? SelectedLocalUser { get; set; }
        public LoginPreview() { SelectedLocalUser = LocalUsers[0]; }
        public string LoginUsername { get; set; } = "test_alice";
        public string LoginPassword { get; set; } = "";
        public string LoginStatus => "Select a remembered account, or add another account with QR/password.";
        public string StatusMessage => "Choose an account.";
    }
}
