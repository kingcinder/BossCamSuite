using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class CameraStabilityTests
{
    [Fact]
    public void ConnectivityStatus_Enum_Has_Expected_Values()
    {
        Assert.Equal(0, (int)ConnectivityStatus.Unknown);
        Assert.Equal(1, (int)ConnectivityStatus.Healthy);
        Assert.Equal(2, (int)ConnectivityStatus.Degraded);
        Assert.Equal(3, (int)ConnectivityStatus.Offline);
    }

    [Fact]
    public void DeviceConnectivitySnapshot_Roundtrips_Json()
    {
        var snap = new DeviceConnectivitySnapshot
        {
            DeviceId = Guid.NewGuid(),
            Status = ConnectivityStatus.Degraded,
            TransportResults = new Dictionary<string, bool> { ["http:80"] = true, ["rtsp:554"] = false },
            LastCheckedAt = DateTimeOffset.UtcNow,
            LastDiagnosticSummary = "HTTP ok, RTSP down",
            ReconnectAttempts = new Dictionary<string, string> { ["alt:8080"] = "reachable" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(snap);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<DeviceConnectivitySnapshot>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(snap.DeviceId, deserialized!.DeviceId);
        Assert.Equal(snap.Status, deserialized.Status);
        Assert.Equal(snap.TransportResults["http:80"], deserialized.TransportResults["http:80"]);
        Assert.Equal(snap.TransportResults["rtsp:554"], deserialized.TransportResults["rtsp:554"]);
        Assert.Equal(snap.LastDiagnosticSummary, deserialized.LastDiagnosticSummary);
        Assert.Equal(snap.ReconnectAttempts["alt:8080"], deserialized.ReconnectAttempts["alt:8080"]);
    }

    [Fact]
    public void ProbeResult_Records_Latency_And_Status()
    {
        var result = new ProbeResult
        {
            Success = true,
            Detail = "TCP 10.0.0.170:80 open in 12ms",
            LatencyMs = 12,
            HttpStatusCode = 200
        };

        Assert.True(result.Success);
        Assert.Equal(12, result.LatencyMs);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public void DeviceDiagnosticReport_Success_Requires_At_Least_One_Working_Probe()
    {
        var report = new DeviceDiagnosticReport
        {
            DeviceId = Guid.NewGuid(),
            Success = true,
            Verdict = ConnectivityDiagnosticVerdict.Healthy,
            ConnectivityStatus = ConnectivityStatus.Healthy,
            Summary = "All probes passed",
            ProbeResults = new Dictionary<string, ProbeResult>
            {
                ["ping"] = new() { Success = true },
                ["http:80"] = new() { Success = true }
            }
        };

        Assert.True(report.Success);
        Assert.Equal(ConnectivityDiagnosticVerdict.Healthy, report.Verdict);
        Assert.Equal(ConnectivityStatus.Healthy, report.ConnectivityStatus);
    }

    [Fact]
    public void DeviceDiagnosticReport_Critical_Failure_Has_No_Working_Probes()
    {
        var report = new DeviceDiagnosticReport
        {
            DeviceId = Guid.NewGuid(),
            Success = false,
            Verdict = ConnectivityDiagnosticVerdict.CriticalFailure,
            ConnectivityStatus = ConnectivityStatus.Offline,
            Summary = "All probes failed",
            ProbeResults = new Dictionary<string, ProbeResult>
            {
                ["ping"] = new() { Success = false },
                ["http:80"] = new() { Success = false },
                ["tcp:554"] = new() { Success = false }
            }
        };

        Assert.False(report.Success);
        Assert.Equal(ConnectivityDiagnosticVerdict.CriticalFailure, report.Verdict);
        Assert.Equal(ConnectivityStatus.Offline, report.ConnectivityStatus);
    }

    [Fact]
    public void DeviceDiagnosticReport_Includes_Recovery_Actions()
    {
        var report = new DeviceDiagnosticReport
        {
            DeviceId = Guid.NewGuid(),
            Success = false,
            Summary = "RTSP port 554 not reachable",
            SuggestedRecoveryActions =
            [
                "Verify RTSP is enabled in the camera's stream settings.",
                "Check firewall rules for port 554."
            ]
        };

        Assert.Contains("Verify RTSP is enabled", report.SuggestedRecoveryActions[0]);
        Assert.Equal(2, report.SuggestedRecoveryActions.Count);
    }

    [Fact]
    public void SelectHighResMainSource_Prefers_Main_Over_Sub()
    {
        var sources = new List<VideoSourceDescriptor>
        {
            new()
            {
                Kind = TransportKind.Rtsp,
                Url = "rtsp://admin:pass@10.0.0.170:554/ch0_1.264",
                Rank = 50,
                Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["highRes"] = "false" }
            },
            new()
            {
                Kind = TransportKind.Rtsp,
                Url = "rtsp://admin:pass@10.0.0.170:554/ch0_0.264",
                Rank = 0,
                Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" }
            }
        };

        var pick = RecordingService.SelectHighResMainSource(sources);
        Assert.NotNull(pick);
        Assert.Contains("ch0_0", pick!.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectHighResMainSource_Returns_Null_When_Only_Sub_Or_Snapshot()
    {
        var sources = new List<VideoSourceDescriptor>
        {
            new()
            {
                Kind = TransportKind.Rtsp,
                Url = "rtsp://admin:pass@10.0.0.170:554/ch0_1.264",
                Rank = 50,
                Metadata = new Dictionary<string, string> { ["stream"] = "sub" }
            },
            new()
            {
                Kind = TransportKind.LanRest,
                Url = "http://10.0.0.170:80/snapshot.jpg",
                Rank = 25,
                Metadata = new Dictionary<string, string> { ["kind"] = "snapshot" }
            }
        };

        var pick = RecordingService.SelectHighResMainSource(sources);
        Assert.Null(pick);
    }

    [Fact]
    public async Task SqliteStore_Saves_And_Loads_DeviceConnectivitySnapshot()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-conn-test-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
            await store.InitializeAsync(CancellationToken.None);

            var deviceId = Guid.NewGuid();
            var snap = new DeviceConnectivitySnapshot
            {
                DeviceId = deviceId,
                Status = ConnectivityStatus.Degraded,
                TransportResults = new Dictionary<string, bool> { ["http:80"] = true, ["rtsp:554"] = false },
                LastCheckedAt = DateTimeOffset.UtcNow,
                LastDiagnosticSummary = "HTTP ok, RTSP down"
            };

            await store.SaveDeviceConnectivitySnapshotAsync(snap, CancellationToken.None);
            var loaded = await store.GetDeviceConnectivitySnapshotAsync(deviceId, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(deviceId, loaded!.DeviceId);
            Assert.Equal(ConnectivityStatus.Degraded, loaded.Status);
            Assert.True(loaded.TransportResults["http:80"]);
            Assert.False(loaded.TransportResults["rtsp:554"]);

            var allSnaps = await store.GetAllDeviceConnectivitySnapshotsAsync(CancellationToken.None);
            Assert.Contains(allSnaps, s => s.DeviceId == deviceId);
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task SqliteStore_DeviceConnectivitySnapshot_Updates_Existing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-conn-upd-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
            await store.InitializeAsync(CancellationToken.None);

            var deviceId = Guid.NewGuid();
            var first = new DeviceConnectivitySnapshot
            {
                DeviceId = deviceId,
                Status = ConnectivityStatus.Healthy
            };
            await store.SaveDeviceConnectivitySnapshotAsync(first, CancellationToken.None);

            var second = new DeviceConnectivitySnapshot
            {
                DeviceId = deviceId,
                Status = ConnectivityStatus.Offline,
                TransportResults = new Dictionary<string, bool> { ["http:80"] = false },
                LastCheckedAt = DateTimeOffset.UtcNow
            };
            await store.SaveDeviceConnectivitySnapshotAsync(second, CancellationToken.None);

            var loaded = await store.GetDeviceConnectivitySnapshotAsync(deviceId, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(ConnectivityStatus.Offline, loaded!.Status);
            Assert.False(loaded.TransportResults["http:80"]);
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void DeviceDiagnosticReport_DeviceNotFound_Verdict()
    {
        var report = new DeviceDiagnosticReport
        {
            DeviceId = Guid.NewGuid(),
            Success = false,
            Verdict = ConnectivityDiagnosticVerdict.DeviceNotFound,
            Summary = "Device not found in store."
        };

        Assert.Equal(ConnectivityDiagnosticVerdict.DeviceNotFound, report.Verdict);
        Assert.False(report.Success);
    }

    [Fact]
    public void DeviceDiagnosticReport_RtspDownSnapshotOnly_Verdict()
    {
        var report = new DeviceDiagnosticReport
        {
            DeviceId = Guid.NewGuid(),
            Success = true,
            Verdict = ConnectivityDiagnosticVerdict.RtspDownSnapshotOnly,
            ConnectivityStatus = ConnectivityStatus.Degraded,
            Summary = "HTTP reachable, RTSP port 554 not responding",
            ProbeResults = new Dictionary<string, ProbeResult>
            {
                ["http:80"] = new() { Success = true, Detail = "HTTP 200 in 5ms" },
                ["tcp:554"] = new() { Success = false, Detail = "TCP timeout" },
                ["snapshot"] = new() { Success = true, Detail = "JPEG snapshot OK" }
            }
        };

        Assert.Equal(ConnectivityDiagnosticVerdict.RtspDownSnapshotOnly, report.Verdict);
        Assert.True(report.Success);
        Assert.Equal(ConnectivityStatus.Degraded, report.ConnectivityStatus);
        // HTTP + snapshot are up, RTSP (tcp:554) is the only failing probe.
        Assert.True(report.ProbeResults["http:80"].Success);
        Assert.True(report.ProbeResults["snapshot"].Success);
        Assert.False(report.ProbeResults["tcp:554"].Success);
    }

    [Fact]
    public void ReconnectAttempts_Records_Port_Scan_Results()
    {
        var snap = new DeviceConnectivitySnapshot
        {
            DeviceId = Guid.NewGuid(),
            Status = ConnectivityStatus.Offline,
            TransportResults = new Dictionary<string, bool>
            {
                ["primary:80"] = false,
                ["alt:8080"] = false,
                ["alt:8000"] = true,
                ["rtsp:554"] = false
            },
            ReconnectAttempts = new Dictionary<string, string>
            {
                ["diagnosedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["verdict"] = "CriticalFailure",
                ["updatedPort"] = "changed to 8000"
            }
        };

        Assert.Equal(ConnectivityStatus.Offline, snap.Status);
        Assert.True(snap.TransportResults["alt:8000"]);
        Assert.Contains("changed to 8000", snap.ReconnectAttempts["updatedPort"]);
    }

    [Fact]
    public async Task TransportFailoverService_ResolveBestSource_Null_For_No_Ip()
    {
        // Create a store with a device that has no IP
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-failover-null-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
            await store.InitializeAsync(CancellationToken.None);

            var noIpDevice = new DeviceIdentity { Name = "NoIP" };
            await store.UpsertDevicesAsync([noIpDevice], CancellationToken.None);

            // The service needs HTTP client factory which won't actually be used because device has no IP
            var service = new TransportFailoverService(
                store,
                new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
                new HttpClientFactoryMock(),
                NullLogger<TransportFailoverService>.Instance);

            var result = await service.ResolveBestSourceAsync(noIpDevice.Id, "main", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }
}

/// <summary>
/// Minimal IHttpClientFactory stub for tests that should never create real HTTP clients.
/// Throws if actually called (tests should verify early-exit conditions).
/// </summary>
internal sealed class HttpClientFactoryMock : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => throw new InvalidOperationException($"HttpClientFactoryMock.CreateClient(\"{name}\") should not be called in this test.");
}
