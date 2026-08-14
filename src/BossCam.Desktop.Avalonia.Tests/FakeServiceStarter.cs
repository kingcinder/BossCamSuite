using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Test double for <see cref="IBossCamServiceStarter"/>. Returns the configured
/// result and records invocation so ViewModel tests can assert the startup
/// handshake only attempts a service start when the health check failed.
/// </summary>
public sealed class FakeServiceStarter : IBossCamServiceStarter
{
    /// <summary>Value returned from TryStartAsync. Default false = "could not start".</summary>
    public bool StartResult { get; set; }

    /// <summary>Number of TryStartAsync invocations.</summary>
    public int StartCallCount { get; private set; }

    /// <summary>The health predicate passed by the handshake, for introspection.</summary>
    public Func<Task<bool>>? LastHealthPredicate { get; private set; }

    /// <summary>
    /// When set, TryStartAsync returns this task instead of <see cref="StartResult"/>.
    /// Lets tests hold a handshake genuinely in-flight (e.g. a TaskCompletionSource).
    /// </summary>
    public Task<bool>? PendingTask { get; set; }

    public bool Disposed { get; private set; }

    public Task<bool> TryStartAsync(Func<Task<bool>> isHealthy, CancellationToken cancellationToken = default)
    {
        StartCallCount++;
        LastHealthPredicate = isHealthy;
        return PendingTask ?? Task.FromResult(StartResult);
    }

    public void Dispose() => Disposed = true;
}
