using Avalonia.Controls;
using BossCam.Desktop.Avalonia.Controls;

namespace BossCam.Desktop.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Hover/focus explainer popups over every explained control, hosted on
        // the dedicated overlay panel so they never distort the window layout.
        ExplainerPopupService.Attach(this, ExplainerHost);
    }
}
