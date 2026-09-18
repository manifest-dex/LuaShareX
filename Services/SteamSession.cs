using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;
using LuaShareX.Models;

namespace LuaShareX.Services;

internal partial class SteamSession
{
    private readonly ToastService _toast;

    public SteamSession(ToastService toast, SteamTokenStore tokenStore)
    {
        _toast = toast;
        _tokenStore = tokenStore;
    }

    private readonly SteamTokenStore _tokenStore;
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private volatile bool _retired;
    private readonly uint _loginId = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
    public string? AccountName => _accountName;

    private enum LoginMode { Auto, Credentials, Qr, Local }

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamApps? _steamApps;
    private volatile bool _callbackLoopRunning;

    private string? _steamInstallPath;
    private string? _currentSteamId;
    private readonly Dictionary<uint, string> _depotKeys = new();
    private readonly Dictionary<uint, string> _appNames = new();
    private readonly Dictionary<uint, string> _appTokens = new();
    private readonly Dictionary<uint, List<SteamDepot>> _appDepots = new();
    private readonly Dictionary<uint, List<uint>> _appDlcs = new();
    private readonly List<uint> _ownedAppIds = [];
    private readonly object _sync = new();
    private static readonly HttpClient _http = new();

    private string? _savedRefreshToken;
    private string? _accessToken;
    private string? _accountName;
    private ulong _steamId64;
    private string? _pendingUsername;
    private string? _pendingPassword;
    private LoginMode _loginMode = LoginMode.Auto;

    private QrAuthSession? _qrSession;
    private CancellationTokenSource? _qrCts;
    private TaskCompletionSource<string>? _guardTcs;

    public string? SteamInstallPath => _steamInstallPath;
    public string? CurrentSteamId => _currentSteamId;
    public bool IsSteamRunning { get; private set; }
    public bool IsSteamKitConnected { get; private set; }
    public string? AutoLoginUsername { get; private set; }

    /// <summary>All accounts from the local loginusers.vdf cache.</summary>
    public List<SteamLocalUser> LocalUsers { get; } = [];
    /// <summary>Best guess at the active local account (registry autologin, flag, recency).</summary>
    public SteamLocalUser? ActiveLocalUser { get; private set; }
    public int DepotKeyCount
    {
        get { lock (_sync) return _depotKeys.Count; }
    }

    public event Action? OnSteamKitConnected;
    public event Action? OnSteamKitDisconnected;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnLoginNeeded;
    public event Action<string>? OnQRCodeReady;
    public event Action<string>? OnGuardCodeNeeded;
    public event Action<int>? OnOwnedGamesLoaded;

    private void UiInvoke(Action action)
    {
        void Deliver() { if (!_retired) action(); }
        if (System.Windows.Application.Current?.Dispatcher != null)
            System.Windows.Application.Current.Dispatcher.BeginInvoke(Deliver);
        else
            Deliver();
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "luasharex.log"), $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    // ── Local mode ─────────────────────────────────────────────────

