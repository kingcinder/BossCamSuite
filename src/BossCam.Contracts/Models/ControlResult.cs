namespace BossCam.Contracts;

/// <summary>
/// Structured result type for adapter operations, replacing bare <c>null</c> returns.
/// All adapter callers should use this instead of nullable-return patterns so that
/// error codes, messages, and metadata travel with the result rather than being
/// inferred from log lines.
/// </summary>
public sealed record ControlResult<T>
{
    /// <summary>True when the operation completed as expected.</summary>
    public bool Success { get; init; }

    /// <summary>The operation payload. Null when <see cref="Success"/> is false.</summary>
    public T? Value { get; init; }

    /// <summary>Machine-readable error code, e.g. "device-unreachable", "auth-failed", "timeout".</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable explanation. Safe to surface in API responses.</summary>
    public string? Message { get; init; }

    /// <summary>HTTP status code when applicable.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Duration of the operation in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Optional diagnostic metadata (adapter name, endpoint, etc.).</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    public static ControlResult<T> Ok(T value, string? message = null, int? httpStatusCode = null)
        => new()
        {
            Success = true,
            Value = value,
            Message = message,
            HttpStatusCode = httpStatusCode
        };

    public static ControlResult<T> Fail(string errorCode, string? message = null, int? httpStatusCode = null)
        => new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            HttpStatusCode = httpStatusCode
        };

    public static ControlResult<T> FromException(Exception ex, string? errorCode = null)
        => new()
        {
            Success = false,
            ErrorCode = errorCode ?? "exception",
            Message = ex.Message
        };
}
