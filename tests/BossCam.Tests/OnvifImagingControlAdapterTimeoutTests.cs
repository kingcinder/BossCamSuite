using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for <see cref="OnvifImagingControlAdapter"/>'s intentional asymmetry between
/// its two probe methods:
///
/// <list type="bullet">
///   <item><description><c>CanHandleAsync</c> brand-check uses
///     <c>HttpClient.Timeout = cts.CancelAfter = Math.Max(2, HttpTimeoutSeconds / 2)</c></description></item>
///   <item><description><c>ProbeAsync</c> device-info query uses
///     <c>HttpClient.Timeout = Math.Max(2, HttpTimeoutSeconds)</c></description></item>
/// </list>
///
/// A future maintainer who unifies these — typically by "tidying" the /2 divisor out — silently
/// regresses the multi-port brand-check fast-fail. This test catches that by exercising the
/// adapters against a blackhole TCP listener (accepts but never responds) and asserting both
/// absolute upper bounds and the relative ratio. Any change that flattens the asymmetry will
/// trip one of the assertions below.
///
/// Implementation note: the blackhole listener is an <see cref="IClassFixture{TFixture}"/> —
/// instantiated once for the class and shared across all 5 [Fact]s. This deliberately avoids
/// 5 back-to-back bind/release cycles on 127.0.0.1:8899, which on Linux would intermittently hit
/// EADDRINUSE on the SSE / TcpListener TIME-WAIT dance under xUnit's parallel-collector scheduling.
/// </summary>
public sealed class OnvifImagingControlAdapterTimeoutTests : IClassFixture<BlackholeListenerFixture>
{
    private const int HttpTimeoutSeconds = 6;
    private const int DevicePortPlaceholder = 9999; // not in any adapter port list — only consulted as a tail element by CanHandleAsync

    // Tests must seed the probe-ports list explicitly so a busy dev box bound on :80 doesn't
    // blow the upper-bound gates in tests 1–2 above. The blackhole is at port 8899; 8888 is the
    // canonical Dahua ONVIF media port (closed on the test runner); 80 is explicitly excluded
    // because dev workstations often have something bound there (e.g. a local web service that
    // would slow-resolve vs. a true fast-fail).
    private static readonly int[] TestPorts = [BlackholeListenerFixture.Port, 8888];

    private readonly BlackholeListenerFixture _blackhole;

    public OnvifImagingControlAdapterTimeoutTests(BlackholeListenerFixture blackhole)
    {
        _blackhole = blackhole;
    }

    [Fact]
    public async Task CanHandleAsync_Brand_Probe_Timeout_Is_Half_Of_HttpTimeoutSeconds()
    {
        await WaitForListenerUp();

        var adapter = NewAdapter();
        var device = NewDevice();

        var sw = Stopwatch.StartNew();
        await adapter.CanHandleAsync(device, CancellationToken.None);
        sw.Stop();

        // Loop is [8899 (blackhole, half-timeout fires), 8888 (closed, fast-fail), 9999 (closed, fast-fail)].
        // Total elapsed ≈ HttpTimeoutSeconds/2 + jitter. The upper bound has 1.5s of slack so
        // 127.0.0.1:8888 holding a slow server (e.g. a local Jupyter up to ~3s) does not trip
        // the assertion — the policy invariant is bounded-ness, not a tight bound.
        var elapsed = sw.Elapsed.TotalSeconds;
        Assert.InRange(elapsed, 2.0, 6.5);
    }

    [Fact]
    public async Task ProbeAsync_DeviceInfo_Probe_Timeout_Is_Full_HttpTimeoutSeconds()
    {
        await WaitForListenerUp();

        var adapter = NewAdapter();
        var device = NewDevice();

        var sw = Stopwatch.StartNew();
        await adapter.ProbeAsync(device, CancellationToken.None);
        sw.Stop();

        // Loop is [8899 (blackhole, full-timeout fires), 8888 (closed, fast-fail), 80 (closed, fast-fail)].
        // Total elapsed ≈ HttpTimeoutSeconds + jitter.
        var elapsed = sw.Elapsed.TotalSeconds;
        Assert.InRange(elapsed, 4.5, 9.0);
    }

