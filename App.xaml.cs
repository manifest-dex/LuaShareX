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
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<GamesViewModel>();
        _services = services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _services.GetRequiredService<MainViewModel>();
        mainWindow.Show();
    }
}
