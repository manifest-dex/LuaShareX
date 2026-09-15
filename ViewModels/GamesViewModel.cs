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

    public async void LoadGames()
    {
        IsLoading = true;
        StatusMessage = "Loading games...";
        Games.Clear();

        try
        {
            _allGames = await _steam.GetOwnedGames();
            foreach (var game in _allGames.OrderBy(g => g.Name))
            {
                Games.Add(game);
            }
            StatusMessage = $"{Games.Count} games loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading games: {ex.Message}";
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
    private async Task FetchDetails()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No games selected";
            return;
        }

        IsLoading = true;
        StatusMessage = $"Fetching details for {selected.Count} games...";

        foreach (var game in selected)
        {
            StatusMessage = $"Fetching {game.Name}...";
            var details = await _steam.GetGameDetails(game.AppId);
            if (details != null)
            {
                game.Depots = details.Depots;
                game.Dlcs = details.Dlcs;
            }
        }

        IsLoading = false;
        StatusMessage = $"Details fetched for {selected.Count} games";
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
