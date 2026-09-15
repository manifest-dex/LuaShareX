using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaShareX.Services;

namespace LuaShareX.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly SteamService _steam;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _authCode = "";
    [ObservableProperty] private bool _isLogging;
    [ObservableProperty] private string _statusMessage = "Enter your Steam credentials to login";

    public event Action? OnLoginSuccess;
    public event Action<string>? OnError;

    public LoginViewModel(SteamService steam)
    {
        _steam = steam;
        _steam.OnLogged_in += () =>
        {
            IsLogging = false;
            OnLoginSuccess?.Invoke();
        };
        _steam.OnError += (err) =>
        {
            IsLogging = false;
            StatusMessage = err;
            OnError?.Invoke(err);
        };
    }

    [RelayCommand]
    private async Task Login()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Please enter username and password";
            return;
        }

        IsLogging = true;
        StatusMessage = "Connecting to Steam...";
        await _steam.StartLogin(Username, Password, AuthCode);
    }

    [RelayCommand]
    private void LoginAnonymous()
    {
        IsLogging = false;
        OnLoginSuccess?.Invoke();
    }
}
