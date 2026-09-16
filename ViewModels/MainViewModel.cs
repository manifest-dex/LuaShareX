using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaShareX.Models;
using LuaShareX.Services;
using QRCoder;

namespace LuaShareX.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SteamService _steam;
    private readonly ToastService _toast;
    private readonly UpdateService _updates;
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string _statusMessage = "Detecting Steam...";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _useSteamKit = true;
    [ObservableProperty] private bool _showLoginPrompt;
    [ObservableProperty] private string _loginUsername = "";
    [ObservableProperty] private string _loginPassword = "";
    [ObservableProperty] private string _loginStatus = "";

    [ObservableProperty] private bool _showQrLogin;
    [ObservableProperty] private ImageSource? _qrImage;
    [ObservableProperty] private string _qrUrl = "";

    [ObservableProperty] private bool _showGuardPrompt;
    [ObservableProperty] private string _guardPrompt = "";
    [ObservableProperty] private string _guardCode = "";

    [ObservableProperty] private List<SteamLocalUser> _localUsers = [];
    [ObservableProperty] private SteamLocalUser? _selectedLocalUser;
    [ObservableProperty] private string _localCacheSummary = "";
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _updateButtonText = "Check for updates";
    [ObservableProperty] private bool _updateBusy;

    public GamesViewModel GamesVm { get; }

    public bool ContentVisible => !ShowLoginPrompt && !ShowQrLogin && !ShowGuardPrompt;

    partial void OnShowLoginPromptChanged(bool value) => OnPropertyChanged(nameof(ContentVisible));
    partial void OnShowQrLoginChanged(bool value) => OnPropertyChanged(nameof(ContentVisible));
    partial void OnShowGuardPromptChanged(bool value) => OnPropertyChanged(nameof(ContentVisible));

    partial void OnSelectedLocalUserChanged(SteamLocalUser? value)
    {
        if (value != null && !string.IsNullOrEmpty(value.AccountName))
            LoginUsername = value.AccountName;
        UpdateLocalCacheSummary();
    }

    private void UpdateLocalCacheSummary()
    {
        // Local mode is account-free (installed apps only, all accounts).
        LocalCacheSummary = $"{_steam.DepotKeyCount} depot keys";
    }

    public MainViewModel(SteamService steam, LuaExportService export, CoverCache covers, ToastService toast, UpdateService updates)
    {
        _steam = steam;
        _toast = toast;
        _updates = updates;

        GamesVm = new GamesViewModel(steam, export, covers, toast);

        _steam.OnSteamKitConnected += () =>
        {
            ShowLoginPrompt = false;
            ShowQrLogin = false;
            ShowGuardPrompt = false;
            IsLoggedIn = true;
            StatusMessage = "Connected to Steam. Loading your library...";
            toast.Show("Steam", "Signed in — loading your library...");
        };
        _steam.OnSteamKitDisconnected += () =>
        {
            IsLoggedIn = false;
            StatusMessage = "Disconnected from Steam";
            toast.Show("Steam", "Disconnected from Steam");
        };
        _steam.OnError += (err) =>
        {
            LoginStatus = err;
            toast.Show("Steam", err, error: true);
        };
        _steam.OnStatusUpdate += (msg) =>
        {
            StatusMessage = msg;
        };
        _steam.OnLoginNeeded += (msg) =>
        {
            LoginStatus = msg;
            ShowLoginPrompt = true;
        };
        _steam.OnQRCodeReady += (url) =>
        {
            QrUrl = url;
            QrImage = CreateQrImage(url);
            ShowLoginPrompt = false;
            ShowQrLogin = true;
            StatusMessage = "Scan the QR code with the Steam Mobile app.";
        };
        _steam.OnGuardCodeNeeded += (prompt) =>
        {
            GuardPrompt = prompt;
            GuardCode = "";
            ShowGuardPrompt = true;
        };
        _steam.OnOwnedGamesLoaded += (count) =>
        {
            LoadAllGames();
            StatusMessage = $"Loaded {count} owned apps from Steam";
            toast.Show("Library", $"Loaded {count} owned apps from Steam");
        };

        CurrentView = GamesVm;
        _ = AutoStart();
    }

    private static ImageSource? CreateQrImage(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            using var qr = new QRCode(data);
            using var bitmap = qr.GetGraphic(20);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private async Task AutoStart()
    {
        await Task.Delay(100);

        _steam.DetectSteam();

        LocalUsers = _steam.LocalUsers;
        SelectedLocalUser = _steam.ActiveLocalUser;
        UpdateLocalCacheSummary();

        if (_steam.SteamInstallPath == null)
        {
            StatusMessage = "Steam not found.";
            return;
        }

        if (UseSteamKit)
        {
            StatusMessage = "Connecting to Steam via SteamKit2...";
            await _steam.StartSteamKitAuth();
        }
        else
        {
            LoadInstalledGames();
        }

        _ = CheckForUpdatesSilentAsync();
    }

    private void LoadInstalledGames()
    {
        StatusMessage = "Loading installed games...";
        var games = _steam.GetInstalledGames();
        GamesVm.LoadGamesFromList(games);
        StatusMessage = $"Loaded {games.Count} games";
    }

    private void LoadAllGames()
    {
        var games = _steam.GetLicensedGames();
        GamesVm.LoadGamesFromList(games);
    }

    [RelayCommand]
    private async Task LoginWithSteamKit()
    {
        if (string.IsNullOrWhiteSpace(LoginUsername) || string.IsNullOrWhiteSpace(LoginPassword))
        {
            LoginStatus = "Enter username and password";
            return;
        }

        LoginStatus = "Connecting...";
        ShowLoginPrompt = false;
        await _steam.StartSteamKitAuth(LoginUsername, LoginPassword);
    }

    [RelayCommand]
    private async Task LoginWithQr()
    {
        ShowLoginPrompt = false;
        ShowQrLogin = true;
        QrImage = null;
        StatusMessage = "Connecting to Steam...";
        await _steam.StartSteamKitQRLogin();
    }

    [RelayCommand]
    private void CancelQrLogin()
    {
        _steam.CancelQRLogin();
        ShowQrLogin = false;
        ShowLoginPrompt = true;
    }

    [RelayCommand]
    private void SubmitGuardCode()
    {
        if (string.IsNullOrWhiteSpace(GuardCode))
            return;

        ShowGuardPrompt = false;
        _steam.SubmitGuardCode(GuardCode);
    }

    [RelayCommand]
    private async Task SwitchToSteamKit()
    {
        UseSteamKit = true;
        ShowLoginPrompt = false;
        ShowQrLogin = false;
        ShowGuardPrompt = false;
        StatusMessage = "Connecting to Steam via SteamKit2...";
        _toast.Show("Steam", "Connecting via SteamKit2...");
        // NOTE: no completion toast here — StartSteamKitAuth only initiates
        // Connect(); the outcome arrives later via OnSteamKitConnected /
        // OnLoginNeeded / OnError, which already toast + update status.
        await _steam.StartSteamKitAuth();
    }

    [RelayCommand]
    private void Logout()
    {
        _steam.Logout();
        IsLoggedIn = false;
        ShowQrLogin = false;
        ShowGuardPrompt = false;
        GamesVm.LoadGamesFromList([]);
        LoginStatus = "Signed out. Sign in again to load your library.";
        ShowLoginPrompt = true;
        StatusMessage = "Signed out.";
        _toast.Show("Steam", "Signed out.");
    }

    [RelayCommand]
    private void UseLocalMode()
    {
        ShowLoginPrompt = false;
        ShowQrLogin = false;
        ShowGuardPrompt = false;
        UseSteamKit = false;
        _steam.Disconnect();
        SelectedLocalUser ??= _steam.ActiveLocalUser;
        UpdateLocalCacheSummary();
        LoadInstalledGames();
        StatusMessage = $"Local mode — {GamesVm.Games.Count} installed games";
    }

    [RelayCommand]
    private async Task CheckOrInstallUpdate()
    {
        if (UpdateBusy) return;

        if (_pendingUpdate != null)
        {
            await DownloadAndInstallAsync(_pendingUpdate);
            return;
        }

        UpdateBusy = true;
        UpdateButtonText = "Checking…";
        try
        {
            var info = await _updates.CheckForUpdatesAsync();
            if (info == null)
            {
                UpdateButtonText = "Check for updates";
                _toast.Show("Updates", "You're up to date.");
            }
            else
            {
                _pendingUpdate = info;
                UpdateButtonText = $"Install {info.Version}";
                StatusMessage = $"Update available: v{info.Version}";
                _toast.Show("Updates", $"v{info.Version} available — click Install to update.");
            }
        }
        catch (Exception ex)
        {
            UpdateButtonText = "Check for updates";
            StatusMessage = $"Update check failed: {ex.Message}";
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    private async Task CheckForUpdatesSilentAsync()
    {
        try
        {
            var info = await _updates.CheckForUpdatesAsync();
            if (info != null)
            {
                _pendingUpdate = info;
                UpdateButtonText = $"Install {info.Version}";
                _toast.Show("Updates", $"v{info.Version} available — click Install to update.");
            }
        }
        catch
        {
            // Update checks must never disturb startup.
        }
    }

    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        UpdateBusy = true;
        try
        {
            var progress = new Progress<double>(p =>
                StatusMessage = $"Downloading v{info.Version}… {p:P0}");
            StatusMessage = $"Downloading v{info.Version}…";
            var zip = await _updates.DownloadAsync(info, progress);
            StatusMessage = "Restarting to install…";
            UpdateService.InstallAndRestart(zip);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            UpdateButtonText = "Check for updates";
            StatusMessage = $"Update failed: {ex.Message}";
            _toast.Show("Updates", $"Update failed: {ex.Message}", error: true);
        }
        finally
        {
            UpdateBusy = false;
        }
    }
}
