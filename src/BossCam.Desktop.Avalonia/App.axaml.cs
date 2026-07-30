using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BossCam.Desktop.Avalonia.Services;
using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Production wiring: create the HTTP client and inject into the ViewModel.
            // The parameterless constructor also works (it internally creates HttpBossCamApiClient),
            // but this explicit form makes the dependency visible and is ready for DI container swap.
            var apiClient = new HttpBossCamApiClient();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(apiClient),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
