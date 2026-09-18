using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using LuaShareX.Models;
using LuaShareX.Services;

namespace LuaShareX.ViewModels;

public partial class GamesViewModel : ObservableObject
{
    private readonly SteamService _steam;
    private readonly LuaExportService _export;
    private readonly CoverCache _covers;
    private readonly ToastService _toast;

    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectAllText = "Select All";
    [ObservableProperty] private string _exportText = "Export .lua";
    [ObservableProperty] private bool _autoDownloadManifests = true;

    partial void OnSearchTextChanged(string value)
    {
        FilterGames();
    }

    private List<GameTileViewModel> _allTiles = [];

    public GamesViewModel(SteamService steam, LuaExportService export, CoverCache covers, ToastService toast)
    {
        _steam = steam;
        _export = export;
        _covers = covers;
        _toast = toast;
    }

    public void LoadGamesFromList(List<SteamGame> games)
    {
        _allTiles = games
            .OrderBy(g => g.Name)
            .Select(g =>
            {
                var tile = new GameTileViewModel(g);
                tile.SelectionChanged += _ => UpdateSelectedCount();
                tile.RefreshFromGame();
                return tile;
            })
            .ToList();

        Games.Clear();
        foreach (var tile in _allTiles)
            Games.Add(tile);

        UpdateSelectedCount();
        _ = PrefetchCoversAsync(_allTiles);
    }

