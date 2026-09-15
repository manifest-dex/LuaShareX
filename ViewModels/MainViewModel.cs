using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaShareX.Models;
using LuaShareX.Services;

namespace LuaShareX.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SteamService _steam;
    private readonly LuaExportService _export;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string _statusMessage = "Detecting Steam...";
    [ObservableProperty] private bool _isLoaded;

    public GamesViewModel GamesVm { get; }

    public MainViewModel(SteamService steam, LuaExportService export)
    {
        _steam = steam;
        _export = export;

        GamesVm = new GamesViewModel(steam, export);

        _steam.OnLoaded += () =>
        {
            IsLoaded = true;
            StatusMessage = $"Loaded {GamesVm.Games.Count} installed games";
        };
        _steam.OnError += (err) =>
        {
            StatusMessage = err;
        };

        CurrentView = GamesVm;

        // Auto-detect and load
        _ = AutoLoad();
    }

    private async Task AutoLoad()
    {
        await Task.Delay(100); // Let UI initialize

        _steam.DetectSteam();

        if (_steam.SteamInstallPath == null)
        {
            StatusMessage = "Steam not found. Please install Steam.";
            return;
        }

        StatusMessage = "Loading installed games...";
        var games = _steam.GetInstalledGames();

        GamesVm.LoadGamesFromList(games);

        if (games.Count > 0)
            StatusMessage = $"Loaded {games.Count} installed games from Steam";
        else
            StatusMessage = "No installed games found";
    }
}