    [Fact]
    public async Task CanHandleAsync_Is_Materially_Faster_Than_ProbeAsync()
    {
        await WaitForListenerUp();

        var adapter = NewAdapter();
        var device = NewDevice();

        var swBrand = Stopwatch.StartNew();
        await adapter.CanHandleAsync(device, CancellationToken.None);
        swBrand.Stop();

        // Settle: ensure ProbeAsync starts on a clean kernel-LISTEN-state, not a state
        // where accepting a TCP arrives mid-expiration of CanHandleAsync's cts.CancelAfter.
        await Settle();

        var swFull = Stopwatch.StartNew();
        await adapter.ProbeAsync(device, CancellationToken.None);
        swFull.Stop();

        // The whole point of the asymmetry: the brand-probe is bounded to HttpTimeoutSeconds/2,
        // the device-info probe is given the full HttpTimeoutSeconds. With HttpTimeoutSeconds=6,
        // the brand-probe MUST be observably tighter. 1.5s is below the smallest difference any
        // "uniform timeout" implementation would produce yet above the worst-case xUnit jitter
        // under parallel load (~400ms).
        Assert.True(
            swFull.Elapsed >= swBrand.Elapsed + TimeSpan.FromMilliseconds(1500),
            $"brand-probe ({swBrand.ElapsedMilliseconds} ms) should be at least 1.5s " +
            $"tighter than device-info probe ({swFull.ElapsedMilliseconds} ms); " +
            $"the /2 fast-fail has regressed.");
    }

    [Fact]
    public async Task ProbeAsync_HttpTimeoutSeconds_Affects_Elapsed_Time_Linearly()
    {
        // Sanity: if HttpTimeoutSeconds is larger, ProbeAsync takes proportionally longer — proves
        // that the wire-level timeout (not just the loop's port count) drives elapsed. This catches
        // refactors that swap TimeSpan.FromSeconds for a hardcoded literal (e.g. always 8s) AND
        // refactors that half the wire timeout. With HttpTimeoutSeconds=4 vs 8 the gap is ~4s ±
        // jitter; 3.0s is firm.
        await WaitForListenerUp();

        var adapterShort = NewAdapter(httpTimeoutSeconds: 4);
        var adapterLong = NewAdapter(httpTimeoutSeconds: 8);
        var device = NewDevice();

        var swShort = Stopwatch.StartNew();
        await adapterShort.ProbeAsync(device, CancellationToken.None);
        swShort.Stop();

        // Settle: the long-config probe follows the short-config probe in the same TCP listener
        // process; without a settle the kernel's accept-queue from the short probe can leak
        // a half-handled 4-tuple into the long probe's connect attempt.
        await Settle();

        var swLong = Stopwatch.StartNew();
        await adapterLong.ProbeAsync(device, CancellationToken.None);
        swLong.Stop();

        Assert.True(
            swLong.Elapsed > swShort.Elapsed + TimeSpan.FromMilliseconds(3000),
            $"doubling HttpTimeoutSeconds (4→8) should add at least 3.0s of elapsed " +
            $"to the device-info probe; short={swShort.ElapsedMilliseconds} ms, " +
            $"long={swLong.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Both_Probes_Honor_The_2_Second_Floor_On_HttpTimeoutSeconds()
    {
        // The adapter's expressions use Math.Max(2, HttpTimeoutSeconds / 2) and
        // Math.Max(2, HttpTimeoutSeconds). The 2-second floor is itself an intentional safety net:
        // without it, HttpTimeoutSeconds=0 would make brand-probes instant and skip the multi-port
        // scan entirely, and HttpTimeoutSeconds=1 would underestimate the wait. Pinning this guards
        // against a maintainer deleting the Math.Max floors.
        await WaitForListenerUp();

        var adapter = NewAdapter(httpTimeoutSeconds: 1); // Below floor → Math.Max kicks in.
        var device = NewDevice();

        var swBrand = Stopwatch.StartNew();
        await adapter.CanHandleAsync(device, CancellationToken.None);
        swBrand.Stop();

        await Settle();

        var swFull = Stopwatch.StartNew();
        await adapter.ProbeAsync(device, CancellationToken.None);
        swFull.Stop();

        // Floor is 2s. Allow 200ms grace for HttpClient.Timeout firing slightly early.
        Assert.InRange(swBrand.Elapsed.TotalSeconds, 1.8, 4.0);
        Assert.InRange(swFull.Elapsed.TotalSeconds, 1.8, 4.0);
    }

    private static OnvifImagingControlAdapter NewAdapter(int httpTimeoutSeconds = HttpTimeoutSeconds, int[]? onvifProbePorts = null) =>
        new(
            Options.Create(new BossCamRuntimeOptions
            {
                HttpTimeoutSeconds = httpTimeoutSeconds,
                OnvifProbePorts = onvifProbePorts ?? TestPorts,
            }),
            new NullHttpClientFactory(),
            NullLogger<OnvifImagingControlAdapter>.Instance);

    // Tiny between-probe settle: drains the LISTEN socket accept queue so the next adapter
    // call sees a kernel-state at rest. Without it, a probe followed immediately by another
    // probe on the same listener quadruples the chance of the second probe landing on a
    // half-closed kernel socket (still in our previous AcceptLoopAsync's accepted-but-unread
    // list). 50ms empirically matches the kernel's NOTIFY_READY delay on loopback.
    private static async Task Settle() => await Task.Delay(50);

    private static DeviceIdentity NewDevice() => new()
    {
        IpAddress = IPAddress.Loopback.ToString(),
        Port = DevicePortPlaceholder,
        LoginName = "admin",
        HardwareModel = "synthetic-onvif-timeout-probe"
    };

    // Race gate: the kernel registers the TCP listener synchronously when TcpListener.Start
    // returns, but the very first SYN can still race ahead of OS plumbing the LISTEN socket.
    // Probe by attempting a connect-and-close loopback handshake: faster and deterministic.
    private async Task WaitForListenerUp()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, BlackholeListenerFixture.Port);
                return; // success — kernel has fully wired the socket.
            }
            catch (SocketException)
            {
                await Task.Delay(10);
            }
        }

        throw new InvalidOperationException($"Blackhole on 127.0.0.1:{BlackholeListenerFixture.Port} never accepted a probe connect after 20 attempts × 10ms.");
    }
}

