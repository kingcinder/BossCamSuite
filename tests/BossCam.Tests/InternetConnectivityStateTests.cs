using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Keeps transient WAN loss separate from LAN camera operation: cloud adapters gate only after
/// the configured failure threshold and become available again immediately after a successful
/// probe, while explicit OfflineMode remains an unconditional policy override.
/// </summary>
public sealed class InternetConnectivityStateTests
{
    [Fact]
    public void State_Requires_Two_Failures_Before_Gating_Cloud_Transports()
    {
        var state = new InternetConnectivityState();

        Assert.Equal(InternetConnectivityStatus.Unknown, state.Status);
        Assert.True(state.AllowsInternetTransports);

        state.ApplyProbeResult(false, failureThreshold: 2);
        Assert.Equal(InternetConnectivityStatus.Unknown, state.Status);
        Assert.True(state.AllowsInternetTransports);

        state.ApplyProbeResult(false, failureThreshold: 2);
        Assert.Equal(InternetConnectivityStatus.Offline, state.Status);
        Assert.False(state.AllowsInternetTransports);
    }

    [Fact]
    public void State_Restores_Cloud_Transports_On_First_Successful_Probe()
    {
        var state = new InternetConnectivityState();
        state.ApplyProbeResult(false, failureThreshold: 1);
        Assert.Equal(InternetConnectivityStatus.Offline, state.Status);

        state.ApplyProbeResult(true, failureThreshold: 2);

        Assert.Equal(InternetConnectivityStatus.Online, state.Status);
        Assert.True(state.AllowsInternetTransports);
    }

    [Fact]
    public async Task Cloud_Adapters_Gate_And_Reopen_Without_Recreating_Them()
    {
        var state = new InternetConnectivityState();
        var options = Options.Create(new BossCamRuntimeOptions { OfflineMode = false });
        var device = new DeviceIdentity { EseeId = "esee-1", IpAddress = "192.0.2.10" };
        var adapter = new EseeJuanP2PAdapter(options, state);

        Assert.Single(await adapter.GetSourcesAsync(device, CancellationToken.None));

        state.ApplyProbeResult(false, failureThreshold: 1);
        Assert.Empty(await adapter.GetSourcesAsync(device, CancellationToken.None));

        state.ApplyProbeResult(true, failureThreshold: 1);
        var restored = await adapter.GetSourcesAsync(device, CancellationToken.None);
        Assert.Single(restored);
        Assert.Equal(TransportKind.EseeJuanP2P, restored.Single().Kind);
    }

    [Fact]
    public async Task Explicit_OfflineMode_Still_Wins_Over_Automatic_Wan_Recovery()
    {
        var state = new InternetConnectivityState();
        state.ApplyProbeResult(true);
        var options = Options.Create(new BossCamRuntimeOptions { OfflineMode = true });
        var device = new DeviceIdentity { EseeId = "esee-1", IpAddress = "192.0.2.10" };
        var adapter = new EseeJuanP2PAdapter(options, state);

        Assert.Empty(await adapter.GetSourcesAsync(device, CancellationToken.None));
    }
}
