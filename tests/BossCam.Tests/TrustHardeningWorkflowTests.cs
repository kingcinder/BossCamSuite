using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class TrustHardeningWorkflowTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-trust-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public TrustHardeningWorkflowTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task DiscoveryCoordinator_Merges_By_Ip_Prefers_Ipc_And_Stabilizes_Id()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var existing = new DeviceIdentity
        {
            Name = "ONVIF 10.0.0.4",
            DeviceType = "ONVIF",
            IpAddress = "10.0.0.4"
        };
        await store.UpsertDevicesAsync([existing], CancellationToken.None);

        var discoveredIpc = new DeviceIdentity
        {
            Name = "5523-W",
            DeviceType = "IPC",
            IpAddress = "10.0.0.4",
            LoginName = "admin",
            Password = "secret"
        };

        var coordinator = new DiscoveryCoordinator(
            [new StubDiscoveryProvider(discoveredIpc)],
            [new StubImportProvider()],
            store,
            NullLogger<DiscoveryCoordinator>.Instance);

        _ = await coordinator.RunAsync(CancellationToken.None);
        var devices = await store.GetDevicesAsync(CancellationToken.None);
        var ipMatches = devices.Where(device => device.IpAddress == "10.0.0.4").ToList();

        Assert.Single(ipMatches);
        Assert.Equal("IPC", ipMatches[0].DeviceType);
        Assert.Equal("admin", ipMatches[0].LoginName);
        Assert.Equal(existing.Id, ipMatches[0].Id);
    }

    [Fact]
    public async Task PersistenceVerification_Classifies_Clamped_Immediate_Status()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Name = "5523-W",
            DeviceType = "IPC",
            IpAddress = "10.0.0.4",
            LoginName = "admin",
            Password = "pw"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new SequenceAdapter(
            new WriteResult { Success = true, AdapterName = "Seq", Response = new JsonObject { ["bitrate"] = 1024 } }, // pre GET
            new WriteResult { Success = true, AdapterName = "Seq", Response = new JsonObject { ["ret"] = "ok" } }, // write
            new WriteResult { Success = true, AdapterName = "Seq", Response = new JsonObject { ["bitrate"] = 900 } }  // post GET
        );
        var service = new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance);

        var result = await service.VerifyAsync(new PersistenceVerificationRequest
        {
            DeviceId = device.Id,
            AdapterName = "Seq",
            Endpoint = "/NetSDK/Video/input/channel/0",
            Method = "PUT",
            Payload = new JsonObject { ["bitrate"] = 950 },
            FieldKey = "bitrate",
            FieldSourcePath = "$.bitrate",
            IntendedValue = JsonValue.Create(950),
            RebootForVerification = false
        }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SemanticWriteStatus.AcceptedClamped, result!.ImmediateStatus);
        Assert.Equal(SemanticWriteStatus.PersistedAfterDelay, result.PersistenceStatus);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_tempDirectory, true);
        }
        catch
        {
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private sealed class StubDiscoveryProvider(DeviceIdentity device) : IDiscoveryProvider
    {
        public string Name => "StubDiscovery";
        public Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<DeviceIdentity>>([device]);
    }

    private sealed class StubImportProvider : IDeviceImportProvider
    {
        public string Name => "StubImport";
        public Task<IReadOnlyCollection<DeviceIdentity>> ImportAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<DeviceIdentity>>([]);
    }

    private sealed class SequenceAdapter(params WriteResult[] results) : IControlAdapter
    {
        private readonly Queue<WriteResult> _results = new(results);

        public string Name => "Seq";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;
        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });
        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name });
        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken) => ReadAsync(device, cancellationToken);

        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
        {
            if (_results.Count == 0)
            {
                return Task.FromResult(new WriteResult { Success = false, AdapterName = Name, Message = "No queued result." });
            }

            return Task.FromResult(_results.Dequeue());
        }

        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }
}

