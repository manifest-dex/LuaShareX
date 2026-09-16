using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace LuaShareX.Services;

/// <summary>
/// App-wide bottom-right toast feedback. Thin wrapper over Wpf.Ui's SnackbarService. The presenter
/// is attached once from App.OnStartup. Safe to call from any thread.
/// </summary>
public class ToastService
{
    private readonly SnackbarService _snackbar = new();
    private SnackbarPresenter? _presenter;

    /// <summary>Wire the presenter that hosts the toasts (called once after the window is built).</summary>
    public void Attach(SnackbarPresenter presenter)
    {
        _presenter = presenter;
        _snackbar.SetSnackbarPresenter(presenter);
    }

    /// <summary>Show a transient toast (auto-dismiss). Marshals to the UI thread; no-ops if unattached.</summary>
    public void Show(string title, string message, bool error = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Post() => _snackbar.Show(
            title, message,
            error ? ControlAppearance.Caution : ControlAppearance.Secondary,
            null,
            TimeSpan.FromSeconds(3));

        if (dispatcher.CheckAccess()) Post();
        else dispatcher.Invoke(Post);
    }
}
