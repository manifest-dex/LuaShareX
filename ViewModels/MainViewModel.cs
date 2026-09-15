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
    [ObservableProperty] private string _statusMessage = "Welcome to LuaShareX";
    [ObservableProperty] private bool _isLoggedIn;

    public LoginViewModel LoginVm { get; }
    public GamesViewModel GamesVm { get; }

    public MainViewModel(SteamService steam, LuaExportService export)
    {
        _steam = steam;
        _export = export;

        LoginVm = new LoginViewModel(steam);
        GamesVm = new GamesViewModel(steam, export);

        LoginVm.OnLoginSuccess += OnLoginSuccess;
        LoginVm.OnError += OnError;

        CurrentView = LoginVm;
    }

    private void OnLoginSuccess()
    {
        IsLoggedIn = true;
        StatusMessage = $"Logged in as {_steam.Username}";
        CurrentView = GamesVm;
        GamesVm.LoadGames();
    }

    private void OnError(string error)
    {
        StatusMessage = error;
    }

    [RelayCommand]
    private void Logout()
    {
        _steam.Disconnect();
        IsLoggedIn = false;
        StatusMessage = "Logged out";
        CurrentView = LoginVm;
    }

    [RelayCommand]
    private void NavigateToLogin()
    {
        CurrentView = LoginVm;
    }

    [RelayCommand]
    private void NavigateToGames()
    {
        if (IsLoggedIn)
            CurrentView = GamesVm;
    }
}
