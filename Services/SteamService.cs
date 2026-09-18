using LuaShareX.Models;

namespace LuaShareX.Services;

/// <summary>Owns the selected account. Each login gets isolated clients, caches and callbacks.</summary>
public sealed class SteamService
{
    private readonly ToastService _toast;
    private readonly SteamTokenStore _tokens = new();
    private SteamSession _session;

    public SteamService(ToastService toast)
    {
        _toast = toast;
        _session = CreateSession();
    }

    public string? SteamInstallPath => _session.SteamInstallPath;
    public string? CurrentSteamId => _session.CurrentSteamId;
    public string? CurrentAccountName => _session.AccountName;
    public bool IsSteamRunning => _session.IsSteamRunning;
    public bool IsSteamKitConnected => _session.IsSteamKitConnected;
    public bool IsAccountSession { get; private set; }
    public string? AutoLoginUsername => _session.AutoLoginUsername;
    public SteamLocalUser? ActiveLocalUser => LocalUsers.FirstOrDefault(a =>
        string.Equals(a.AccountName, CurrentAccountName ?? _tokens.LastAccount ?? AutoLoginUsername, StringComparison.OrdinalIgnoreCase))
        ?? _session.ActiveLocalUser;
    public int DepotKeyCount => _session.DepotKeyCount;
    public int OwnedAppCount => _session.OwnedAppCount;
    public List<SteamLocalUser> LocalUsers => _session.LocalUsers.Concat(_tokens.Accounts)
        .DistinctBy(a => a.AccountName, StringComparer.OrdinalIgnoreCase).ToList();

    public event Action? OnSteamKitConnected;
    public event Action? OnSteamKitDisconnected;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnLoginNeeded;
    public event Action<string>? OnQRCodeReady;
    public event Action<string>? OnGuardCodeNeeded;
    public event Action<int>? OnOwnedGamesLoaded;

    private SteamSession CreateSession()
    {
        var session = new SteamSession(_toast, _tokens);
        session.OnSteamKitConnected += () => { if (session == _session) OnSteamKitConnected?.Invoke(); };
        session.OnSteamKitDisconnected += () => { if (session == _session) OnSteamKitDisconnected?.Invoke(); };
        session.OnError += message => { if (session == _session) OnError?.Invoke(message); };
        session.OnStatusUpdate += message => { if (session == _session) OnStatusUpdate?.Invoke(message); };
        session.OnLoginNeeded += message => { if (session == _session) OnLoginNeeded?.Invoke(message); };
        session.OnQRCodeReady += url => { if (session == _session) OnQRCodeReady?.Invoke(url); };
        session.OnGuardCodeNeeded += message => { if (session == _session) OnGuardCodeNeeded?.Invoke(message); };
        session.OnOwnedGamesLoaded += count => { if (session == _session) OnOwnedGamesLoaded?.Invoke(count); };
        return session;
    }

    private void ReplaceSession()
    {
        _session.Disconnect();
        _session = CreateSession();
        _session.DetectSteam();
    }

    public void DetectSteam()
    {
        _session.DetectSteam();
        _tokens.MigrateLegacy(_session.LocalUsers);
    }

    public void RefreshAccounts() => _session.RefreshLocalAccounts();

    public Task StartSteamKitAuth(string? username = null, string? password = null)
    {
        ReplaceSession();
        IsAccountSession = true;
        return _session.StartSteamKitAuth(username, password);
    }

    public Task StartSteamKitLocalLogin(SteamLocalUser account)
    {
        ReplaceSession();
        IsAccountSession = true;
        // Resolve by account name from a fresh list rather than using stale UI metadata.
        var selected = LocalUsers.FirstOrDefault(a => string.Equals(a.AccountName, account.AccountName, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            OnLoginNeeded?.Invoke("Account not found. Refresh the accounts or sign in with QR/password.");
            return Task.CompletedTask;
        }
        return _session.StartSteamKitLocalLogin(selected);
    }

    public Task StartSteamKitQRLogin()
    {
        ReplaceSession();
        IsAccountSession = true;
        return _session.StartSteamKitQRLogin();
    }

    public void CancelQRLogin() => Disconnect();
    public void SubmitGuardCode(string code) => _session.SubmitGuardCode(code);
    public void Disconnect()
    {
        ReplaceSession();
        IsAccountSession = false;
    }
    public void Shutdown() => _session.Disconnect();

    public void Logout()
    {
        var name = _session.AccountName;
        Disconnect();
        if (name != null)
        {
            try { _tokens.Remove(name); }
            catch { OnError?.Invoke("Signed out, but the saved login could not be removed."); }
        }
    }

    public string? GetDepotKey(uint id) => _session.GetDepotKey(id);
    public List<SteamGame> GetInstalledGames() => _session.GetInstalledGames();
    public List<SteamGame> GetOwnedGames() => _session.GetOwnedGames();
    public List<SteamGame> GetLicensedGames() => _session.GetLicensedGames();
    public List<SteamGame> GetAllGames() => _session.GetAllGames();

    public async Task<(int keysOk, int keysFail, int tokensOk)> EnsureExportDataAsync(List<SteamGame> games)
    {
        var session = _session;
        if (IsAccountSession && !session.IsSteamKitConnected)
            throw new InvalidOperationException("Sign in before exporting this account's games.");
        var result = await session.EnsureExportDataAsync(games);
        if (session != _session) throw new OperationCanceledException("The account changed. Select games from the current account and try again.");
        return result;
    }

    /// <summary>Auto-downloads .manifest files for the given games into folder.</summary>
    public Task<(int ok, int skipped, int fail)> DownloadManifestsAsync(
        List<SteamGame> games, string folder, IProgress<double>? progress, CancellationToken ct = default)
    {
        var session = _session;
        return session.DownloadManifestsAsync(games, folder, progress, ct);
    }
}
