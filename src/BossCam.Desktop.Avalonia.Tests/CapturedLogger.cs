using Microsoft.Extensions.Logging;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// In-memory <see cref="ILogger{T}"/> fake that records (level, message) tuples so
/// tests can assert the Avalonia app's logging wiring actually fires on failures.
/// </summary>
internal sealed class CapturedLogger<T> : ILogger<T>
{
    // ConcurrentQueue (not List): the SUT's LogDebug calls run in catch blocks after awaits and,
    // with no SynchronizationContext in unit tests, can execute on threadpool threads. A plain
    // List is not safe for concurrent Add + enumerate; ConcurrentQueue keeps both the write and
    // the Assert.Single/Assert.Contains reads race-free.
    public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception)));
}
