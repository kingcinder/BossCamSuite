using System.Diagnostics;
using System.Net.Http.Json;
using BossCam.Contracts;

namespace BossCam.E2E;

/// <summary>
/// Exercises the snapshot-pipeline branch of <see cref="BossCam.Core.RecordingService"/>
/// without any cameras on the LAN. The bash script on Linux/macOS writes
/// <c>/tmp/bosscam-rec-{guid:N}.sh</c> and pipes <c>curl | ffmpeg</c>. By redirecting
/// <c>BOSSCAM_FFMPEG_PATH</c> at a tiny shell stub we deterministically control whether
/// the pipeline self-exits or stays alive, so both cleanup paths can be asserted in CI
/// without any 5523-W hardware on the network.
/// </summary>
[Collection("BossCamE2E")]
public sealed class SimulatedLanCleanupTests : IClassFixture<BossCamWebAppFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly BossCamWebAppFactory _factory;
    private readonly List<string> _stubPaths = new();
    private readonly HashSet<Guid> _ownedDeviceIds = [];
    private readonly HashSet<string> _ownedSnapshotIps = new(StringComparer.Ordinal);
    private readonly string? _originalFfmpegEnv;

    public SimulatedLanCleanupTests(BossCamWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _originalFfmpegEnv = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
    }

    public void Dispose()
    {
        // Best-effort teardown: clean up any orphan bash/curl/sleep children left over
        // from the snapshot pipeline. Process.Kill(entireProcessTree:true) on Linux does NOT
        // walk the cgroup, so /tmp/bosscam-rec-*.sh would otherwise keep being created by
        // long-curl retries. A bash-side pkill sweep is the only portable cleanup.
        KillProcessTree();

        try
        {
            Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", _originalFfmpegEnv);
        }
        catch
        {
            // best-effort teardown
        }

        foreach (var stub in _stubPaths)
        {
            try
            {
                if (File.Exists(stub))
                {
                    File.Delete(stub);
                }
            }
            catch
            {
                // best-effort teardown
            }
        }
    }

    [Fact]
    public void CleanupPatterns_Are_Scoped_To_Test_Owned_Artifacts()
    {
        var deviceId = Guid.NewGuid();
        var stub = $"/tmp/bosscam-stub-ffmpeg-{Guid.NewGuid():N}.sh";
        var patterns = BuildCleanupPatterns([deviceId], [stub], ["10.99.99.10"]);

        Assert.Contains($"bosscam-rec-{deviceId:N}", patterns);
        Assert.Contains(Path.GetFileNameWithoutExtension(stub), patterns);
        Assert.Contains("curl.*10\\.99\\.99\\.10.*NetSDK.*snapShot", patterns);
        Assert.DoesNotContain("bosscam-rec-", patterns);
        Assert.DoesNotContain("bosscam-stub-ffmpeg-", patterns);
    }

    private void KillProcessTree()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (var pattern in BuildCleanupPatterns(_ownedDeviceIds, _stubPaths, _ownedSnapshotIps))
        {
            try
            {
                var pkill = new ProcessStartInfo
                {
                    FileName = "pkill",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                pkill.ArgumentList.Add("-f");
                pkill.ArgumentList.Add(pattern);
                using var process = Process.Start(pkill);
                process?.WaitForExit(2000);
            }
            catch
            {
                // pkill not available; nothing else we can do without /proc walking.
            }
        }
    }

    private static IReadOnlyCollection<string> BuildCleanupPatterns(
        IEnumerable<Guid> deviceIds,
        IEnumerable<string> stubPaths,
        IEnumerable<string> snapshotIps)
    {
        var patterns = deviceIds.Select(id => $"bosscam-rec-{id:N}").ToList();
        patterns.AddRange(stubPaths
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>());
        patterns.AddRange(snapshotIps.Select(ip => $"curl.*{System.Text.RegularExpressions.Regex.Escape(ip)}.*NetSDK.*snapShot"));
        return patterns;
    }

    [Fact]
    public async Task StopAsync_Deletes_Snapshot_Pipeline_Script()
    {
        // Long-running stub keeps the pipeline alive long enough to invoke /stop/{id}.
        var stub = WriteStubFfmpeg(sleepSeconds: 30, exitCode: 0);
        _stubPaths.Add(stub);
        Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", stub);

        // Clear any leftover recordings carried over from other tests in this class.
        await _client.PostAsync("/api/recordings/stop-all", null);

        const string ip = "10.99.99.10";
        var deviceId = await RegisterOfflineDeviceAsync(ip);
        var start = await _client.PostAsJsonAsync("/api/recordings/start", new
        {
            deviceId,
            sourceUrl = "snapshot"
        });
        start.EnsureSuccessStatusCode();
        var job = await start.Content.ReadFromJsonAsync<RecordingJob>();
        Assert.NotNull(job);

        var scriptPath = $"/tmp/bosscam-rec-{deviceId:N}.sh";
        await WaitForAsync(() => File.Exists(scriptPath), TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(scriptPath), $"expected scratch script was not created at {scriptPath}");

        var stop = await _client.PostAsync($"/api/recordings/stop/{job!.Id}", null);
        stop.EnsureSuccessStatusCode();

        await WaitForAsync(() => !File.Exists(scriptPath), TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(scriptPath),
            $"snapshot pipeline script was not deleted after StopAsync: {scriptPath}");

    }

    [Fact]
    public async Task Spontaneous_Process_Exit_Deletes_Snapshot_Pipeline_Script()
    {
        // The bash pipeline is `while true; do curl ...; sleep 0.5; done | ffmpeg`. The
        // while-loop is infinite, so even when stub ffmpeg exits the pipeline as a whole
        // never completes — bash waits forever for the LHS to drain. We must simulate a
        // spontaneous exit by killing the bash process externally (camera drop / ffmpeg
        // EOF / signal); the production code path is identical: process.Exited fires on
        // any non-zero exit and the RecordingService handler runs TryDeleteScript.
        var stub = WriteStubFfmpeg(sleepSeconds: 60, exitCode: 0);
        _stubPaths.Add(stub);
        Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", stub);

        await _client.PostAsync("/api/recordings/stop-all", null);

        const string ip = "10.99.99.11";
        var deviceId = await RegisterOfflineDeviceAsync(ip);
        var start = await _client.PostAsJsonAsync("/api/recordings/start", new
        {
            deviceId,
            sourceUrl = "snapshot"
        });
        start.EnsureSuccessStatusCode();
        var job = await start.Content.ReadFromJsonAsync<RecordingJob>();
        Assert.NotNull(job);

        var scriptPath = $"/tmp/bosscam-rec-{deviceId:N}.sh";
        await WaitForAsync(() => File.Exists(scriptPath), TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(scriptPath), $"expected scratch script was not created at {scriptPath}");

        // Externally kill the bash process to simulate camera drop / ffmpeg EOF / signal
        // (the production code path is identical: process.Exited fires → handler runs).
        if (job!.ProcessId is int pid)
        {
            try
            {
                using var bashProc = Process.GetProcessById(pid);
                bashProc.Kill(entireProcessTree: true);
            }
            catch
            {
                // process may have already exited; cleanup handler still runs on its own.
            }
        }

        await WaitForAsync(() => !File.Exists(scriptPath), TimeSpan.FromSeconds(15));
        Assert.False(File.Exists(scriptPath),
            $"snapshot pipeline script was not deleted after spontaneous process exit: {scriptPath}");

    }

    private async Task<Guid> RegisterOfflineDeviceAsync(string ip)
    {
        var reg = await _client.PostAsJsonAsync("/api/devices/register", new
        {
            ipAddress = ip,
            port = 9, // closed → curl fails fast instead of hanging on connect
            loginName = "admin",
            password = "",
            name = $"cleanup-{ip}",
            hardwareModel = "fake"
        });
        reg.EnsureSuccessStatusCode();
        var device = await reg.Content.ReadFromJsonAsync<DeviceIdentity>();
        Assert.NotNull(device);
        _ownedDeviceIds.Add(device!.Id);
        _ownedSnapshotIps.Add(ip);
        return device.Id;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < until)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(150);
        }
    }

    private static string WriteStubFfmpeg(double sleepSeconds, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bosscam-stub-ffmpeg-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path,
            "#!/usr/bin/env bash\n" +
            $"sleep {sleepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
            $"exit {exitCode}\n");
        try
        {
            using var chmod = Process.Start("chmod", $"+x {path}");
            chmod?.WaitForExit(2000);
        }
        catch
        {
            // chmod unavailable on Windows; the .NET Process ctor on Linux still
            // resolves shebang through /usr/bin/env bash so this only matters for tests.
        }
        return path;
    }
}
