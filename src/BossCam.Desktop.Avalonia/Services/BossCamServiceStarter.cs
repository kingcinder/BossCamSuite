using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BossCam.Desktop.Avalonia.Services;

/// <summary>
/// Brings the local BossCamService up when the startup health check fails.
/// Strategy order:
///   1. systemd — <c>systemctl start bosscam.service</c> (installed production unit).
///   2. Direct spawn — the published install (<c>/opt/bosscam/BossCam.Service.dll</c>)
///      or, when running from a source checkout, <c>dotnet run --project</c> on
///      <c>src/BossCam.Service/BossCam.Service.csproj</c>.
/// Each strategy polls the caller-supplied health predicate until it reports true or
/// its timeout elapses; the first strategy that yields a healthy service wins.
/// Any process spawned here is owned by this instance and terminated on Dispose.
/// </summary>
public sealed class BossCamServiceStarter : IBossCamServiceStarter
{
    private static readonly TimeSpan SystemdWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SpawnWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SystemdStartTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly string _publishedServiceDir;
    private readonly string _devSearchRoot;
    private readonly bool _allowSystemd;
    private readonly string? _dotnetPathOverride;
    private Process? _spawnedProcess;

    /// <summary>Production constructor — published install defaults to /opt/bosscam.</summary>
    public BossCamServiceStarter(ILogger? logger = null)
        : this(logger ?? NullLogger.Instance, "/opt/bosscam", AppContext.BaseDirectory, allowSystemd: true, dotnetPathOverride: null)
    {
    }

    /// <summary>
    /// Test constructor — injects the published dir, the dev-project search root,
    /// whether the systemd branch may run (tests pass false so no systemctl call is
    /// made), and an optional explicit .NET host path so tests avoid mutating the
    /// process-wide DOTNET_ROOT environment variable.
    /// </summary>
    internal BossCamServiceStarter(
        ILogger logger,
        string publishedServiceDir,
        string devSearchRoot,
        bool allowSystemd = true,
        string? dotnetPathOverride = null)
    {
        _logger = logger;
        _publishedServiceDir = publishedServiceDir;
        _devSearchRoot = devSearchRoot;
        _allowSystemd = allowSystemd;
        _dotnetPathOverride = dotnetPathOverride;
    }

    public async Task<bool> TryStartAsync(Func<Task<bool>> isHealthy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isHealthy);

