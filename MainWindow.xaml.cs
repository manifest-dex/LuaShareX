using System.Windows;
using System.Windows.Controls;
using LuaShareX.ViewModels;

namespace LuaShareX;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CurrentView is GamesViewModel gamesVm)
        {
            gamesVm.SelectionChangedCommand.Execute(null);
        }
    }
}
