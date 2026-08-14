using Avalonia.Controls;
using Avalonia.Input;
using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia.Views;

public partial class LiveView : UserControl
{
    public LiveView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Single click on a board tile selects it (drives the detail pane + management
    /// sections). Selection is idempotent, so the two presses of a double-click also
    /// fire this harmlessly before <see cref="OnTileDoubleTapped"/> opens fullscreen.
    /// </summary>
    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed is false)
        {
            return;
        }
        if (sender is Control { DataContext: BoardTileViewModel tile } && e.ClickCount == 1)
        {
            tile.SelectCommand.Execute(null);
        }
    }

    /// <summary>Double-click opens the immersive fullscreen camera view.</summary>
    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: BoardTileViewModel tile })
        {
            tile.OpenFullscreenCommand.Execute(null);
        }
    }
}