    private async Task PrefetchCoversAsync(List<GameTileViewModel> tiles)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = tiles.Select(async tile =>
        {
            await gate.WaitAsync();
            try
            {
                await tile.EnsureCoverAsync(_covers);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void RefreshGames()
    {
        if (_steam.SteamInstallPath == null)
        {
            StatusMessage = "Steam not found";
            _toast.Show("Refresh", "Steam not found.", error: true);
            return;
        }

        StatusMessage = "Refreshing...";
        var games = _steam.IsAccountSession
            ? _steam.GetLicensedGames()
            : _steam.GetInstalledGames();
        LoadGamesFromList(games);
        StatusMessage = $"Loaded {games.Count} games";
        _toast.Show("Refresh", $"Loaded {games.Count} games.");
    }

    [RelayCommand]
    private void ToggleSelectGame(GameTileViewModel? tile)
    {
        if (tile == null) return;
        tile.IsSelected = !tile.IsSelected;
    }

    private void FilterGames()
    {
        Games.Clear();
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allTiles
            : _allTiles.Where(t => t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var tile in filtered.OrderBy(t => t.Name))
            Games.Add(tile);

        UpdateSelectedCount();
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var allSelected = Games.All(t => t.IsSelected);
        foreach (var tile in Games)
            tile.IsSelected = !allSelected;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void CopyAppId(GameTileViewModel? tile)
    {
        if (tile == null) return;
        Clipboard.SetText(tile.AppId.ToString());
        StatusMessage = $"Copied App ID {tile.AppId}";
        _toast.Show("Copy", $"Copied App ID {tile.AppId}.");
    }

    [RelayCommand]
    private void OpenStorePage(GameTileViewModel? tile)
    {
        if (tile == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://store.steampowered.com/app/{tile.AppId}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open store page: {ex.Message}";
        }
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = _allTiles.Count(t => t.IsSelected);
        SelectAllText = Games.Count > 0 && Games.All(t => t.IsSelected)
            ? "Unselect All"
            : "Select All";
        ExportText = SelectedCount > 1 ? "Export .zip" : "Export .lua";
    }

    [RelayCommand]
    private async Task ExportSelected()
    {
        var selected = Games.Where(t => t.IsSelected).Select(t => t.Game).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No games selected for export";
            _toast.Show("Export", "No games selected for export.", error: true);
            return;
        }

        IsLoading = true;
        StatusMessage = $"Fetching keys for {selected.Count} game(s)...";
        try
        {
            await _steam.EnsureExportDataAsync(selected);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Key fetch failed: {ex.Message}";
            _toast.Show("Export", $"Key fetch failed: {ex.Message}", error: true);
            return;
        }
        finally
        {
            IsLoading = false;
        }

        foreach (var tile in Games.Where(t => t.IsSelected))
            tile.RefreshFromGame();

        if (selected.Count == 1)
        {
            var content = _export.Export(selected[0]);
            var dialog = new SaveFileDialog
            {
                Filter = "Lua files (*.lua)|*.lua|All files (*.*)|*.*",
                DefaultExt = ".lua",
                FileName = $"{selected[0].AppId}.lua"
            };

            if (dialog.ShowDialog() == true)
            {
                _export.SaveToFile(content, dialog.FileName);
                StatusMessage = $"Exported {selected[0].Name} to {Path.GetFileName(dialog.FileName)}";
                _toast.Show("Export", $"Exported {selected[0].Name} to {Path.GetFileName(dialog.FileName)}.");
                await MaybeDownloadManifestsAsync(selected, dialog.FileName);
            }
            return;
        }

        var zipDialog = new SaveFileDialog
        {
            Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".zip",
            FileName = $"luasharex_export_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };

        if (zipDialog.ShowDialog() == true)
        {
            _export.SaveMultipleToZip(selected, zipDialog.FileName);
            StatusMessage = $"Exported {selected.Count} game(s) to {Path.GetFileName(zipDialog.FileName)}";
            _toast.Show("Export", $"Exported {selected.Count} game(s) to {Path.GetFileName(zipDialog.FileName)}.");
            await MaybeDownloadManifestsAsync(selected, zipDialog.FileName);
        }
    }

    /// <summary>Auto-downloads .manifest files next to a saved export when enabled.
    /// Never fails the export itself: lua/zip is already on disk by now.</summary>
    private async Task MaybeDownloadManifestsAsync(List<SteamGame> selected, string savedPath)
    {
        if (!AutoDownloadManifests) return;
        if (!selected.SelectMany(g => g.Depots).Any(d => !string.IsNullOrEmpty(d.ManifestId))) return;
        var folder = Path.GetDirectoryName(savedPath);
        if (string.IsNullOrEmpty(folder)) return;

        IsLoading = true;
        try
        {
            StatusMessage = "Downloading manifests...";
            var prog = new Progress<double>(p => StatusMessage = $"Downloading manifests... {p:P0}");
            var (ok, skipped, fail) = await _steam.DownloadManifestsAsync(selected, folder, prog);
            StatusMessage = $"Manifests: {ok} downloaded, {skipped} skipped{(fail > 0 ? $", {fail} failed" : "")}";
            _toast.Show("Manifests",
                $"Downloaded {ok} manifest(s) beside {Path.GetFileName(savedPath)}" +
                (skipped > 0 ? $", {skipped} already present" : "") +
                (fail > 0 ? $", {fail} failed." : "."),
                error: ok == 0 && fail > 0);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Manifest download failed: {ex.Message}";
            _toast.Show("Manifests", $"Manifest download failed: {ex.Message}", error: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CopyToClipboard()
    {
        var selected = Games.Where(t => t.IsSelected).Select(t => t.Game).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No games selected";
            _toast.Show("Copy", "No games selected.", error: true);
            return;
        }

        IsLoading = true;
        StatusMessage = $"Fetching keys for {selected.Count} game(s)...";
        try
        {
            await _steam.EnsureExportDataAsync(selected);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Key fetch failed: {ex.Message}";
            _toast.Show("Copy", $"Key fetch failed: {ex.Message}", error: true);
            return;
        }
        finally
        {
            IsLoading = false;
        }

        foreach (var tile in Games.Where(t => t.IsSelected))
            tile.RefreshFromGame();

        var content = selected.Count == 1
            ? _export.Export(selected[0])
            : _export.ExportMultiple(selected);

        Clipboard.SetText(content);
        StatusMessage = $"Copied {selected.Count} game(s) to clipboard";
        _toast.Show("Copy", $"Copied {selected.Count} game(s) to clipboard.");
    }
}
