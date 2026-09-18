using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LuaShareX.ViewModels;

namespace LuaShareX;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: GameTileViewModel tile } &&
            DataContext is MainViewModel vm)
        {
            vm.GamesVm.ToggleSelectGameCommand.Execute(tile);
        }
    }
}
