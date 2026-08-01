using BossCam.Core;
using BossCam.Infrastructure;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression guards for the transport failover wiring:
///  - The full DI container must build with <c>ValidateOnBuild</c>. This would have caught
///    the TransportBroker ↔ TransportFailoverService singleton circular dependency that
///    failed every E2E WebApplicationFactory host startup before TransportBroker switched
///    to lazy <see cref="IServiceProvider"/> resolution.
///  - TransportBroker.GetSourcesAsync must not recurse (broker → failover → broker → …)
///    for a device that has an IP but no discoverable sources; the AsyncLocal reentrancy
///    guard terminates the chain with an empty source list.
/// </summary>
public sealed class DependencyInjectionCycleTests
{
    [Fact]
    public async Task Full_Container_Builds_With_ValidateOnBuild_No_Circular_Dependency()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Mirror Program.cs host-level registrations that the Core/Infrastructure
        // extensions intentionally don't provide: the SignalR event broadcaster and
        // IHostEnvironment (supplied by the ASP.NET host in production). Without them,
        // ValidateOnBuild flags every service that takes them.
        services.AddSingleton<BossCam.Core.IBossCamEventBroadcaster>(NullBossCamEventBroadcaster.Instance);
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new HostEnvironmentStub("Development"));
        services.AddBossCamInfrastructure(BuildTestConfig());
        services.AddBossCamCore();

        // ValidateOnBuild walks every registered service's constructor graph; a circular
        // singleton dependency (TransportBroker <-> TransportFailoverService) throws here.
        // DisposeAsync is required because LiveStreamService implements IAsyncDisposable
        // (a synchronous Dispose would throw InvalidOperationException).
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Both sides of the former cycle must resolve to usable instances, and a couple of
        // dependents must construct through the real graph (no hand-rolled graph in tests).
        _ = provider.GetRequiredService<TransportBroker>();
        _ = provider.GetRequiredService<TransportFailoverService>();
        _ = provider.GetRequiredService<RecordingService>();
        _ = provider.GetRequiredService<LiveStreamService>();
        Assert.IsType<RecordingProcessSupervisor>(provider.GetRequiredService<RecordingProcessSupervisor>());
        Assert.IsType<ApplicationStoreRecordingStore>(provider.GetRequiredService<IRecordingStore>());
        Assert.IsType<ApplicationStoreTypedControlStore>(provider.GetRequiredService<ITypedControlStore>());
    }

    [Fact]
    public async Task TransportBroker_With_Failover_Does_Not_Recurse_For_Sourceless_Device()
    {
        // Device has an IP (so the broker's failover branch is eligible) but no transport
        // adapter yields sources. Without the AsyncLocal reentrancy guard the call chain
        // broker → failover.ResolveBestSourceAsync → broker.GetSourcesAsync → … would
        // recurse until the stack overflows.
        var device = new BossCam.Contracts.DeviceIdentity
        {
            Id = Guid.NewGuid(),
            IpAddress = "192.0.2.1", // RFC 5737 TEST-NET-1: never routed, connects fail fast
            Port = 80,
            Name = "sourceless"
        };

        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-di-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
            await store.InitializeAsync(CancellationToken.None);
            await store.UpsertDevicesAsync([device], CancellationToken.None);

            TransportBroker? broker = null;
            TransportFailoverService? failover = null;
            broker = new TransportBroker(
                [],
                store,
                new ServiceProviderStub(() => failover),
                NullLogger<TransportBroker>.Instance);
            failover = new TransportFailoverService(
                store,
                broker,
                new StubHttpClientFactory(),
                NullLogger<TransportFailoverService>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var sources = await broker.GetSourcesAsync(device.Id, cts.Token);

            // Must terminate with an empty source list — never stack-overflow or hang.
            Assert.Empty(sources);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    private static IConfiguration BuildTestConfig()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"bosscam-di-cfg-{Guid.NewGuid():N}");
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BossCam:DatabasePath"] = Path.Combine(temp, "di.db"),
                ["BossCam:StorageRoot"] = Path.Combine(temp, "recordings"),
                ["BossCam:FirmwareArtifactDirectory"] = Path.Combine(temp, "firmware"),
                ["BossCam:IpcamSuiteDirectory"] = string.Empty,
                ["BossCam:EseeCloudDirectory"] = string.Empty,
                ["BossCam:EseeCloudDataDirectory"] = Path.Combine(temp, "esee"),
                ["BossCam:LanAuthToken"] = "di-test-token",
                ["BossCam:RateLimitEnabled"] = "false"
            })
            .Build();
    }

    private sealed class ServiceProviderStub(Func<object?> resolver) : IServiceProvider
    {
        public object? GetService(Type serviceType) => resolver();
    }

    private sealed class HostEnvironmentStub(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "BossCam.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        // IHostEnvironment declares ContentRootFileProvider with { get; set; }, so both
        // accessors must be implemented; a default NullFileProvider instance is sufficient.
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name)
            => new() { Timeout = TimeSpan.FromSeconds(3) };
    }
}
