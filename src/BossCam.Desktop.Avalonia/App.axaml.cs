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
            // Keep the desktop media consumer authenticated with the same LAN gate as its
            // manifest/snapshot requests. Environment variables are used here rather than
            // embedding a secret in the executable or diagnostic output.
            apiClient.LanToken = ResolveConfiguredLanToken(
                Environment.GetEnvironmentVariable("BOSSCAM_LAN_TOKEN"),
                Environment.GetEnvironmentVariable("BossCam__LanAuthToken"));
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

    /// <summary>
    /// Resolves the LAN token using the same precedence as BossCam.Service: the dedicated
    /// environment variable wins, then the .NET double-underscore configuration form.
    /// Whitespace-only values are treated as unset so ffmpeg never receives a blank header.
    /// Internal for regression tests; the value is never logged.
    /// </summary>
    internal static string? ResolveConfiguredLanToken(string? environmentToken, string? configurationToken)
    {
        var environment = environmentToken?.Trim();
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return environment;
        }

        var configuration = configurationToken?.Trim();
        return string.IsNullOrWhiteSpace(configuration) ? null : configuration;
    }
}
