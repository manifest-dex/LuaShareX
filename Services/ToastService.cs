using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LuaShareX.Services;

public partial class ToastNotification : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isError;
}

/// <summary>
/// App-wide bottom-right toast stack. Multiple toasts show at once, each
/// auto-dismissing after <see cref="Duration"/> (or via its close button).
/// Safe to call from any thread.
/// </summary>
public partial class ToastService : ObservableObject
{
    /// <summary>How long each toast stays up.</summary>
    public static TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(8);

    private const int MaxVisible = 5;

    public ObservableCollection<ToastNotification> Toasts { get; } = [];

    /// <summary>Show a toast. Never blocks, never throws.</summary>
    public void Show(string title, string message, bool error = false)
    {
        var toast = new ToastNotification { Title = title, Message = message, IsError = error };
        RunOnUi(() =>
        {
            Toasts.Add(toast);
            while (Toasts.Count > MaxVisible)
                Toasts.RemoveAt(0);
        });
        _ = Task.Delay(Duration).ContinueWith(
            _ => RunOnUi(() => Toasts.Remove(toast)),
            TaskScheduler.Default);
    }

    [RelayCommand]
    private void Dismiss(ToastNotification? toast)
    {
        if (toast is null) return;
        RunOnUi(() => Toasts.Remove(toast));
    }

    private static void RunOnUi(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
        catch { /* shutting down */ }
    }
}