        // 1) systemd unit (installed production layout). systemctl start on a
        //    Type=notify unit blocks until the service reports readiness, so a
        //    bounded async wait here usually means the service is up right after.
        if (_allowSystemd && BuildSystemdStartInfo() is { } systemdInfo)
        {
            try
            {
                using var systemd = Process.Start(systemdInfo);
                if (systemd is not null)
                {
                    try
                    {
                        // Async so a wedged unit cannot freeze the UI thread. net8.0
                        // has no WaitForExitAsync(TimeSpan) overload, so bound the wait
                        // with a linked timeout token that also observes the caller's
                        // cancellation.
                        using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        startTimeout.CancelAfter(SystemdStartTimeout);
                        await systemd.WaitForExitAsync(startTimeout.Token);
                        if (systemd.ExitCode == 0)
                        {
                            if (await WaitForHealthyAsync(isHealthy, SystemdWait, cancellationToken))
                            {
                                return true;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // systemctl still running after the timeout — reap it so it
                        // cannot linger as a zombie, then poll anyway: the unit may
                        // still come up before the fallback spawn is attempted.
                        KillSafely(systemd);
                        if (await WaitForHealthyAsync(isHealthy, SystemdWait, cancellationToken))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Non-fatal: fall through to a direct spawn. Deliberately re-throws when
                // the caller cancelled: a close-during-systemd-wait must not proceed to
                // spawn a service process that Dispose (already run) would never reap.
                _logger.LogDebug(ex, "systemctl start bosscam.service failed");
            }
        }

        // 2) Direct spawn — the GUI runs the service itself. Prefer the published
        //    install; fall back to the dev checkout when running from source.
        //    Re-check cancellation before spawning: if the caller cancelled between
        //    the systemd attempt and here, Dispose has already run and a process
        //    spawned now would never be reaped.
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = BuildServiceStartInfo();
        if (startInfo is null)
        {
            _logger.LogDebug("No published BossCam.Service.dll or dev project found to start");
            return false;
        }

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }
            _spawnedProcess = process;
            _logger.LogDebug("Spawned BossCamService via {FileName}", startInfo.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to spawn BossCamService via {FileName}", startInfo.FileName);
            return false;
        }

        return await WaitForHealthyAsync(isHealthy, SpawnWait, cancellationToken);
    }

    /// <summary>
    /// Polls <paramref name="isHealthy"/> until it returns true or <paramref name="timeout"/>
    /// elapses. Probe failures (connection refused, slow timeouts) are treated as
    /// "not ready yet", never as fatal — except when <paramref name="cancellationToken"/>
    /// is cancelled, which propagates.
    /// </summary>
    internal static async Task<bool> WaitForHealthyAsync(
        Func<Task<bool>> isHealthy, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool healthy;
            try
            {
                healthy = await isHealthy();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Service not listening yet, or a probe timed out — keep polling.
                healthy = false;
            }
            if (healthy)
            {
                return true;
            }
            await Task.Delay(HealthPollInterval, cancellationToken);
        }
        return false;
    }

    /// <summary>True when a systemd unit for the service exists on this Linux host.</summary>
    internal static bool IsSystemdUnitAvailable()
        => OperatingSystem.IsLinux()
           && File.Exists("/usr/bin/systemctl")
           && (File.Exists("/etc/systemd/system/bosscam.service")
               || File.Exists("/usr/lib/systemd/system/bosscam.service")
               || File.Exists("/lib/systemd/system/bosscam.service"));

    /// <summary>
    /// ProcessStartInfo for <c>systemctl start bosscam.service</c>, or null when the
    /// unit is not installed. Output is not redirected: systemctl exits quickly and
    /// inheriting the console keeps its diagnostics visible in the GUI's own output.
    /// </summary>
    internal static ProcessStartInfo? BuildSystemdStartInfo()
        => IsSystemdUnitAvailable()
            ? new ProcessStartInfo
            {
                FileName = "/usr/bin/systemctl",
                UseShellExecute = false,
                CreateNoWindow = true,
            }.WithArguments(["start", "bosscam.service"])
            : null;

    /// <summary>
    /// ProcessStartInfo that runs the service directly. Prefers the published install
    /// (<see cref="_publishedServiceDir"/>/BossCam.Service.dll via the .NET host) and
    /// falls back to <c>dotnet run --project</c> on the dev checkout. Null when neither
    /// exists.
    /// </summary>
    internal ProcessStartInfo? BuildServiceStartInfo()
    {
        var dotnet = ResolveDotnetPath(_dotnetPathOverride);
        if (dotnet is null)
        {
            return null;
        }

        var publishedDll = Path.Combine(_publishedServiceDir, "BossCam.Service.dll");
        if (File.Exists(publishedDll))
        {
            return new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = _publishedServiceDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            }.WithArguments([publishedDll]);
        }

        var devProject = LocateDevProject(_devSearchRoot);
        if (devProject is not null)
        {
            return new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = Path.GetDirectoryName(devProject) ?? _devSearchRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
            }.WithArguments(["run", "--project", devProject, "--no-launch-profile"]);
        }

        return null;
    }

    /// <summary>
    /// Walks up from <paramref name="searchRoot"/> looking for the service project,
    /// so a GUI running from a source checkout can start the service without a
    /// published install. Returns the csproj path or null.
    /// </summary>
    internal static string? LocateDevProject(string searchRoot)
    {
        var dir = new DirectoryInfo(searchRoot);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BossCam.Service", "BossCam.Service.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Resolves the .NET host: an injected override wins (tests), then
    /// DOTNET_ROOT/dotnet when configured, otherwise the plain executable name so
    /// the PATH lookup applies.
    /// </summary>
    internal static string? ResolveDotnetPath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return name;
    }

    private static void KillSafely(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Process already gone — cleanup is best effort.
        }
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref _spawnedProcess, null);
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Process already gone or torn down concurrently — cleanup is best effort.
        }
        finally
        {
            process.Dispose();
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    /// <summary>Adds arguments one-per-element, mirroring ProcessLauncher conventions.</summary>
    internal static ProcessStartInfo WithArguments(this ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }
        return info;
    }
}
