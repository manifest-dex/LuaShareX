using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LuaShareX.Models;
using LuaShareX.Services;

namespace LuaShareX.ViewModels;

/// <summary>
/// Element-style selectable game card: cover image resolves lazily from the
/// disk-cached Steam header art, selection drives the export count.
/// </summary>
public partial class GameTileViewModel : ObservableObject
{
    public SteamGame Game { get; }
    public uint AppId => Game.AppId;
    public string Name => Game.Name;
    public string Initial => string.IsNullOrEmpty(Game.Name) ? "?" : Game.Name[..1].ToUpperInvariant();

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private string _depotInfo = "No depot info yet";

    private bool _coverLoading;
    internal Action<GameTileViewModel>? SelectionChanged;

    public GameTileViewModel(SteamGame game)
    {
        Game = game;
        _isSelected = game.IsSelected;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        Game.IsSelected = value;
        SelectionChanged?.Invoke(this);
    }

    public void RefreshFromGame()
    {
        var total = Game.Depots.Count;
        var withKeys = Game.Depots.Count(d => !string.IsNullOrEmpty(d.DepotKey));
        DepotInfo = total == 0
            ? "No depot info yet"
            : $"{total} depot{(total == 1 ? "" : "s")}, {withKeys} key{(withKeys == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
    }

    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover != null || _coverLoading) return;
        _coverLoading = true;

        try
        {
            var local = covers.GetLocalPath(AppId);
            if (local == null && !covers.IsKnownMissing(AppId))
                local = await covers.EnsureAsync(AppId);
            if (local == null)
            {
                covers.MarkMissing(AppId);
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(local, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            if (Application.Current?.Dispatcher != null)
                await Application.Current.Dispatcher.InvokeAsync(() => Cover = image);
            else
                Cover = image;
        }
        catch
        {
            covers.MarkMissing(AppId);
        }
    }
}
