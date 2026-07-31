using Microsoft.Extensions.Logging;

namespace BossCam.Tests;

/// <summary>
/// In-memory <see cref="ILogger{T}"/> fake that records every entry as a
/// (level, message) tuple so tests can assert that a specific log level and
/// message fired (used by the silent-catch logging tests).
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// Non-generic <see cref="ILogger"/> twin of <see cref="ListLogger{T}"/> for targets that
/// take a plain <see cref="ILogger"/> (e.g. static endpoint classes like
/// <c>ApiStorageEndpoints</c>, which cannot be used as a generic type argument).
/// </summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
