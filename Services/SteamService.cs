using System.IO;
using System.Collections.Generic;
using SteamKit2;
using LuaShareX.Models;

namespace LuaShareX.Services;

public class SteamService
{
    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamApps? _steamApps;

    public bool IsLogged_in { get; private set; }
    public string Username { get; private set; } = "";
    public ulong SteamId { get; private set; }

    public event Action? OnLogged_in;
    public event Action<string>? OnError;
    public event Action? OnDisconnected;

    public async Task StartLogin(string username, string password, string authCode = "")
    {
        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>();
        _steamApps = _steamClient.GetHandler<SteamApps>();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnectedCallback);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        _steamClient.Connect();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                await Task.Delay(100);
            }
        });

        await Task.Delay(2000);

        _steamUser?.LogOn(new SteamUser.LogOnDetails
        {
            Username = username,
            Password = password,
            AuthCode = authCode,
            ShouldRememberPassword = true,
        });
    }

    public async Task<List<SteamGame>> GetOwnedGames()
    {
        var games = new List<SteamGame>();
        if (_steamApps is null || !IsLogged_in) return games;

        try
        {
                    var response = await _steamApps.PICSGetProductInfo(new SteamApps.PICSRequest(), new SteamApps.PICSRequest(), metaDataOnly: true);
            foreach (var callback in response.Results!)
            {
                if (callback.Apps is null) continue;

                foreach (var kvp in callback.Apps)
                {
                    var appInfo = kvp.Value;
                    if (appInfo.KeyValues is null) continue;

                    var name = appInfo.KeyValues["common"]["name"].AsString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var game = new SteamGame
                    {
                        AppId = appInfo.ID,
                        Name = name
                    };

                    var depots = appInfo.KeyValues["depots"];
                    foreach (var depotChild in depots.Children)
                    {
                        if (depotChild.Name == "branches") continue;
                        if (uint.TryParse(depotChild.Name, out var depotId))
                        {
                            var depotName = depotChild["name"].AsString() ?? $"Depot {depotId}";
                            game.Depots.Add(new SteamDepot
                            {
                                DepotId = depotId,
                                Name = depotName
                            });
                        }
                    }

                    var dlcNode = appInfo.KeyValues["common"]["dlc"];
                    foreach (var dlcChild in dlcNode.Children)
                    {
                        if (uint.TryParse(dlcChild.Name, out var dlcId))
                        {
                            game.Dlcs.Add(new SteamDlc
                            {
                                AppId = dlcId,
                                Name = $"DLC {dlcId}"
                            });
                        }
                    }

                    games.Add(game);
                }
            }
        }
        catch { }

        return games;
    }

    public async Task<SteamGame?> GetGameDetails(uint appId)
    {
        if (_steamApps is null) return null;

        try
        {
            var response = await _steamApps.PICSGetProductInfo(new SteamApps.PICSRequest(appId), null);
            foreach (var callback in response.Results!)
            {
                if (callback.Apps is null || callback.Apps.Count == 0) continue;

                var appInfo = callback.Apps.Values.First();
                if (appInfo.KeyValues is null) continue;

                var name = appInfo.KeyValues["common"]["name"].AsString();
                if (string.IsNullOrEmpty(name)) continue;

                var game = new SteamGame
                {
                    AppId = appId,
                    Name = name
                };

                var depots = appInfo.KeyValues["depots"];
                foreach (var depotChild in depots.Children)
                {
                    if (depotChild.Name == "branches") continue;
                    if (uint.TryParse(depotChild.Name, out var depotId))
                    {
                        var depotName = depotChild["name"].AsString() ?? $"Depot {depotId}";
                        game.Depots.Add(new SteamDepot
                        {
                            DepotId = depotId,
                            Name = depotName
                        });
                    }
                }

                return game;
            }
        }
        catch { }

        return null;
    }

    public void Disconnect()
    {
        _steamClient?.Disconnect();
        IsLogged_in = false;
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
    }

    private void OnDisconnectedCallback(SteamClient.DisconnectedCallback callback)
    {
        IsLogged_in = false;
        OnDisconnected?.Invoke();
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        switch (callback.Result)
        {
            case EResult.OK:
                IsLogged_in = true;
                SteamId = callback.ClientSteamID?.ConvertToUInt64() ?? 0;
                Username = callback.ClientSteamID?.ConvertToUInt64().ToString() ?? "";
                OnLogged_in?.Invoke();
                break;
            case EResult.AccountLogonDenied:
                OnError?.Invoke("Steam Guard code required. Check your email.");
                break;
            case EResult.TwoFactorCodeMismatch:
            case EResult.TwoFactorActivationCodeMismatch:
                OnError?.Invoke("Invalid two-factor code.");
                break;
            case EResult.InvalidPassword:
                OnError?.Invoke("Invalid password.");
                break;
            default:
                OnError?.Invoke($"Login failed: {callback.Result}");
                break;
        }
    }
}
