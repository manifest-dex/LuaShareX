using System.IO;
using System.IO.Compression;
using System.Collections.ObjectModel;
using System.Text;
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
    private readonly ManifestDeXUploadService _uploader;

    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectAllText = "Select All";
    [ObservableProperty] private string _exportText = "Export .lua";
    [ObservableProperty] private bool _showShareDialog;
    [ObservableProperty] private string _shareDialogFileName = "";
    [ObservableProperty] private int _shareDialogGameCount;
    [ObservableProperty] private bool _shareDialogIsUploading;
    [ObservableProperty] private double _shareDialogProgress;
    [ObservableProperty] private string _shareDialogStatus = "";

    partial void OnSearchTextChanged(string value)
    {
        FilterGames();
    }

    private List<GameTileViewModel> _allTiles = [];
    private CancellationTokenSource? _coverCts;

    public GamesViewModel(SteamService steam, LuaExportService export, CoverCache covers, ToastService toast, ManifestDeXUploadService uploader)
    {
        _steam = steam;
        _export = export;
        _covers = covers;
        _toast = toast;
        _uploader = uploader;
    }

    public void LoadGamesFromList(List<SteamGame> games)
    {
        try { _coverCts?.Cancel(); } catch { }
        _coverCts?.Dispose();
        _coverCts = new CancellationTokenSource();
        var ct = _coverCts.Token;
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
        _ = PrefetchCoversAsync(_allTiles, ct);
    }

    private async Task PrefetchCoversAsync(List<GameTileViewModel> tiles, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = tiles.Select(async tile =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!ct.IsCancellationRequested)
                    await tile.EnsureCoverAsync(_covers);
            }
            catch (OperationCanceledException) { }
            finally
            {
                gate.Release();
            }
        });
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
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
        try
        {
            Clipboard.SetText(tile.AppId.ToString());
            StatusMessage = $"Copied App ID {tile.AppId}";
            _toast.Show("Copy", $"Copied App ID {tile.AppId}.");
        }
        catch
        {
            StatusMessage = "Clipboard is busy â€” try again";
            _toast.Show("Copy", "Clipboard is busy â€” try again.", error: true);
        }
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

    /// <summary>Failure reasons that stay readable in the status bar, e.g.
    /// " (Depot 1311106: timed out)". Empty when everything was fetched.</summary>
    private static string KeyFailureSuffix(List<(uint depotId, string reason)> failures)
    {
        if (failures.Count == 0) return "";
        var shown = failures
            .OrderBy(f => f.depotId)
            .Take(4)
            .Select(f => $"Depot {f.depotId}: {f.reason}");
        var extra = failures.Count > 4 ? $"; +{failures.Count - 4} more" : "";
        return $" ({string.Join("; ", shown)}{extra})";
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
        List<(uint depotId, string reason)> keyFailures = [];
        try
        {
            (_, _, _, keyFailures) = await _steam.EnsureExportDataAsync(selected);
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
                StatusMessage = $"Exported {selected[0].Name} to {Path.GetFileName(dialog.FileName)}{KeyFailureSuffix(keyFailures)}";
                _toast.Show("Export", $"Exported {selected[0].Name} to {Path.GetFileName(dialog.FileName)}.");
                MaybeShareViaManifestDeXAsync(selected, dialog.FileName);
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
            StatusMessage = $"Exported {selected.Count} game(s) to {Path.GetFileName(zipDialog.FileName)}{KeyFailureSuffix(keyFailures)}";
            _toast.Show("Export", $"Exported {selected.Count} game(s) to {Path.GetFileName(zipDialog.FileName)}.");
            MaybeShareViaManifestDeXAsync(selected, zipDialog.FileName);
        }
    }

    private List<SteamGame> _pendingShareGames = [];
    private string _pendingSharePath = "";
    private CancellationTokenSource? _uploadCts;

    /// <summary>Opens the themed share dialog after a successful export.
    /// Never fails the export itself: the file is already on disk by now.</summary>
    private void MaybeShareViaManifestDeXAsync(List<SteamGame> selected, string savedPath)
    {
        _pendingShareGames = selected.ToList();
        _pendingSharePath = savedPath;
        ShareDialogFileName = Path.GetFileName(savedPath);
        ShareDialogGameCount = selected.Count;
        ShareDialogIsUploading = false;
        ShareDialogProgress = 0;
        ShareDialogStatus = "";
        ShowShareDialog = true;
    }

    [RelayCommand]
    private void CloseShareDialog()
    {
        if (ShareDialogIsUploading)
        {
            // Cancel the in-flight upload and let ConfirmShareDialogAsync clean up.
            _uploadCts?.Cancel();
            return;
        }
        ShowShareDialog = false;
    }

    [RelayCommand]
    private async Task ConfirmShareDialogAsync()
    {
        var selected = _pendingShareGames.ToList();
        var savedPath = _pendingSharePath;
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(savedPath))
        {
            ShowShareDialog = false;
            return;
        }

        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;

        ShareDialogIsUploading = true;
        ShareDialogStatus = "Preparing upload...";
        var zipPath = savedPath;
        string? tempZip = null;
        try
        {
            if (savedPath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) && selected.Count == 1)
            {
                tempZip = Path.Combine(Path.GetTempPath(), $"luasharex_share_{selected[0].AppId}_{Guid.NewGuid():N}.zip");
                using var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create);
                var entry = zip.CreateEntry($"{selected[0].AppId}.lua", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(_export.Export(selected[0]));
                zipPath = tempZip;
            }

            var info = new FileInfo(zipPath);
            if (info.Length > 100L * 1024 * 1024)
            {
                ShareDialogStatus = "Exported zip is larger than 100 MB and cannot be staged.";
                ShareDialogIsUploading = false;
                return;
            }

            var prog = new Progress<double>(p =>
            {
                ShareDialogProgress = p;
                ShareDialogStatus = $"Uploading to ManifestDeX... {p:P0}";
            });
            var (ok, stagedToken, _, error) = await _uploader.StageZipAsync(zipPath, prog, ct);
            if (!ok || string.IsNullOrWhiteSpace(stagedToken))
            {
                ShareDialogStatus = ct.IsCancellationRequested
                    ? "Upload cancelled."
                    : $"Upload failed: {error ?? "unknown error"}";
                ShareDialogIsUploading = false;
                if (ct.IsCancellationRequested) ShowShareDialog = false;
                return;
            }

            var url = ManifestDeXUploadService.BuildConfirmUrl(stagedToken);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShareDialogStatus = $"Browser could not be opened ({ex.Message}). Confirm within 24 hours: {url}";
                ShareDialogIsUploading = false;
                return;
            }

            try { System.Windows.Clipboard.SetDataObject(url, copy: true); } catch { /* clipboard is best-effort */ }

            ShowShareDialog = false;
            StatusMessage = "Upload ready â€” confirm it on ManifestDeX within 24 hours.";
            _toast.Show("ManifestDeX", $"Upload ready. Confirm link copied to clipboard.");
        }
        catch (OperationCanceledException)
        {
            ShareDialogStatus = "Upload cancelled.";
            ShareDialogIsUploading = false;
            ShowShareDialog = false;
        }
        catch (Exception ex)
        {
            ShareDialogStatus = $"Upload failed: {ex.Message}";
            ShareDialogIsUploading = false;
        }
        finally
        {
            _uploadCts?.Dispose();
            _uploadCts = null;
            try
            {
                if (tempZip != null && File.Exists(tempZip)) File.Delete(tempZip);
            }
            catch { /* best effort temp cleanup */ }
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
        List<(uint depotId, string reason)> keyFailures = [];
        try
        {
            (_, _, _, keyFailures) = await _steam.EnsureExportDataAsync(selected);
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

        try
        {
            Clipboard.SetText(content);
        }
        catch
        {
            StatusMessage = "Clipboard is busy â€” try again";
            _toast.Show("Copy", "Clipboard is busy â€” try again.", error: true);
            return;
        }
        StatusMessage = $"Copied {selected.Count} game(s) to clipboard{KeyFailureSuffix(keyFailures)}";
        _toast.Show("Copy", $"Copied {selected.Count} game(s) to clipboard.");
    }

    /// <summary>Fetches depot keys for the whole library and reports every
    /// failure with its reason, instead of exporting one game at a time.
    /// Full per-depot report goes to a temp file (path shown in status).</summary>
    [RelayCommand]
    private async Task TestAllKeys()
    {
        var all = _allTiles.Select(t => t.Game).ToList();
        if (all.Count == 0)
        {
            StatusMessage = "Library is empty â€” nothing to test";
            _toast.Show("Key test", "Library is empty.", error: true);
            return;
        }

        IsLoading = true;
        StatusMessage = $"Testing keys across {all.Count} game(s)... this can take a while";
        try
        {
            var (_, _, _, failures) = await _steam.EnsureExportDataAsync(all);
            foreach (var tile in _allTiles)
                tile.RefreshFromGame();

            var depotToGame = all
                .SelectMany(g => g.Depots.Select(d => (d.DepotId, g)))
                .ToLookup(t => t.DepotId, t => t.g);
            var lines = failures
                .OrderBy(f => f.depotId)
                .Select(f =>
                {
                    var owner = depotToGame[f.depotId].FirstOrDefault();
                    return $"{owner?.Name ?? "Unknown"} ({owner?.AppId}) â€” Depot {f.depotId}: {f.reason}";
                })
                .ToList();

            var report = Path.Combine(Path.GetTempPath(), $"luasharex_keytest_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllLines(report,
                new[] { $"LuaShareX key test â€” {DateTime.Now:G} â€” {all.Count} game(s), {failures.Count} failure(s)", "" }.Concat(lines));

            if (failures.Count == 0)
            {
                StatusMessage = $"Key test: all {all.Count} game(s) OK ({report})";
                _toast.Show("Key test", $"All {all.Count} game(s) OK.");
            }
            else
            {
                StatusMessage = $"Key test: {failures.Count} failure(s) â€” {string.Join("; ", lines.Take(3))}" +
                    (failures.Count > 3 ? "; â€¦" : "") + $" (full report: {report})";
                _toast.Show("Key test", $"{failures.Count} failure(s). Full report: {Path.GetFileName(report)}.", error: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Key test failed: {ex.Message}";
            _toast.Show("Key test", $"Key test failed: {ex.Message}", error: true);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
