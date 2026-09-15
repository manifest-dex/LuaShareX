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

    public ObservableCollection<SteamGame> Games { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _manualAppId = "";

    partial void OnSearchTextChanged(string value)
    {
        FilterGames();
    }

    private List<SteamGame> _allGames = [];

    public GamesViewModel(SteamService steam, LuaExportService export)
    {
        _steam = steam;
        _export = export;
    }

    public void LoadGamesFromList(List<SteamGame> games)
    {
        _allGames = games;
        Games.Clear();
        foreach (var game in _allGames.OrderBy(g => g.Name))
        {
            Games.Add(game);
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void RefreshGames()
    {
        if (_steam.SteamInstallPath == null)
        {
            StatusMessage = "Steam not found";
            return;
        }

        StatusMessage = "Refreshing...";
        var games = _steam.GetInstalledGames();
        LoadGamesFromList(games);
        StatusMessage = $"Loaded {games.Count} installed games";
    }

    [RelayCommand]
    private async Task AddManualGame()
    {
        if (string.IsNullOrWhiteSpace(ManualAppId))
        {
            StatusMessage = "Enter an App ID";
            return;
        }

        if (!uint.TryParse(ManualAppId.Trim(), out var appId))
        {
            StatusMessage = "Invalid App ID";
            return;
        }

        if (_allGames.Any(g => g.AppId == appId))
        {
            StatusMessage = $"App {appId} already in list";
            return;
        }

        IsLoading = true;
        StatusMessage = $"Fetching details for App {appId}...";

        // Try to get name from Steam Store API
        var http = new System.Net.Http.HttpClient();
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await http.GetStringAsync(url);
            var doc = System.Text.Json.JsonDocument.Parse(response);

            string name = $"App {appId}";
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var appData) &&
                appData.TryGetProperty("success", out var success) &&
                success.GetBoolean() &&
                appData.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var nameEl))
            {
                name = nameEl.GetString() ?? name;
            }

            var game = new SteamGame { AppId = appId, Name = name };
            _allGames.Add(game);
            Games.Add(game);
            ManualAppId = "";
            StatusMessage = $"Added {name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FilterGames()
    {
        Games.Clear();
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allGames
            : _allGames.Where(g => g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var game in filtered.OrderBy(g => g.Name))
            Games.Add(game);

        UpdateSelectedCount();
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var allSelected = Games.All(g => g.IsSelected);
        foreach (var game in Games)
            game.IsSelected = !allSelected;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectionChanged()
    {
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Games.Count(g => g.IsSelected);
    }

    [RelayCommand]
    private void ExportSelected()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No games selected for export";
            return;
        }

        var content = selected.Count == 1
            ? _export.Export(selected[0])
            : _export.ExportMultiple(selected);

        var dialog = new SaveFileDialog
        {
            Filter = "Lua files (*.lua)|*.lua|All files (*.*)|*.*",
            DefaultExt = ".lua",
            FileName = selected.Count == 1
                ? $"{selected[0].AppId}.lua"
                : $"luasharex_export_{DateTime.Now:yyyyMMdd_HHmmss}.lua"
        };

        if (dialog.ShowDialog() == true)
        {
            _export.SaveToFile(content, dialog.FileName);
            StatusMessage = $"Exported {selected.Count} game(s) to {Path.GetFileName(dialog.FileName)}";
        }
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No games selected";
            return;
        }

        var content = selected.Count == 1
            ? _export.Export(selected[0])
            : _export.ExportMultiple(selected);

        Clipboard.SetText(content);
        StatusMessage = $"Copied {selected.Count} game(s) to clipboard";
    }
}