    public void DetectSteam()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "steam.exe")))
                _steamInstallPath = path;
        }
        catch { }
        var possiblePaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        foreach (var path in possiblePaths)
        {
            if (_steamInstallPath != null) break;
            if (File.Exists(Path.Combine(path, "steam.exe")))
            {
                _steamInstallPath = path;
                break;
            }
        }

        if (_steamInstallPath == null) return;

        ReadDepotKeysFromConfig();
        ReadStPlugInLuaFiles();
        DetectAutoLoginUser();
        ReadLocalUsers();
    }

    private void ReadDepotKeysFromConfig()
    {
        _depotKeys.Clear();
        var configPath = Path.Combine(_steamInstallPath!, "config", "config.vdf");
        if (!File.Exists(configPath)) return;

        var section = ExtractVdfSection(File.ReadAllText(configPath), "depots");
        if (section == null) return;

        foreach (Match match in DepotKeyEntryRegex().Matches(section))
        {
            if (uint.TryParse(match.Groups[1].Value, out var depotId))
                _depotKeys[depotId] = match.Groups[2].Value;
        }
    }

    /// <summary>
    /// Extracts a top-level-ish VDF section by brace matching instead of
    /// indentation-sensitive regex (Steam's indent depth varies by file).
    /// Returns the inner text (without the outer braces), or null.
    /// </summary>
    private static string? ExtractVdfSection(string content, string sectionName)
    {
        var key = $"\"{sectionName}\"";
        int keyIdx = content.IndexOf(key, StringComparison.Ordinal);
        if (keyIdx < 0) return null;

        int openIdx = content.IndexOf('{', keyIdx + key.Length);
        if (openIdx < 0) return null;

        int depth = 0;
        for (int i = openIdx; i < content.Length; i++)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return content.Substring(openIdx + 1, i - openIdx - 1);
            }
        }
        return null;
    }

    private readonly Dictionary<uint, string> _luaDepotNames = new();
    private readonly Dictionary<uint, string> _luaAppTokens = new();

    private void ReadStPlugInLuaFiles()
    {
        var dir = Path.Combine(_steamInstallPath!, "config", "stplug-in");
        if (!Directory.Exists(dir)) return;

        foreach (var f in Directory.GetFiles(dir, "*.lua"))
        {
            try
            {
                if (!uint.TryParse(Path.GetFileNameWithoutExtension(f), out var fileAppId)) continue;
                foreach (var line in File.ReadLines(f))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;

                    var tokenMatch = LuaTokenRegex().Match(line);
                    if (tokenMatch.Success &&
                        uint.TryParse(tokenMatch.Groups[1].Value, out var tokenAppId) &&
                        ulong.TryParse(tokenMatch.Groups[2].Value, out var appToken) &&
                        appToken != 0)
                    {
                        _luaAppTokens[tokenAppId] = tokenMatch.Groups[2].Value;
                        continue;
                    }

                    var m = LuaAppIdRegex().Match(line);
                    if (!m.Success) continue;
                    if (!uint.TryParse(m.Groups[1].Value, out var id)) continue;

                    var key = m.Groups[2].Value;
                    var comment = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";

                    if (key.Length == 64 && key.All(Uri.IsHexDigit))
                    {
                        if (!_depotKeys.ContainsKey(id))
                            _depotKeys[id] = key;
                        if (id != fileAppId && !string.IsNullOrEmpty(comment) && !_luaDepotNames.ContainsKey(id))
                            _luaDepotNames[id] = comment;
                    }
                }
            }
            catch { }
        }
    }

    private string ResolveDepotName(uint depotId, string fallback)
    {
        lock (_sync)
        {
            if (_luaDepotNames.TryGetValue(depotId, out var name) && !string.IsNullOrEmpty(name))
                return name;
        }
        return fallback;
    }

    private string ResolveAppToken(uint appId)
    {
        lock (_sync) return _appTokens.TryGetValue(appId, out var t) ? t : "";
    }

    /// <summary>
    /// Parses every account from the local loginusers.vdf cache and picks the
    /// active one: registry AutoLoginUser match first, then AutoLogin flag,
    /// then most recent timestamp.
    /// </summary>
    private void ReadLocalUsers()
    {
        LocalUsers.Clear();
        ActiveLocalUser = null;
        if (!IsSteamKitConnected) _currentSteamId = null;

        var path = Path.Combine(_steamInstallPath!, "config", "loginusers.vdf");
        if (!File.Exists(path)) return;

        try
        {
            var users = KeyValue.LoadAsText(path);
            foreach (var user in users?.Children ?? [])
            {
                if (!ulong.TryParse(user.Name, out _)) continue;
                var fields = user.Children.Where(c => c.Name != null).ToDictionary(c => c.Name!, c => c.Value ?? "", StringComparer.OrdinalIgnoreCase);

                fields.TryGetValue("AccountName", out var account);
                fields.TryGetValue("PersonaName", out var persona);
                fields.TryGetValue("RememberPassword", out var remember);
                fields.TryGetValue("AutoLogin", out var auto);
                fields.TryGetValue("MostRecent", out var recent);
                fields.TryGetValue("Timestamp", out var ts);
                if (string.IsNullOrWhiteSpace(account)) continue;

                LocalUsers.Add(new SteamLocalUser
                {
                    SteamId64 = user.Name!,
                    AccountName = account ?? "",
                    PersonaName = persona ?? "",
                    RememberPassword = remember == "1",
                    AutoLogin = auto == "1",
                    MostRecent = recent == "1",
                    Timestamp = long.TryParse(ts, out var t) ? t : 0,
                });
            }
        }
        catch { }

        if (LocalUsers.Count == 0) return;

        MergeAccountsSection();

        ActiveLocalUser =
            LocalUsers.FirstOrDefault(u => !string.IsNullOrEmpty(AutoLoginUsername) &&
                u.AccountName.Equals(AutoLoginUsername, StringComparison.OrdinalIgnoreCase))
            ?? LocalUsers.FirstOrDefault(u => u.MostRecent)
            ?? LocalUsers.FirstOrDefault(u => u.AutoLogin)
            ?? LocalUsers.OrderByDescending(u => u.Timestamp).First();
        if (!IsSteamKitConnected) _currentSteamId = ActiveLocalUser?.SteamId64;
    }

    public void RefreshLocalAccounts()
    {
        DetectAutoLoginUser();
        if (_steamInstallPath != null) ReadLocalUsers();
    }

    /// <summary>
    /// The config.vdf Accounts section remembers every account that ever signed
    /// in on this machine (account name → SteamID), including ones already
    /// pruned from loginusers.vdf. Merge those in as lightweight entries.
    /// </summary>
    private void MergeAccountsSection()
    {
        try
        {
            var configPath = Path.Combine(_steamInstallPath!, "config", "config.vdf");
            if (!File.Exists(configPath)) return;

            var content = File.ReadAllText(configPath);
            foreach (Match m in AccountsEntryRegex().Matches(content))
            {
                var account = m.Groups[1].Value;
                var steamId = m.Groups[2].Value;
                if (LocalUsers.Any(u => u.AccountName.Equals(account, StringComparison.OrdinalIgnoreCase)))
                    continue;
                LocalUsers.Add(new SteamLocalUser
                {
                    SteamId64 = steamId,
                    AccountName = account,
                });
            }
        }
        catch { }
    }

    private void DetectAutoLoginUser()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            AutoLoginUsername = key?.GetValue("AutoLoginUser")?.ToString();
        }
        catch { }
    }

    public string? GetDepotKey(uint depotId)
    {
        lock (_sync)
            return _depotKeys.TryGetValue(depotId, out var key) ? key : null;
    }

    public List<SteamGame> GetInstalledGames()
    {
        var games = new List<SteamGame>();
        if (_steamInstallPath == null) return games;

        var libPath = Path.Combine(_steamInstallPath, "config", "libraryfolders.vdf");
        if (!File.Exists(libPath)) return games;

        foreach (var lp in ParseLibraryFolders(libPath))
        {
            var dir = Path.Combine(lp, "steamapps");
            if (!Directory.Exists(dir)) continue;

            foreach (var acf in Directory.GetFiles(dir, "appmanifest_*.acf"))
            {
                var game = ParseAppManifest(acf);
                if (game == null) continue;

                foreach (var depot in game.Depots)
                {
                    var key = GetDepotKey(depot.DepotId);
                    if (key != null) depot.DepotKey = key;
                }
                games.Add(game);
            }
        }
        return games;
    }

    public List<SteamGame> GetOwnedGames()
    {
        var games = new List<SteamGame>();

        List<uint> ids;
        lock (_sync) ids = [.. _ownedAppIds];

        foreach (var appId in ids)
        {
            string name;
            string token;
            Dictionary<uint, SteamDepot> picsById;
            List<SteamDepot> depots;
            lock (_sync)
            {
                name = _appNames.TryGetValue(appId, out var n) ? n : $"App {appId}";
                token = _appTokens.TryGetValue(appId, out var t) ? t
                    : _luaAppTokens.TryGetValue(appId, out var lt) ? lt : "";
                picsById = _appDepots.TryGetValue(appId, out var d)
                    ? d.ToDictionary(x => x.DepotId)
                    : new Dictionary<uint, SteamDepot>();
                depots = _appDepots.TryGetValue(appId, out var dd)
                    ? dd.Select(x => new SteamDepot
                    {
                        DepotId = x.DepotId,
                        Name = x.Name,
                        DepotKey = _depotKeys.TryGetValue(x.DepotId, out var k) ? k : "",
                        ParentAppId = x.ParentAppId,
                        IsShared = x.IsShared,
                        SharedFrom = x.SharedFrom,
                        IsRedistributable = x.IsRedistributable,
                    }).ToList()
                    : [];
            }

            var game = new SteamGame
            {
                AppId = appId,
                Name = name,
                Token = token,
                BaseDepotKey = GetDepotKey(appId) ?? ""
            };
            foreach (var depot in GetDepotsForApp(appId, depots))
            {
                // Backfill everything Steam knows about this depot ID.
                if (picsById.TryGetValue(depot.DepotId, out var pics))
                {
                    if (depot.Name == $"Depot {depot.DepotId}")
                        depot.Name = pics.Name;
                    if (depot.ParentAppId == 0)
                        depot.ParentAppId = pics.ParentAppId;
                    depot.IsShared |= pics.IsShared;
                    if (string.IsNullOrEmpty(depot.SharedFrom))
                        depot.SharedFrom = pics.SharedFrom;
                    depot.IsRedistributable |= pics.IsRedistributable;
                }
                game.Depots.Add(depot);
            }
            game.Dlcs = BuildDlcs(appId);
            games.Add(game);
        }

        return games;
    }

    private List<SteamDepot> GetDepotsForApp(uint appId, List<SteamDepot>? picsDepots = null)
    {
        var depots = new List<SteamDepot>();
        if (_steamInstallPath != null)
        {
            var libPath = Path.Combine(_steamInstallPath, "config", "libraryfolders.vdf");
            if (File.Exists(libPath))
            {
                foreach (var lp in ParseLibraryFolders(libPath))
                {
                    var dir = Path.Combine(lp, "steamapps");
                    if (!Directory.Exists(dir)) continue;

                    var acf = Path.Combine(dir, $"appmanifest_{appId}.acf");
                    if (File.Exists(acf))
                    {
                        var content = File.ReadAllText(acf);
                        var manifests = ParseInstalledDepotManifests(content);
                        foreach (Match m in DepotIdRegex().Matches(content))
                        {
                            if (uint.TryParse(m.Groups[1].Value, out var depotId))
                            {
                                var key = GetDepotKey(depotId);
                                manifests.TryGetValue(depotId, out var manifest);
                                depots.Add(new SteamDepot
                                {
                                    DepotId = depotId,
                                    Name = ResolveDepotName(depotId, $"Depot {depotId}"),
                                    DepotKey = key ?? "",
                                    ManifestId = manifest.manifest ?? "",
                                    ManifestSize = manifest.size,
                                });
                            }
                        }
                        }
                    }
                }
            }
        if (depots.Count == 0 && picsDepots != null)
            depots.AddRange(picsDepots);

        return depots.GroupBy(d => d.DepotId).Select(g => g.First()).ToList();
    }

    public int OwnedAppCount
    {
        get { lock (_sync) return _ownedAppIds.Count; }
    }

    /// <summary>
    /// Licensed games only: every app Steam reports as owned by this account
    /// (WebAPI + license merge). Installed-but-unlicensed leftovers are excluded.
    /// Manifest depots are folded in for installed ones.
    /// </summary>
    public List<SteamGame> GetLicensedGames()
        => GetOwnedGames().OrderBy(g => g.Name).ToList();

    public List<SteamGame> GetAllGames()
    {
        var installed = GetInstalledGames();
        var installedIds = new HashSet<uint>(installed.Select(g => g.AppId));

        foreach (var ownedGame in GetOwnedGames())
        {
            if (!installedIds.Contains(ownedGame.AppId))
                installed.Add(ownedGame);
        }

        return installed.OrderBy(g => g.Name).ToList();
    }

    // ── Token store (JSON, backward compatible) ────────────────────

    private void LoadTokens(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return;
        _accountName = accountName;
        var saved = _tokenStore.Get(accountName);
        if (saved == null) return;
        _savedRefreshToken = saved.Value.Token;
        _steamId64 = saved.Value.SteamId;
    }

    private void SaveTokens(string refreshToken, string? accessToken = null, string? accountName = null, ulong steamId = 0)
    {
        lock (_lifetimeGate)
        {
            if (_retired) return;
            _savedRefreshToken = refreshToken;
            if (!string.IsNullOrEmpty(accessToken)) _accessToken = accessToken;
            if (!string.IsNullOrEmpty(accountName)) _accountName = accountName;
            if (steamId != 0) _steamId64 = steamId;

            // Only persist an authenticated account, never a pending QR/password attempt.
            if (IsSteamKitConnected && _steamId64 != 0 && !string.IsNullOrEmpty(_accountName))
            {
                try { _tokenStore.Save(_accountName, _steamId64, refreshToken); }
                catch { UiInvoke(() => OnError?.Invoke("Signed in, but this account's login could not be saved securely.")); }
            }
        }
    }

    public void ClearSavedTokens()
    {
        lock (_lifetimeGate)
        {
            if (_retired) return;
            if (_accountName != null)
            {
                try { _tokenStore.Remove(_accountName); }
                catch { UiInvoke(() => OnError?.Invoke("The expired saved login could not be removed.")); }
            }
            _savedRefreshToken = null;
            _accessToken = null;
        }
    }

    // ── SteamKit2 connection ───────────────────────────────────────

    private async Task EnsureConnectedAsync(LoginMode mode, string? username, string? password)
    {
        _loginMode = mode;
        _pendingUsername = username;
        _pendingPassword = password;

        if (_retired) return;

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>();
        _steamApps = _steamClient.GetHandler<SteamApps>();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

        UiInvoke(() => OnStatusUpdate?.Invoke("Connecting to Steam network..."));

        _steamClient.Connect();

        _callbackLoopRunning = true;
        _ = Task.Run(async () =>
        {
            while (_callbackLoopRunning && !_retired)
            {
                try
                {
                    _callbackManager?.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
                catch { }
                await Task.Delay(100);
            }
        });

        await Task.CompletedTask;
    }

    public Task StartSteamKitAuth(string? username = null, string? password = null)
    {
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            return EnsureConnectedAsync(LoginMode.Credentials, username.Trim(), password);
        LoadTokens(username ?? _tokenStore.LastAccount);
        if (string.IsNullOrEmpty(_savedRefreshToken))
        {
            UiInvoke(() => OnLoginNeeded?.Invoke("Choose a remembered account, or sign in with QR/password."));
            return Task.CompletedTask;
        }
        return EnsureConnectedAsync(LoginMode.Auto, username, null);
    }

    public Task StartSteamKitLocalLogin(SteamLocalUser account)
    {
        _accountName = account.AccountName;
        ulong.TryParse(account.SteamId64, out _steamId64);
        _savedRefreshToken = SteamLocalTokenReader.Read(account);
        if (_savedRefreshToken == null) LoadTokens(account.AccountName);
        if (string.IsNullOrEmpty(_savedRefreshToken))
        {
            UiInvoke(() => OnLoginNeeded?.Invoke("No usable remembered login for this account. Sign in to Steam with Remember me, or use QR/password here."));
            return Task.CompletedTask;
        }
        return EnsureConnectedAsync(LoginMode.Local, account.AccountName, null);
    }

    public Task StartSteamKitQRLogin()
        => EnsureConnectedAsync(LoginMode.Qr, null, null);

    public void CancelQRLogin()
    {
        try { _qrCts?.Cancel(); } catch { }
        UiInvoke(() => OnStatusUpdate?.Invoke("QR login cancelled."));
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        if (_retired) return;

        if (_loginMode == LoginMode.Qr)
        {
            _ = DoQrLogin();
            return;
        }

        if (_loginMode == LoginMode.Credentials
            && !string.IsNullOrEmpty(_pendingUsername)
            && !string.IsNullOrEmpty(_pendingPassword))
        {
            _ = DoCredentialAuth(_pendingUsername, _pendingPassword);
            return;
        }

        if (!string.IsNullOrEmpty(_savedRefreshToken))
        {
            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username = _accountName ?? _pendingUsername ?? AutoLoginUsername,
                AccessToken = _savedRefreshToken,
                ShouldRememberPassword = true,
                LoginID = _loginId,
            });
            return;
        }

        if (!string.IsNullOrEmpty(_pendingUsername) && !string.IsNullOrEmpty(_pendingPassword))
        {
            _loginMode = LoginMode.Credentials;
            _ = DoCredentialAuth(_pendingUsername, _pendingPassword);
            return;
        }

        UiInvoke(() => OnLoginNeeded?.Invoke("Log in with the Steam app (QR) or enter your Steam credentials."));
    }

    private async Task DoQrLogin()
    {
        try
        {
            UiInvoke(() => OnStatusUpdate?.Invoke("Generating Steam sign-in code..."));

            _qrCts?.Cancel();
            _qrCts = new CancellationTokenSource();

            var session = await _steamClient!.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = "LuaShareX",
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                WebsiteID = "Client",
            });

            _qrSession = session;
            if (_retired) return;
            session.ChallengeURLChanged = () =>
                UiInvoke(() => OnQRCodeReady?.Invoke(session.ChallengeURL));

            UiInvoke(() => OnQRCodeReady?.Invoke(session.ChallengeURL));
            UiInvoke(() => OnStatusUpdate?.Invoke("Scan the QR code with the Steam Mobile app to sign in."));

            var pollResult = await session.PollingWaitForResultAsync(_qrCts.Token);
            if (_retired) return;

            SaveTokens(pollResult.RefreshToken, pollResult.AccessToken, pollResult.AccountName, _steamId64);

            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
                ShouldRememberPassword = true,
                LoginID = _loginId,
            });
        }
        catch (OperationCanceledException)
        {
            UiInvoke(() => OnStatusUpdate?.Invoke("QR login cancelled."));
        }
        catch (Exception ex)
        {
            Log($"QR login failed: {ex.Message}");
            UiInvoke(() => OnError?.Invoke($"QR login failed: {ex.Message}"));
            UiInvoke(() => OnLoginNeeded?.Invoke("QR sign-in failed. Try again or choose another sign-in method."));
        }
    }

    private async Task DoCredentialAuth(string username, string password)
    {
        try
        {
            UiInvoke(() => OnStatusUpdate?.Invoke("Authenticating with Steam..."));

            var authenticator = new DelegateAuthenticator
            {
                DeviceCodeFn = async _ => await RequestGuardCodeAsync("Enter your Steam Guard app code:"),
                EmailCodeFn = async (email, _) => await RequestGuardCodeAsync($"Enter the Steam Guard email code sent to {email}:"),
                MobileConfirmationFn = () => Task.FromResult(false),
            };

            var session = await _steamClient!.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                DeviceFriendlyName = "LuaShareX",
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                WebsiteID = "Client",
                Authenticator = authenticator,
            });

            if (_retired) return;
            var pollResult = await session.PollingWaitForResultAsync(_lifetime.Token);
            if (_retired) return;

            SaveTokens(pollResult.RefreshToken, pollResult.AccessToken, pollResult.AccountName, _steamId64);

            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
                ShouldRememberPassword = true,
                LoginID = _loginId,
            });
        }
        catch (Exception ex)
        {
            Log($"Auth failed: {ex.Message}");
            UiInvoke(() => OnError?.Invoke($"Auth failed: {ex.Message}"));
            UiInvoke(() => OnLoginNeeded?.Invoke("Sign-in failed. Check your credentials or choose another method."));
        }
        finally { _pendingPassword = null; }
    }

    public Task<string> RequestGuardCodeAsync(string prompt)
    {
        if (_retired) return Task.FromCanceled<string>(new CancellationToken(true));
        _guardTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiInvoke(() => OnGuardCodeNeeded?.Invoke(prompt));
        return _guardTcs.Task.WaitAsync(_lifetime.Token);
    }

    public void SubmitGuardCode(string code)
    {
        _guardTcs?.TrySetResult(code?.Trim() ?? string.Empty);
    }

    private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (_retired) return;
        switch (callback.Result)
        {
            case EResult.OK:
                if (_steamClient?.SteamID != null)
                {
                    var actualId = _steamClient.SteamID.ConvertToUInt64();
                    if (_steamId64 != 0 && _steamId64 != actualId)
                    {
                        _steamClient.Disconnect();
                        UiInvoke(() => OnLoginNeeded?.Invoke("The saved login does not match the selected account. Use QR/password to sign in again."));
                        return;
                    }
                    _steamId64 = actualId;
                    _currentSteamId = actualId.ToString();
                    IsSteamKitConnected = true;
                    if (_steamId64 != 0 && !string.IsNullOrEmpty(_savedRefreshToken))
                        SaveTokens(_savedRefreshToken, _accessToken, _accountName, _steamId64);
                }
                UiInvoke(() => OnSteamKitConnected?.Invoke());
                await FetchLibraryDirectAsync();
                break;

            case EResult.InvalidPassword:
            case EResult.AccountLogonDenied:
            case EResult.Expired:
            case EResult.Revoked:
            case EResult.AccountNotFound:
                IsSteamKitConnected = false;
                ClearSavedTokens();
                UiInvoke(() => OnLoginNeeded?.Invoke("Session expired. Sign in again with the Steam app (QR) or your credentials."));
                break;

            default:
                IsSteamKitConnected = false;
                UiInvoke(() => OnLoginNeeded?.Invoke($"Login failed: {callback.Result}. Try QR/password or select another account."));
                break;
        }
    }

    /// <summary>
    /// Fetches depot decryption keys for a list of depots, only storing keys
    /// when Steam actually returns EResult.OK with a non-empty key.
    /// Steam exposes no batch endpoint for depot keys (single-depot requests
    /// only — DepotDownloader loops the same way), so the list goes out
    /// 8-at-a-time instead of one "give me the key" per depot round-trip
    /// blocking the next. Returns (succeeded, failed) counts.
    /// </summary>
    private async Task<(int ok, int fail)> FetchDepotKeysParallelAsync(
        List<(uint appId, uint depotId)> missing, int emptySkipped = 0)
    {
        if (_steamApps == null || missing.Count == 0) return (0, 0);

        // Resolve the final work list up front: skip keys we already hold and
        // pin each depot's owning app now, so every spawned task does exactly
        // one request and no gate slot is spent on no-ops.
        List<(uint depotId, uint parentApp)> todo;
        lock (_sync)
        {
            todo = missing
                .Where(m => !_depotKeys.ContainsKey(m.depotId))
                .Select(m => (m.depotId, ResolveParentApp(m.appId, m.depotId)))
                .Distinct()
                .ToList();
        }
        if (todo.Count == 0) return (0, 0);

        UiInvoke(() => _toast.Show("Depot keys", $"Fetching {todo.Count} key(s) from Steam..."));
        int ok = 0, fail = 0;
        using var gate = new SemaphoreSlim(8);

        var tasks = todo.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                var result = await _steamApps.GetDepotDecryptionKey(item.depotId, item.parentApp);
                if (result.Result is EResult.Timeout or EResult.NoConnection or EResult.ServiceUnavailable)
                {
                    await Task.Delay(500);
                    result = await _steamApps.GetDepotDecryptionKey(item.depotId, item.parentApp);
                }
                if (result.Result == EResult.OK && result.DepotKey is { Length: > 0 })
                {
                    var key = Convert.ToHexString(result.DepotKey).ToLowerInvariant();
                    lock (_sync) _depotKeys[item.depotId] = key;
                    Interlocked.Increment(ref ok);
                }
                else
                {
                    Log($"DepotKey denied: depot={item.depotId} app={item.parentApp} result={result.Result}");
                    Interlocked.Increment(ref fail);
                }
            }
            catch (Exception ex)
            {
                Log($"DepotKey error: depot={item.depotId} app={item.parentApp}: {ex.Message}");
                Interlocked.Increment(ref fail);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        if (ok > 0 || fail > 0 || emptySkipped > 0)
            UiInvoke(() => _toast.Show("Depot keys", $"{ok} ready{(fail > 0 ? $", {fail} denied by Steam" : "")}{(emptySkipped > 0 ? $", {emptySkipped} empty (no key needed)" : "")}.",
                error: ok == 0 && fail > 0));
        return (ok, fail);
    }

    private uint ResolveParentApp(uint appId, uint depotId)
    {
        lock (_sync)
        {
            if (_appDepots.TryGetValue(appId, out var depots))
            {
                foreach (var d in depots)
                {
                    if (d.DepotId == depotId && d.ParentAppId != 0)
                        return d.ParentAppId;
                }
            }
        }
        return appId;
    }

    /// <summary>
    /// Export-time fetch: ensures the given games have fresh depot lists
    /// (names, parents, redist flags), depot keys and app tokens — but only
    /// for these games, nothing bulk. Returns (keysOk, keysFail, tokensOk).
    /// Mutates the passed game objects in place.
    /// </summary>
    public async Task<(int keysOk, int keysFail, int tokensOk)> EnsureExportDataAsync(List<SteamGame> games)
    {
        if (games.Count == 0) return (0, 0, 0);

        var appIds = games.Select(g => g.AppId).Distinct().ToList();

        if (_steamApps == null || !IsSteamKitConnected)
        {
            RefreshGamesFromStore(games);
            return (0, 0, 0);
        }

        UiInvoke(() => OnStatusUpdate?.Invoke($"Preparing {games.Count} game(s)..."));
        await EnsureAppDepotsForAsync(appIds);
        await ClassifySharedDepotsAsync();
        RefreshGamesFromStore(games);

        var missing = games
            .SelectMany(g => g.Depots.Where(d => string.IsNullOrEmpty(d.DepotKey))
                .Select(d => (appId: g.AppId, depotId: d.DepotId, manifest: d.ManifestId, size: d.ManifestSize)))
            .Concat(games.Where(g => string.IsNullOrEmpty(g.BaseDepotKey))
                .Select(g => (appId: g.AppId, depotId: g.AppId, manifest: "", size: 0UL)))
            .Distinct()
            .ToList();

        // Empty depots (known manifest, zero bytes — e.g. Yakuza 0's 638974)
        // have no decryption key; Steam denies the request, so don't ask.
        var empty = missing.Where(m => !string.IsNullOrEmpty(m.manifest) && m.size == 0).ToList();
        var wanted = missing.Except(empty).Select(m => (m.appId, m.depotId)).ToList();
        if (empty.Count > 0)
            Log($"Skipping {empty.Count} empty depot(s) that need no key: {string.Join(",", empty.Select(m => m.depotId))}");

        var (ok, fail) = await FetchDepotKeysParallelAsync(wanted, empty.Count);

        // PICS access tokens are unsigned 64-bit values. Ownership tickets from
        // GetAppOwnershipTicket are opaque byte blobs and are not addtoken values.
        await EnsureDlcNamesAsync(appIds);
        var tokenNeeded = games.Where(g => !IsValidAppToken(g.Token)).Select(g => g.AppId).ToList();
        List<uint> dlcIds;
        lock (_sync) dlcIds = appIds
            .Where(id => _appDlcs.ContainsKey(id))
            .SelectMany(id => _appDlcs[id])
            .Distinct()
            .ToList();
        var tokensOk = await FetchAppTokensForAsync(tokenNeeded.Concat(dlcIds).Distinct().ToList());

        RefreshGamesFromStore(games);
        Log($"Export prep: {ok} keys ok, {fail} denied, {tokensOk} app tokens");
        return (ok, fail, tokensOk);
    }

    /// <summary>
    /// Rebuilds each game's Token + Depots from the latest in-memory store
    /// (local files + PICS + fetched keys), preserving selection state.
    /// </summary>
    private void RefreshGamesFromStore(List<SteamGame> games)
    {
        foreach (var game in games)
        {
            string token;
            Dictionary<uint, SteamDepot> picsById;
            List<SteamDepot> picsDepots;
            string name;
            lock (_sync)
            {
                token = _appTokens.TryGetValue(game.AppId, out var t) && IsValidAppToken(t) ? t
                    : IsValidAppToken(game.Token) ? game.Token : "";
                picsById = _appDepots.TryGetValue(game.AppId, out var d)
                    ? d.ToDictionary(x => x.DepotId)
                    : new Dictionary<uint, SteamDepot>();
                picsDepots = _appDepots.TryGetValue(game.AppId, out var dd)
                    ? dd.Select(x => new SteamDepot
                    {
                        DepotId = x.DepotId,
                        Name = x.Name,
                        DepotKey = _depotKeys.TryGetValue(x.DepotId, out var k) ? k : "",
                        ParentAppId = x.ParentAppId,
                        IsShared = x.IsShared,
                        SharedFrom = x.SharedFrom,
                        IsRedistributable = x.IsRedistributable,
                        ManifestId = x.ManifestId,
                        ManifestSize = x.ManifestSize,
                    }).ToList()
                    : [];
                name = _appNames.TryGetValue(game.AppId, out var n) ? n : game.Name;
            }

            game.Token = token;
            game.BaseDepotKey = GetDepotKey(game.AppId) ?? "";
            game.Name = name;
            var fresh = GetDepotsForApp(game.AppId, picsDepots);
            foreach (var depot in fresh)
            {
                if (picsById.TryGetValue(depot.DepotId, out var pics))
                {
                    if (depot.Name == $"Depot {depot.DepotId}")
                        depot.Name = pics.Name;
                    if (depot.ParentAppId == 0)
                        depot.ParentAppId = pics.ParentAppId;
                    else if (depot.ParentAppId == game.AppId
                        && pics.ParentAppId != 0 && pics.ParentAppId != game.AppId)
                    {
                        // Local manifests assume own-app ownership; PICS knows better
                        // (dlcappid / depotfromapp). Adopt it so key requests go out
                        // with the owning app instead of being denied.
                        depot.ParentAppId = pics.ParentAppId;
                        depot.IsShared = true;
                        depot.SharedFrom = pics.ParentAppId.ToString();
                    }
                    depot.IsShared |= pics.IsShared;
                    if (string.IsNullOrEmpty(depot.SharedFrom))
                        depot.SharedFrom = pics.SharedFrom;
                    depot.IsRedistributable |= pics.IsRedistributable;
                    if (string.IsNullOrEmpty(depot.ManifestId) && !string.IsNullOrEmpty(pics.ManifestId))
                    {
                        depot.ManifestId = pics.ManifestId;
                        depot.ManifestSize = pics.ManifestSize;
                    }
                }
                var key = GetDepotKey(depot.DepotId);
                if (!string.IsNullOrEmpty(key))
                    depot.DepotKey = key;
            }
            game.Depots = fresh;
            game.Dlcs = BuildDlcs(game.AppId);
        }
    }

    // ── Manifest downloads (Steam request codes + Steam CDN) ───

    private List<Server>? _cdnServers;

    /// <summary>Steam's depotcache folder — downloaded manifests install here.</summary>
    private string? DepotCacheDir =>
        _steamInstallPath is { } p ? Path.Combine(p, "depotcache") : null;

    /// <summary>
    /// Manifest request codes straight from Steam (no third party).
    /// Logged-in sessions get codes for owned depots; local mode tries a
    /// single anonymous session (works for public depots).
    /// </summary>
    private async Task<Dictionary<(uint depotId, ulong manifestId), ulong>> GetSteamManifestCodesAsync(
        List<(uint appId, uint depotId, ulong manifestId, uint parentApp)> targets, CancellationToken ct)
    {
        var codes = new Dictionary<(uint, ulong), ulong>();

        if (_steamClient != null && IsSteamKitConnected)
        {
            var content = _steamClient.GetHandler<SteamContent>();
            if (content != null)
            {
                foreach (var t in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var code = await content.GetManifestRequestCode(
                            t.depotId, t.parentApp != 0 ? t.parentApp : t.appId, t.manifestId, null, null);
                        if (code != 0) codes[(t.depotId, t.manifestId)] = code;
                    }
                    catch { }
                }
            }
            return codes;
        }

        await RunAnonymousAsync(async client =>
        {
            var content = client.GetHandler<SteamContent>();
            if (content == null) return;
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var code = await content.GetManifestRequestCode(
                        t.depotId, t.parentApp != 0 ? t.parentApp : t.appId, t.manifestId, null, null);
                    if (code != 0) codes[(t.depotId, t.manifestId)] = code;
                }
                catch { }
            }
        }, ct);
        return codes;
    }

    /// <summary>Runs work on a throwaway anonymous Steam session (~10-30s).
    /// Used in local mode for CDN server discovery and manifest request codes.</summary>
    private static async Task RunAnonymousAsync(Func<SteamClient, Task> work, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var client = new SteamClient();
        var mgr = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mgr.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            try { user?.LogOnAnonymous(); }
            catch { tcs.TrySetResult(false); }
        });
        mgr.Subscribe<SteamUser.LoggedOnCallback>(cb => tcs.TrySetResult(cb.Result == EResult.OK));
        mgr.Subscribe<SteamClient.DisconnectedCallback>(_ => tcs.TrySetResult(false));

        var pump = Task.Run(async () =>
        {
            while (!linked.Token.IsCancellationRequested)
            {
                try { mgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(500)); } catch { }
                try { await Task.Delay(100, linked.Token); } catch { break; }
            }
        }, linked.Token);

        try
        {
            client.Connect();
            if (!await tcs.Task.WaitAsync(linked.Token)) return;
            await work(client);
        }
        catch { }
        finally
        {
            try { linked.Cancel(); } catch { }
            try { await pump; } catch { }
            try { client.Disconnect(); } catch { }
        }
    }

    /// <summary>
    /// Downloads .manifest files for every depot of the given games that has a
    /// known manifest id. Files install as {depotid}_{gid}.manifest straight
    /// into Steam's depotcache; existing files are skipped.
    /// Returns (downloaded, skipped, failed).
    /// </summary>
    public async Task<(int ok, int skipped, int fail)> DownloadManifestsAsync(
        List<SteamGame> games, IProgress<double>? progress, CancellationToken ct = default)
    {
        var folder = DepotCacheDir;
        if (string.IsNullOrEmpty(folder))
            throw new InvalidOperationException("Steam folder not found, so there is no depotcache to install into.");

        var targets = games
            .SelectMany(g => g.Depots.Select(d => (appId: g.AppId, depot: d)))
            .Where(t => ulong.TryParse(t.depot.ManifestId, out var gid) && gid != 0)
            .Select(t => (t.appId, depotId: t.depot.DepotId, manifestId: ulong.Parse(t.depot.ManifestId),
                           parentApp: t.depot.ParentAppId, key: t.depot.DepotKey))
            .Distinct()
            .ToList();
        if (targets.Count == 0) return (0, 0, 0);
        Directory.CreateDirectory(folder);

        UiInvoke(() => OnStatusUpdate?.Invoke($"Requesting {targets.Count} manifest code(s) from Steam..."));

        // Phase 1: request codes from Steam itself (logged-in session, else one
        // anonymous session for the whole batch).
        var codes = await GetSteamManifestCodesAsync(
            targets.Select(t => (t.appId, t.depotId, t.manifestId, t.parentApp)).ToList(), ct);
        var coded = targets
            .Where(t => codes.TryGetValue((t.depotId, t.manifestId), out var c) && c != 0)
            .Select(t => (t.appId, t.depotId, t.manifestId, t.parentApp, t.key, code: codes[(t.depotId, t.manifestId)]))
            .ToList();

        int done = targets.Count - coded.Count;
        progress?.Report((double)done / targets.Count);

        // Phase 2: bytes, a few at a time.
        var servers = await GetCdnServersAsync(ct);
        if (servers.Count == 0)
            return (0, 0, targets.Count);

        int ok = 0, skipped = 0, fail = 0;
        using var gate = new SemaphoreSlim(3);
        var tasks = coded.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = Path.Combine(folder, $"{item.depotId}_{item.manifestId}.manifest");
                if (File.Exists(path)) { Interlocked.Increment(ref skipped); return; }
                if (await TryDownloadManifestAsync(item, folder, servers, ct))
                    Interlocked.Increment(ref ok);
                else
                    Interlocked.Increment(ref fail);
            }
            catch { Interlocked.Increment(ref fail); }
            finally
            {
                gate.Release();
                progress?.Report((double)Interlocked.Increment(ref done) / targets.Count);
            }
        });
        await Task.WhenAll(tasks);
        Log($"Manifests: {ok} ok, {skipped} skipped, {fail} failed");
        return (ok, skipped, fail);
    }

    private async Task<List<Server>> GetCdnServersAsync(CancellationToken ct)
    {
        if (_cdnServers is { Count: > 0 }) return _cdnServers;

        if (_steamClient != null && IsSteamKitConnected)
        {
            try
            {
                var content = _steamClient.GetHandler<SteamContent>();
                if (content != null)
                {
                    var list = await content.GetServersForSteamPipe().WaitAsync(ct);
                    var filtered = FilterCdnServers(list);
                    if (filtered.Count > 0) return _cdnServers = filtered;
                }
            }
            catch { }
        }

        // Local mode: brief anonymous session just for server discovery.
        try
        {
            var anon = await FetchServersAnonymouslyAsync(ct);
            if (anon.Count > 0) return _cdnServers = anon;
        }
        catch { }
        return [];
    }

    private static List<Server> FilterCdnServers(IEnumerable<Server> servers) =>
        servers.Where(s => !s.SteamChinaOnly && !string.IsNullOrEmpty(s.Host)).OrderBy(s => s.WeightedLoad).ToList();

    private static async Task<List<Server>> FetchServersAnonymouslyAsync(CancellationToken ct)
    {
        List<Server> found = [];
        await RunAnonymousAsync(async client =>
        {
            try
            {
                var content = client.GetHandler<SteamContent>();
                if (content == null) return;
                var list = await content.GetServersForSteamPipe().WaitAsync(ct);
                found = FilterCdnServers(list);
            }
            catch { }
        }, ct);
        return found;
    }

    private async Task<bool> TryDownloadManifestAsync(
        (uint appId, uint depotId, ulong manifestId, uint parentApp, string key, ulong code) item,
        string folder, List<Server> servers, CancellationToken ct)
    {
        byte[]? depotKey = null;
        try
        {
            if (!string.IsNullOrEmpty(item.key) && item.key.Length == 64)
                depotKey = Convert.FromHexString(item.key);
        }
        catch { depotKey = null; }

        var path = Path.Combine(folder, $"{item.depotId}_{item.manifestId}.manifest");
        var tried = servers.Take(3).ToList();

        foreach (var server in tried)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cdn = new Client(_steamClient ?? new SteamClient());
                var manifest = await cdn.DownloadManifestAsync(
                    item.depotId, item.manifestId, item.code, server, depotKey);
                if (!await SaveManifestAsync(manifest, path, depotKey, ct))
                    continue;
                return true;
            }
            catch { /* next server */ }
        }

        // Authenticated retry when signed in (private depots may 401/403).
        if (IsSteamKitConnected && _steamClient != null && tried.Count > 0)
        {
            try
            {
                var content = _steamClient.GetHandler<SteamContent>();
                if (content == null) return false;
                var token = await content.GetCDNAuthToken(
                    item.depotId, item.parentApp != 0 ? item.parentApp : item.appId, tried[0].Host!);
                if (!string.IsNullOrEmpty(token?.Token))
                {
                    using var cdn = new Client(_steamClient);
                    var manifest = await cdn.DownloadManifestAsync(
                        item.depotId, item.manifestId, item.code, tried[0], depotKey, null, token.Token);
                    if (await SaveManifestAsync(manifest, path, depotKey, ct))
                        return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static async Task<bool> SaveManifestAsync(
        DepotManifest manifest, string path, byte[]? depotKey, CancellationToken ct)
    {
        try
        {
            if (depotKey != null)
            {
                try { manifest.DecryptFilenames(depotKey); } catch { }
            }
            using var ms = new MemoryStream();
            manifest.Serialize(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return false;
            // Sanity: must re-parse as a manifest, otherwise don't write junk.
            DepotManifest.Deserialize(new MemoryStream(bytes, writable: false));
            await File.WriteAllBytesAsync(path, bytes, ct);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// PICS appinfo (depot IDs + names) for specific apps missing depot lists.
    /// </summary>
    private async Task EnsureAppDepotsForAsync(List<uint> appIds)
    {
        if (_steamApps == null) return;

        List<uint> lacking;
        lock (_sync) lacking = appIds.Where(id => !_appDepots.ContainsKey(id)).Distinct().ToList();
        if (lacking.Count == 0) return;

        UiInvoke(() => OnStatusUpdate?.Invoke($"Resolving depots for {lacking.Count} app(s)..."));

        var tokenResult = await _steamApps.PICSGetAccessTokens(lacking, Array.Empty<uint>());

        foreach (var batch in lacking.Chunk(30))
        {
            var requests = batch.Select(id =>
            {
                tokenResult.AppTokens.TryGetValue(id, out var token);
                return new SteamApps.PICSRequest(id, token);
            }).ToList();

            try
            {
                var result = await _steamApps.PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>());
                if (result.Results != null)
                {
                    foreach (var cb in result.Results)
                    {
                        if (cb.Apps != null)
                        {
                            foreach (var appInfo in cb.Apps.Values)
                                MergeAppInfo(appInfo.ID, appInfo.KeyValues, !_webApiOk);
                        }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Builds the DLC list for an app from discovered listofdlc ids,
    /// PICS names and any fetched app access tokens.
    /// </summary>
    private List<SteamDlc> BuildDlcs(uint appId)
    {
        lock (_sync)
        {
            if (!_appDlcs.TryGetValue(appId, out var ids)) return [];
            return ids.Select(id => new SteamDlc
            {
                AppId = id,
                Name = _appNames.TryGetValue(id, out var n) && n != $"App {id}" && !string.IsNullOrEmpty(n)
                    ? n
                    : $"AppID {id}",
                Token = _appTokens.TryGetValue(id, out var t) ? t : "",
            }).ToList();
        }
    }

    /// <summary>
    /// Resolves display names for the DLCs of the given apps via PICS.
    /// allowNewIds is always false here: DLC lookups must never pollute
    /// the owned-apps list.
    /// </summary>
    private async Task EnsureDlcNamesAsync(List<uint> appIds)
    {
        if (_steamApps == null) return;

        List<uint> dlcIds;
        lock (_sync) dlcIds = appIds
            .Where(id => _appDlcs.ContainsKey(id))
            .SelectMany(id => _appDlcs[id])
            .Distinct()
            .ToList();
        lock (_sync) dlcIds = dlcIds.Where(id => !_appNames.ContainsKey(id)).ToList();
        if (dlcIds.Count == 0) return;

        UiInvoke(() => OnStatusUpdate?.Invoke($"Resolving {dlcIds.Count} DLC name(s)..."));

        var tokenResult = await _steamApps.PICSGetAccessTokens(dlcIds, Array.Empty<uint>());

        foreach (var batch in dlcIds.Chunk(30))
        {
            var requests = batch.Select(id =>
            {
                tokenResult.AppTokens.TryGetValue(id, out var token);
                return new SteamApps.PICSRequest(id, token);
            }).ToList();

            try
            {
                var result = await _steamApps.PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>());
                if (result.Results != null)
                {
                    foreach (var cb in result.Results)
                    {
                        if (cb.Apps != null)
                        {
                            foreach (var appInfo in cb.Apps.Values)
                                MergeAppInfo(appInfo.ID, appInfo.KeyValues, allowNewIds: false);
                        }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Fetches unsigned 64-bit PICS app access tokens for addtoken lines.
    /// Best effort. Returns tokens granted.
    /// </summary>
    private async Task<int> FetchAppTokensForAsync(List<uint> appIds)
    {
        if (_steamApps == null || appIds.Count == 0) return 0;

        List<uint> ids;
        lock (_sync) ids = appIds.Where(id => !_appTokens.ContainsKey(id)).Distinct().ToList();
        if (ids.Count == 0) return 0;

        try
        {
            var result = await _steamApps.PICSGetAccessTokens(ids, Array.Empty<uint>());
            var ok = 0;
            lock (_sync)
            {
                foreach (var appId in ids)
                {
                    if (!result.AppTokens.TryGetValue(appId, out var token) || token == 0) continue;
                    _appTokens[appId] = token.ToString(CultureInfo.InvariantCulture);
                    ok++;
                }
            }
            Log($"PICS app access tokens: {ok}/{ids.Count}");
            return ok;
        }
        catch (Exception ex)
        {
            Log($"PICS app access tokens failed: {ex.Message}");
            return 0;
        }
    }

    private static bool IsValidAppToken(string token) =>
        ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value != 0;

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        IsSteamKitConnected = false;
        UiInvoke(() => OnSteamKitDisconnected?.Invoke());
    }

    public void Disconnect()
    {
        lock (_lifetimeGate)
        {
            _retired = true;
            _callbackLoopRunning = false;
            _lifetime.Cancel();
            _guardTcs?.TrySetCanceled();
            try { _qrCts?.Cancel(); } catch { }
            _steamClient?.Disconnect();
            IsSteamKitConnected = false;
            _pendingPassword = null;
        }
    }

    // ── Direct library fetch: WebAPI first, PICS merge ─────────────

    private async Task FetchLibraryDirectAsync()
    {
        try
        {
            UiInvoke(() => OnStatusUpdate?.Invoke("Loading your Steam library..."));
            var webOk = await TryFetchOwnedGamesViaWebAPIAsync();
            if (webOk)
            {
                int count;
                lock (_sync) count = _ownedAppIds.Count;
                UiInvoke(() => OnOwnedGamesLoaded?.Invoke(count));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await EnsureAppDepotsAsync();
                        await ClassifySharedDepotsAsync();
                        int full;
                        lock (_sync) full = _ownedAppIds.Count;
                        UiInvoke(() => OnOwnedGamesLoaded?.Invoke(full));
                    }
                    catch (Exception ex)
                    {
                        Log($"Library completion failed: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log($"Library fetch failed: {ex.Message}");
        }
    }

    private async Task<string?> EnsureWebApiTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken))
            return _accessToken;

        try
        {
            if (_steamClient == null || string.IsNullOrEmpty(_savedRefreshToken) || _steamId64 == 0)
                return null;

            var result = await _steamClient.Authentication.GenerateAccessTokenForAppAsync(
                new SteamID(_steamId64), _savedRefreshToken);
            if (!string.IsNullOrEmpty(result.AccessToken))
            {
                _accessToken = result.AccessToken;
                if (!string.IsNullOrEmpty(result.RefreshToken))
                    SaveTokens(result.RefreshToken, result.AccessToken, _accountName, _steamId64);
                return _accessToken;
            }
        }
        catch (Exception ex)
        {
            Log($"GenerateAccessToken failed: {ex.Message}");
        }
        return null;
    }

    private volatile bool _webApiOk;

    private async Task<bool> TryFetchOwnedGamesViaWebAPIAsync()
    {
        try
        {
            if (_steamId64 == 0) return false;
            var token = await EnsureWebApiTokenAsync();
            if (string.IsNullOrEmpty(token)) return false;

            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/" +
                      $"?format=json&steamid={_steamId64}&include_appinfo=1&include_played_free_games=1" +
                      $"&access_token={Uri.EscapeDataString(token)}";

            using var res = await _http.GetAsync(url, _lifetime.Token);
            if (!res.IsSuccessStatusCode)
            {
                Log($"GetOwnedGames HTTP {(int)res.StatusCode}");
                _accessToken = null;
                token = await EnsureWebApiTokenAsync();
                if (string.IsNullOrEmpty(token)) return false;
                url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/" +
                      $"?format=json&steamid={_steamId64}&include_appinfo=1&include_played_free_games=1" +
                      $"&access_token={Uri.EscapeDataString(token)}";
                using var retry = await _http.GetAsync(url, _lifetime.Token);
                if (!retry.IsSuccessStatusCode) return false;
                return await ParseOwnedGamesResponse(await retry.Content.ReadAsStringAsync());
            }

            return await ParseOwnedGamesResponse(await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            Log($"GetOwnedGames failed: {ex.Message}");
            return false;
        }
    }

    private Task<bool> ParseOwnedGamesResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response))
                return Task.FromResult(false);
            if (!response.TryGetProperty("games", out var games))
                return Task.FromResult(false);

            int added = 0;
            lock (_sync)
            {
                foreach (var g in games.EnumerateArray())
                {
                    if (!g.TryGetProperty("appid", out var appIdEl)) continue;
                    var appId = (uint)appIdEl.GetInt32();
                    string? name = null;
                    if (g.TryGetProperty("name", out var nameEl))
                        name = nameEl.GetString();
                    if (!string.IsNullOrEmpty(name))
                        _appNames[appId] = name;
                    else if (!_appNames.ContainsKey(appId))
                        _appNames[appId] = $"App {appId}";
                    if (!_ownedAppIds.Contains(appId))
                    {
                        _ownedAppIds.Add(appId);
                        added++;
                    }
                }
            }

            Log($"GetOwnedGames: {added} new apps");
            var hasAny = _ownedAppIds.Count > 0;
            if (hasAny) _webApiOk = true;
            return Task.FromResult(added >= 0 && hasAny);
        }
        catch (Exception ex)
        {
            Log($"ParseOwnedGames failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    // ── License list → merge PICS data (fallback/augment) ──────────

    private void OnLicenseList(SteamApps.LicenseListCallback callback)
    {
        if (_retired) return;
        Log($"LicenseList received: {callback.LicenseList.Count} licenses, result={callback.Result}");
        UiInvoke(() => OnStatusUpdate?.Invoke($"License list received: {callback.LicenseList.Count} licenses"));
        _ = ProcessLicenseList(callback).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                Log($"License processing failed: {t.Exception.InnerException?.Message}");
                UiInvoke(() => OnError?.Invoke($"License processing failed: {t.Exception.InnerException?.Message}"));
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ProcessLicenseList(SteamApps.LicenseListCallback callback)
    {
        var licenses = callback.LicenseList.Where(l => l.PackageID != 0).ToList();
        Log($"Processing {licenses.Count} licenses with non-zero PackageID");

        var packageIds = licenses.Select(l => l.PackageID).Distinct().ToList();
        UiInvoke(() => OnStatusUpdate?.Invoke($"Querying Steam for {packageIds.Count} packages..."));

        var tokenResult = await _steamApps!.PICSGetAccessTokens(Array.Empty<uint>(), packageIds);
        Log($"PICSGetAccessTokens result: {tokenResult.PackageTokens.Count} tokens");

        var allAppIds = new HashSet<uint>();

        var pkgRequests = packageIds.Select(pkgId =>
        {
            tokenResult.PackageTokens.TryGetValue(pkgId, out var token);
            return new SteamApps.PICSRequest(pkgId, token);
        }).ToList();

        foreach (var batch in pkgRequests.Chunk(30))
        {
            try
            {
                var result = await _steamApps.PICSGetProductInfo(Array.Empty<SteamApps.PICSRequest>(), batch);

                if (result.Results != null)
                {
                    foreach (var cb in result.Results)
                    {
                        if (cb.Packages != null)
                        {
                            foreach (var kv in cb.Packages)
                            {
                                try { ExtractAppIdsFromKV(kv.Value.KeyValues, allAppIds); }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Package batch error: {ex.Message}");
            }
        }

        Log($"Total apps from packages: {allAppIds.Count}");
        UiInvoke(() => OnStatusUpdate?.Invoke($"Found {allAppIds.Count} apps. Fetching app info..."));

        List<uint> newAppIds;
        lock (_sync) newAppIds = allAppIds.Where(id => !_ownedAppIds.Contains(id)).ToList();
        if (newAppIds.Count == 0 && allAppIds.Count > 0)
        {
            lock (_sync) newAppIds = [.. allAppIds];
        }

        var appTokenResult = await _steamApps.PICSGetAccessTokens(newAppIds, Array.Empty<uint>());
        Log($"App tokens: {appTokenResult.AppTokens.Count}");

        foreach (var batch in newAppIds.Chunk(30))
        {
            var requests = batch.Select(id =>
            {
                appTokenResult.AppTokens.TryGetValue(id, out var token);
                return new SteamApps.PICSRequest(id, token);
            }).ToList();

            try
            {
                var result = await _steamApps.PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>());

                if (result.Results != null)
                {
                    foreach (var cb in result.Results)
                    {
                        if (cb.Apps != null)
                        {
                            foreach (var appInfo in cb.Apps.Values)
                                MergeAppInfo(appInfo.ID, appInfo.KeyValues, !_webApiOk);
                        }
                    }
                }
            }
            catch { }
        }

        await EnsureAppDepotsAsync();
        await ClassifySharedDepotsAsync();

        int count;
        lock (_sync) count = _ownedAppIds.Count;
        Log($"Final: {count} owned apps");
        UiInvoke(() => OnStatusUpdate?.Invoke($"Done! {count} owned apps from Steam"));
        UiInvoke(() => OnOwnedGamesLoaded?.Invoke(count));
    }

    private void MergeAppInfo(uint appId, KeyValue keyValues, bool allowNewIds = true)
    {
        try
        {
            string? appName = null;
            List<SteamDepot>? depots = null;

            for (int i = 0; i < keyValues.Children.Count; i++)
            {
                var child = keyValues.Children[i];
                if (child.Name == "common")
                {
                    for (int j = 0; j < child.Children.Count; j++)
                    {
                        if (child.Children[j].Name == "name")
                            appName = child.Children[j].Value;
                    }
                }
                else if (child.Name == "depots")
                {
                    depots = [];
                    for (int j = 0; j < child.Children.Count; j++)
                    {
                        var depotNode = child.Children[j];
                        if (uint.TryParse(depotNode.Name, out var depotId))
                        {
                            string depotName = $"Depot {depotId}";
                            uint parentApp = appId;
                            uint dlcApp = 0;
                            bool sharedInstall = false;
                            string manifestId = "";
                            ulong manifestSize = 0;
                            for (int k = 0; k < depotNode.Children.Count; k++)
                            {
                                var field = depotNode.Children[k];
                                if (field.Name == "name")
                                    depotName = field.Value ?? depotName;
                                else if (field.Name == "depotfromapp"
                                    && uint.TryParse(field.Value, out var parent))
                                    parentApp = parent;
                                else if (field.Name == "dlcappid"
                                    && uint.TryParse(field.Value, out var dlc)
                                    && dlc != 0 && dlc != appId)
                                    dlcApp = dlc;
                                else if (field.Name == "sharedinstall"
                                    && field.Value == "1")
                                    sharedInstall = true;
                                else if (field.Name == "manifests")
                                {
                                    // manifests -> <branch, usually "public"> -> gid + size
                                    for (int m = 0; m < field.Children.Count; m++)
                                    {
                                        var branch = field.Children[m];
                                        bool isPublic = string.Equals(branch.Name, "public", StringComparison.OrdinalIgnoreCase);
                                        string? gid = null;
                                        ulong size = 0;
                                        for (int n = 0; n < branch.Children.Count; n++)
                                        {
                                            if (branch.Children[n].Name == "gid")
                                                gid = branch.Children[n].Value;
                                            else if (branch.Children[n].Name == "size"
                                                && ulong.TryParse(branch.Children[n].Value, out var s))
                                                size = s;
                                        }
                                        if (!string.IsNullOrEmpty(gid) && (isPublic || string.IsNullOrEmpty(manifestId)))
                                        {
                                            manifestId = gid;
                                            manifestSize = size;
                                            if (isPublic) break;
                                        }
                                    }
                                }
                            }
                            // A dlcappid-gated depot is licensed through the DLC app, not the
                            // base game: requesting its key with the game id is always denied.
                            // Route ownership (and the key request) to the DLC instead.
                            if (dlcApp != 0 && parentApp == appId)
                                parentApp = dlcApp;
                            depots.Add(new SteamDepot
                            {
                                DepotId = depotId,
                                Name = depotName,
                                ParentAppId = parentApp,
                                IsShared = parentApp != appId,
                                SharedFrom = parentApp != appId ? parentApp.ToString() : "",
                                IsRedistributable = sharedInstall,
                                ManifestId = manifestId,
                                ManifestSize = manifestSize,
                            });
                        }
                    }
                }
                else if (child.Name == "extended")
                {
                    for (int j = 0; j < child.Children.Count; j++)
                    {
                        if (child.Children[j].Name == "listofdlc"
                            && !string.IsNullOrEmpty(child.Children[j].Value))
                        {
                            var dlcIds = child.Children[j].Value!
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Where(s => uint.TryParse(s, out _))
                                .Select(uint.Parse)
                                .Distinct()
                                .ToList();
                            lock (_sync) _appDlcs[appId] = dlcIds;
                        }
                    }
                }
            }

            lock (_sync)
            {
                if (!string.IsNullOrEmpty(appName))
                    _appNames[appId] = appName;
                else if (!_appNames.ContainsKey(appId))
                    _appNames[appId] = $"App {appId}";
                // When the WebAPI already gave us the authoritative owned list,
                // PICS only enriches (names/depots) — it must not add expired
                // free-weekend etc. apps as new owned games.
                if (allowNewIds || _ownedAppIds.Contains(appId))
                {
                    if (!_ownedAppIds.Contains(appId))
                        _ownedAppIds.Add(appId);
                }
                if (depots is { Count: > 0 })
                    _appDepots[appId] = depots;
            }
        }
        catch { }
    }

    /// <summary>
    /// Makes sure every owned app has a depot list. Apps that only came
    /// from the WebAPI (no PICS data yet) get their appinfo queried so we
    /// learn their depot IDs + names. Merge-only.
    /// </summary>
    private async Task EnsureAppDepotsAsync()
    {
        if (_steamApps == null) return;

        List<uint> lacking;
        lock (_sync) lacking = _ownedAppIds.Where(id => !_appDepots.ContainsKey(id)).ToList();
        if (lacking.Count == 0) return;

        UiInvoke(() => OnStatusUpdate?.Invoke($"Resolving depots for {lacking.Count} apps..."));
        Log($"EnsureAppDepots: {lacking.Count} apps lack depot lists");

        var tokenResult = await _steamApps.PICSGetAccessTokens(lacking, Array.Empty<uint>());

        foreach (var batch in lacking.Chunk(30))
        {
            var requests = batch.Select(id =>
            {
                tokenResult.AppTokens.TryGetValue(id, out var token);
                return new SteamApps.PICSRequest(id, token);
            }).ToList();

            try
            {
                var result = await _steamApps.PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>());
                if (result.Results != null)
                {
                    foreach (var cb in result.Results)
                    {
                        if (cb.Apps != null)
                        {
                            foreach (var appInfo in cb.Apps.Values)
                                MergeAppInfo(appInfo.ID, appInfo.KeyValues, !_webApiOk);
                        }
                    }
                }
            }
            catch { }
        }

        int withDepots;
        lock (_sync) withDepots = _ownedAppIds.Count(id => _appDepots.ContainsKey(id));
        Log($"EnsureAppDepots done: {withDepots}/{_ownedAppIds.Count} owned apps have depot lists");
    }

    /// <summary>
    /// Marks shared depots as redistributable using Steam's own data only:
    /// depots shared from an app whose PICS common/type is Tool/Application
    /// (e.g. the Steamworks Common Redistributables). No name matching.
    /// </summary>
    private async Task ClassifySharedDepotsAsync()
    {
        if (_steamApps == null) return;

        Dictionary<uint, List<uint>> parents = new();
        lock (_sync)
        {
            foreach (var kv in _appDepots)
            {
                foreach (var depot in kv.Value)
                {
                    if (depot.IsShared && depot.ParentAppId != 0 && depot.ParentAppId != kv.Key)
                    {
                        if (!parents.TryGetValue(depot.ParentAppId, out var list))
                            parents[depot.ParentAppId] = list = [];
                        list.Add(depot.DepotId);
                    }
                }
            }
        }

        if (parents.Count == 0) return;

        var parentIds = parents.Keys.ToList();
        UiInvoke(() => OnStatusUpdate?.Invoke($"Classifying shared depots from {parentIds.Count} apps..."));

        var redistParents = new HashSet<uint>();
        foreach (var batch in parentIds.Chunk(30))
        {
            var requests = batch.Select(id => new SteamApps.PICSRequest(id)).ToList();
            try
            {
                var result = await _steamApps.PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>());
                if (result.Results == null) continue;

                foreach (var cb in result.Results)
                {
                    if (cb.Apps == null) continue;
                    foreach (var appInfo in cb.Apps.Values)
                    {
                        string? type = null;
                        string? name = null;
                        var kvp = appInfo.KeyValues;
                        for (int i = 0; i < kvp.Children.Count; i++)
                        {
                            if (kvp.Children[i].Name != "common") continue;
                            var common = kvp.Children[i];
                            for (int j = 0; j < common.Children.Count; j++)
                            {
                                if (common.Children[j].Name == "type")
                                    type = common.Children[j].Value;
                                else if (common.Children[j].Name == "name")
                                    name = common.Children[j].Value;
                            }
                        }

                        if (type is not null && (type.Equals("tool", StringComparison.OrdinalIgnoreCase)
                            || type.Equals("application", StringComparison.OrdinalIgnoreCase)))
                        {
                            redistParents.Add(appInfo.ID);
                            Log($"Redist parent: app={appInfo.ID} type={type} name={name}");
                        }
                    }
                }
            }
            catch { }
        }

        if (redistParents.Count == 0) return;

        lock (_sync)
        {
            foreach (var kv in _appDepots)
            {
                foreach (var depot in kv.Value)
                {
                    if (redistParents.Contains(depot.ParentAppId))
                        depot.IsRedistributable = true;
                }
            }
        }
        Log($"Classified redistributable depots from {redistParents.Count} parent apps");
    }

    // ── KV helpers ──────────────────────────────────────────────

    private static void ExtractAppIdsFromKV(KeyValue keyValues, HashSet<uint> allAppIds)
    {
        for (int i = 0; i < keyValues.Children.Count; i++)
        {
            var child = keyValues.Children[i];
            if (child.Name == "appids" || child.Name == "apps")
            {
                for (int j = 0; j < child.Children.Count; j++)
                {
                    if (uint.TryParse(child.Children[j].Name, out var appId))
                        allAppIds.Add(appId);
                    else if (uint.TryParse(child.Children[j].Value, out appId))
                        allAppIds.Add(appId);
                }
            }
        }
    }

    // ── Parsing helpers ───────────────────────────────────────────

    private List<string> ParseLibraryFolders(string path)
    {
        var paths = new List<string>();
        if (_steamInstallPath != null) paths.Add(_steamInstallPath);

        var content = File.ReadAllText(path);
        foreach (Match m in LibraryPathRegex().Matches(content))
        {
            var p = m.Groups[1].Value.Replace("\\\\", "\\");
            if (!paths.Contains(p) && Directory.Exists(p)) paths.Add(p);
        }
        return paths;
    }

    private SteamGame? ParseAppManifest(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var appIdMatch = AppIdRegex().Match(content);
        var nameMatch = NameRegex().Match(content);
        if (!appIdMatch.Success || !nameMatch.Success) return null;
        if (!uint.TryParse(appIdMatch.Groups[1].Value, out var appId)) return null;

        var game = new SteamGame
        {
            AppId = appId,
            Name = nameMatch.Groups[1].Value,
            BaseDepotKey = GetDepotKey(appId) ?? ""
        };
        lock (_sync)
        {
            if (_luaAppTokens.TryGetValue(appId, out var appToken))
                game.Token = appToken;
        }
        var sharedParents = ParseSharedDepots(content);
        var manifests = ParseInstalledDepotManifests(content);

        foreach (Match m in DepotIdRegex().Matches(content))
        {
            if (uint.TryParse(m.Groups[1].Value, out var depotId))
            {
                sharedParents.TryGetValue(depotId, out var parent);
                parent = parent != 0 ? parent : appId;
                manifests.TryGetValue(depotId, out var manifest);
                game.Depots.Add(new SteamDepot
                {
                    DepotId = depotId,
                    Name = ResolveDepotName(depotId, $"Depot {depotId}"),
                    ParentAppId = parent,
                    IsShared = parent != appId,
                    SharedFrom = parent != appId ? parent.ToString() : "",
                    ManifestId = manifest.manifest ?? "",
                    ManifestSize = manifest.size,
                });
            }
        }
        return game;
    }

    /// <summary>
    /// Reads InstalledDepots manifest gid + size per depot from an appmanifest
    /// ("depotid" { "manifest" "gid" "size" "bytes" }). Used for setManifestid lines.
    /// </summary>
    private static Dictionary<uint, (string manifest, ulong size)> ParseInstalledDepotManifests(string content)
    {
        var map = new Dictionary<uint, (string, ulong)>();
        var section = ExtractVdfSection(content, "InstalledDepots");
        if (section == null) return map;

        foreach (Match m in Regex.Matches(section, @"""(\d+)""\s*\{([^}]*)\}"))
        {
            if (!uint.TryParse(m.Groups[1].Value, out var depotId)) continue;
            var body = m.Groups[2].Value;
            var gid = Regex.Match(body, @"""manifest""\s+""(\d+)""");
            if (!gid.Success) continue;
            var size = Regex.Match(body, @"""size""\s+""(\d+)""");
            ulong.TryParse(size.Success ? size.Groups[1].Value : "0", out var bytes);
            map[depotId] = (gid.Groups[1].Value, bytes);
        }
        return map;
    }

    /// <summary>
    /// Reads the manifest's SharedDepots map ("depotid" "owningappid").
    /// </summary>
    private static Dictionary<uint, uint> ParseSharedDepots(string content)
    {
        var map = new Dictionary<uint, uint>();
        var section = ExtractVdfSection(content, "SharedDepots");
        if (section == null) return map;

        foreach (Match m in Regex.Matches(section, @"""(\d+)""\s+""(\d+)"""))
        {
            if (uint.TryParse(m.Groups[1].Value, out var depotId) &&
                uint.TryParse(m.Groups[2].Value, out var parent))
                map[depotId] = parent;
        }
        return map;
    }

    // ── Regex ─────────────────────────────────────────────────────

    [GeneratedRegex(@"""(\d{17})""\s*\r?\n\s*\{\s*\r?\n((?:\s*""[^\r\n""]+""\s+""[^\r\n""]*""\s*\r?\n)+)", RegexOptions.Compiled)]
    private static partial Regex LocalUserBlockRegex();
    [GeneratedRegex(@"""([^""]+)""\s+""([^""]*)""", RegexOptions.Compiled)]
    private static partial Regex LocalUserFieldRegex();
    [GeneratedRegex(@"""([^""]+)""\s*\{\s*""SteamID""\s+""(\d{17})""\s*\}", RegexOptions.Compiled)]
    private static partial Regex AccountsEntryRegex();
    [GeneratedRegex(@"""path""\s+""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex LibraryPathRegex();
    [GeneratedRegex(@"""appid""\s+""(\d+)""", RegexOptions.Compiled)]
    private static partial Regex AppIdRegex();
    [GeneratedRegex(@"""name""\s+""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex NameRegex();
    [GeneratedRegex(@"^[\t ]*""(\d+)""\s*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex DepotIdRegex();
    [GeneratedRegex(@"addappid\((\d+),\s*1,\s*""([^""]+)""\)(?:\s*--\s*(.*))?", RegexOptions.Compiled)]
    private static partial Regex LuaAppIdRegex();
    [GeneratedRegex(@"addtoken\((\d+),\s*""?(\d+)""?\)", RegexOptions.Compiled)]
    private static partial Regex LuaTokenRegex();
    [GeneratedRegex(@"""(\d+)""\s*\{[^}]*?""DecryptionKey""\s*""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex DepotKeyEntryRegex();
}
