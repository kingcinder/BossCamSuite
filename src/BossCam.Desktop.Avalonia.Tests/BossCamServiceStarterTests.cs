using System.Net;
using BossCam.Desktop.Avalonia.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="BossCamServiceStarter"/>. Pure helpers (health
/// polling, strategy selection, project location) are tested directly; the
/// spawn path is exercised with a throwaway fake .NET host so no real service
/// process is started.
/// </summary>
public sealed class BossCamServiceStarterTests
{
    private static BossCamServiceStarter CreateStarter(
        string publishedDir,
        string devSearchRoot,
        bool allowSystemd = false,
        string? dotnetPathOverride = null)
        => new(NullLogger.Instance, publishedDir, devSearchRoot, allowSystemd, dotnetPathOverride);

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bosscam-starter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Health polling ──────────────────────────────────────────────

    [Fact]
    public async Task WaitForHealthy_Returns_True_When_Predicate_Becomes_Healthy()
    {
        var calls = 0;
        var healthy = await BossCamServiceStarter.WaitForHealthyAsync(
            () => Task.FromResult(++calls >= 3),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(healthy);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitForHealthy_Returns_False_On_Timeout()
    {
        var healthy = await BossCamServiceStarter.WaitForHealthyAsync(
            () => Task.FromResult(false),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task WaitForHealthy_Swallows_Probe_Exceptions_Until_Timeout()
    {
        var healthy = await BossCamServiceStarter.WaitForHealthyAsync(
            () => throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task WaitForHealthy_Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BossCamServiceStarter.WaitForHealthyAsync(
                () => Task.FromResult(false),
                TimeSpan.FromSeconds(5),
                cts.Token));
    }

    // ── Dev project location ────────────────────────────────────────

    [Fact]
    public void LocateDevProject_Finds_Project_By_Walking_Up()
    {
        var root = CreateTempDir();
        try
        {
            var projectDir = Path.Combine(root, "src", "BossCam.Service");
            Directory.CreateDirectory(projectDir);
            var csproj = Path.Combine(projectDir, "BossCam.Service.csproj");
            File.WriteAllText(csproj, "<Project />");

            // Search from a nested directory below the repo root.
            var nested = Path.Combine(root, "src", "BossCam.Desktop.Avalonia", "bin", "Debug");
            Directory.CreateDirectory(nested);

            Assert.Equal(csproj, BossCamServiceStarter.LocateDevProject(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocateDevProject_Returns_Null_When_Missing()
    {
        var root = CreateTempDir();
        try
        {
            Assert.Null(BossCamServiceStarter.LocateDevProject(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Strategy selection ──────────────────────────────────────────

    [Fact]
    public void BuildServiceStartInfo_Prefers_Published_Dll_Over_Dev_Project()
    {
        var published = CreateTempDir();
        var devRoot = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(published, "BossCam.Service.dll"), "fake");
            var devProject = Path.Combine(devRoot, "src", "BossCam.Service", "BossCam.Service.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(devProject)!);
            File.WriteAllText(devProject, "<Project />");

            var starter = CreateStarter(published, devRoot);
            var info = starter.BuildServiceStartInfo();

            Assert.NotNull(info);
            Assert.Contains(Path.Combine(published, "BossCam.Service.dll"), info.ArgumentList);
        }
        finally
        {
            Directory.Delete(published, recursive: true);
            Directory.Delete(devRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildServiceStartInfo_Falls_Back_To_Dev_Project_When_No_Published_Dll()
    {
        var published = CreateTempDir();
        var devRoot = CreateTempDir();
        try
        {
            var devProject = Path.Combine(devRoot, "src", "BossCam.Service", "BossCam.Service.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(devProject)!);
            File.WriteAllText(devProject, "<Project />");

            var starter = CreateStarter(published, devRoot);
            var info = starter.BuildServiceStartInfo();

            Assert.NotNull(info);
            Assert.Contains("run", info.ArgumentList);
            Assert.Contains(devProject, info.ArgumentList);
        }
        finally
        {
            Directory.Delete(published, recursive: true);
            Directory.Delete(devRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildServiceStartInfo_Returns_Null_When_Nothing_Available()
    {
        var published = CreateTempDir();
        var devRoot = CreateTempDir();
        try
        {
            var starter = CreateStarter(published, devRoot);
            Assert.Null(starter.BuildServiceStartInfo());
        }
        finally
        {
            Directory.Delete(published, recursive: true);
            Directory.Delete(devRoot, recursive: true);
        }
    }

    // ── TryStartAsync end-to-end ────────────────────────────────────

    [Fact]
    public async Task TryStartAsync_Returns_False_When_Nothing_Available()
    {
        var published = CreateTempDir();
        var devRoot = CreateTempDir();
        try
        {
            var starter = CreateStarter(published, devRoot);
            using (starter)
            {
                var started = await starter.TryStartAsync(() => Task.FromResult(false));
                Assert.False(started);
            }
        }
        finally
        {
            Directory.Delete(published, recursive: true);
            Directory.Delete(devRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryStartAsync_Spawns_Published_Dll_And_Reports_Healthy()
    {
        if (!OperatingSystem.IsLinux())
        {
            // The fake .NET host below is a POSIX script; skip elsewhere.
            return;
        }

        var published = CreateTempDir();
        var devRoot = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(published, "BossCam.Service.dll"), "fake");
            // A fake "dotnet" that exits immediately — proves the spawn path runs
            // without launching a real .NET host. Injected directly so the test never
            // mutates the process-wide DOTNET_ROOT.
            var fakeDotnet = Path.Combine(published, "dotnet");
            File.WriteAllText(fakeDotnet, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(fakeDotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var starter = CreateStarter(published, devRoot, dotnetPathOverride: fakeDotnet);
            using (starter)
            {
                // The published "dll" exists and the fake host runs; the predicate
                // reports healthy immediately.
                var started = await starter.TryStartAsync(() => Task.FromResult(true));
                Assert.True(started);
            }
        }
        finally
        {
            Directory.Delete(published, recursive: true);
            Directory.Delete(devRoot, recursive: true);
        }
    }
}
