using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Control;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for LAN-only / air-gapped operation (<c>BossCam:OfflineMode=true</c> or
/// <c>BOSSCAM_OFFLINE=1</c>): cloud/P2P transport adapters (ESEE/Juan, KP2P, LinkVision) must emit
/// no sources, and the remote-command relay must not claim devices — while LAN adapters keep
/// working. Without these guards an offline deployment would try to tunnel through the internet
/// to vendor brokers and stall every stream/settings resolution.
/// </summary>
public sealed class OfflineModeTests
{
    private static DeviceIdentity JuanDevice() => new()
    {
        Id = Guid.NewGuid(),
        Name = "5523-W offline test",
        IpAddress = "10.0.0.29",
        Port = 80,
        LoginName = "admin",
        EseeId = "juan-0001",
        TransportProfiles = []
    };

    [Fact]
    public async Task EseeJuanP2P_Adapter_Emits_No_Sources_When_OfflineMode()
    {
        var adapter = new EseeJuanP2PAdapter(Options.Create(new BossCamRuntimeOptions { OfflineMode = true }));
        var sources = await adapter.GetSourcesAsync(JuanDevice(), CancellationToken.None);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task EseeJuanP2P_Adapter_Emits_Sources_When_Online()
    {
        var adapter = new EseeJuanP2PAdapter(Options.Create(new BossCamRuntimeOptions { OfflineMode = false }));
        var sources = await adapter.GetSourcesAsync(JuanDevice(), CancellationToken.None);
        var source = Assert.Single(sources);
        Assert.Equal(TransportKind.EseeJuanP2P, source.Kind);
    }

    [Fact]
    public async Task Kp2p_Adapter_Emits_No_Sources_When_OfflineMode()
    {
        var adapter = new Kp2pAdapter(Options.Create(new BossCamRuntimeOptions { OfflineMode = true }));
        var sources = await adapter.GetSourcesAsync(JuanDevice(), CancellationToken.None);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task LinkVision_Adapter_Emits_No_Sources_When_OfflineMode()
    {
        var adapter = new LinkVisionAdapter(Options.Create(new BossCamRuntimeOptions { OfflineMode = true }));
        var sources = await adapter.GetSourcesAsync(JuanDevice(), CancellationToken.None);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task RemoteCommand_Adapter_Does_Not_Claim_Devices_When_OfflineMode()
    {
        var adapter = new OwnedRemoteCommandAdapter(
            Options.Create(new BossCamRuntimeOptions { OfflineMode = true }),
            new NeverHttpClientFactory(),
            null!,
            NullLogger<OwnedRemoteCommandAdapter>.Instance);
        var canHandle = await adapter.CanHandleAsync(JuanDevice(), CancellationToken.None);
        Assert.False(canHandle);
    }

    [Fact]
    public async Task RemoteCommand_Adapter_Claims_Devices_When_Online()
    {
        var adapter = new OwnedRemoteCommandAdapter(
            Options.Create(new BossCamRuntimeOptions { OfflineMode = false }),
            new NeverHttpClientFactory(),
            null!,
            NullLogger<OwnedRemoteCommandAdapter>.Instance);
        var canHandle = await adapter.CanHandleAsync(JuanDevice(), CancellationToken.None);
        Assert.True(canHandle);
    }

    /// <summary>Factory that must never be asked for a client in these tests.</summary>
    private sealed class NeverHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException($"Unexpected client creation: {name}");
    }
}
