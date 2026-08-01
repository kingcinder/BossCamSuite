using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BossCam.Desktop.Avalonia.Services;
using BossCam.Desktop.Avalonia.ViewModels;
using Microsoft.Extensions.Logging;

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
            // Production wiring: a shared LoggerFactory (console sink) so the API client and
            // ViewModels can log failures instead of swallowing them. Avalonia's own logging
            // stays on LogToTrace() in Program.cs; this factory is for app-layer diagnostics.
            _loggerFactory = LoggerFactory.Create(builder =>
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

            var apiClient = new HttpBossCamApiClient(
                logger: _loggerFactory.CreateLogger<HttpBossCamApiClient>());
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    apiClient,
                    _loggerFactory.CreateLogger<MainWindowViewModel>()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ILoggerFactory? _loggerFactory;
}