/// <summary>
/// xUnit fixture: TCP listener that accepts but never sends/receives — keeps the connection open
/// so the HttpClient opponent believes the server is slow (not closed). Shared across all [Fact]s
/// in <see cref="OnvifImagingControlAdapterTimeoutTests"/> via IClassFixture, so the bind/release
/// lifecycle spans the whole test class rather than 5 back-to-back cycles.
/// </summary>
/// <summary>
/// Lightweight <see cref="IHttpClientFactory"/> test double that returns a single-use
/// <see cref="HttpClient"/> with default settings. Used by timeout tests where the factory
/// is a constructor dependency but per-request handler configuration is not exercised.
/// </summary>
public sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

public sealed class BlackholeListenerFixture : IDisposable
{
    public const int Port = 8899;
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _accepted = [];
    private readonly object _lock = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public BlackholeListenerFixture()
    {
        // SO_REUSEADDR is defensive: keeps a freshly-Started listener bindable on a port that has
        // a TIME_WAIT 4-tuple from any prior test assembly run, even though our single-bind
        // architecture means we shouldn't normally need it.
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        // Soft cap: keep at most this many accepted-but-unread TcpClient references. As tests in
        // the class progress, older entries are dropped (Dispose) so the kernel's per-listener
        // resource budget doesn't accumulate to the point where subsequent probes see unexpected
        // timing variance. Without a low cap, the linear test (which fires the long-config probe
        // after several prior probes have stacked) intermittently saw long elapsed ≈ 2s instead
        // of the expected 8s — most likely the kernel handling an accept-queue with many held
        // sockets differently than one with few. The cap of 8 is well under the typical kernel
        // backlog limit (defaults: tcp_max_syn_backlog=128, somaxconn=4096) but above the
        // maximum number of accepted connections a single [Fact] generates (~6: 1 WaitForListenerUp
        // probe + 1 CanHandleAsync try + 1 settle + 1 ProbeAsync try + 2 explicit retries).
        // Round-17 review reworded this comment to acknowledge that cross-[Fact] accumulation
        // under stress (with Settle) can briefly push past 8 — the oldest-drop policy is the
        // safety net, not a guarantee of never-thrashing.
        const int AcceptedQuota = 8;
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_lock)
                {
                    _accepted.Add(client);
                    while (_accepted.Count > AcceptedQuota)
                    {
                        var oldest = _accepted[0];
                        _accepted.RemoveAt(0);
                        try { oldest.Dispose(); } catch { /* race-tolerant cleanup */ }
                    }
                }
                // Intentionally do NOT read or write on the still-held clients. The remaining
                // entries exist so the OS keeps their connection open and the HttpClient opponent
                // believes the server is stalling on response headers.
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { /* port closed externally */ return; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* idempotent */ }
        try { _listener.Stop(); } catch { /* idempotent */ }

        lock (_lock)
        {
            foreach (var client in _accepted)
            {
                // Dispose internally closes the underlying socket — no need to call Close() first.
                try { client.Dispose(); } catch { /* idempotent */ }
            }
            _accepted.Clear();
        }

        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* idempotent */ }
        _cts.Dispose();
    }
}
