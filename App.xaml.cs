using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using LuaShareX.Services;
using LuaShareX.ViewModels;

namespace LuaShareX;

public partial class App : Application
{
    private readonly ServiceProvider _services;

    public App()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SteamService>();
        services.AddSingleton<LuaExportService>();
        services.AddSingleton<CoverCache>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<GamesViewModel>();
        _services = services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var mainWindow = _services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _services.GetRequiredService<MainViewModel>();
            _services.GetRequiredService<ToastService>().Attach(mainWindow.ToastPresenter);
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup error: {ex}", "LuaShareX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }
}
