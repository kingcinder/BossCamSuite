using System.Diagnostics;

namespace BossCam.Core.Utilities;

/// <summary>
/// Shared helper for safe process invocation. Every <c>Process.Start</c> / <c>new ProcessStartInfo</c>
/// site in the repo must go through this class so:
/// (1) command-line arguments are passed one-per-element via <see cref="ProcessStartInfo.ArgumentList"/>,
///     eliminating argument-injection vulnerabilities;
/// (2) bash / curl helper scripts are constructed with the same <see cref="BashQuote"/> escaping
///     rule everywhere, with no per-callsite drift;
/// (3) the redirection / non-shell-execute / no-window defaults are consistent.
/// </summary>
public static class ProcessLauncher
{
    /// <summary>
    /// Build a <see cref="ProcessStartInfo"/> for <paramref name="fileName"/> with
    /// <paramref name="arguments"/> passed one-per-element via <c>ArgumentList</c>.
    /// </summary>
    /// <remarks>
    /// Using <c>ArgumentList</c> prevents argument-injection — even an argument value
    /// containing <c>"</c>, <c>'</c>, spaces, or shell metacharacters is delivered
    /// verbatim to the spawned process as a single argv element. We deliberately do NOT
    /// set <c>UseShellExecute=true</c> (would re-introduce shell expansion) and we DO
    /// redirect stderr/stdout + create no window so ffmpeg doesn't pop a console on
    /// Windows and so logs land in our drainers.
    /// </remarks>
    public static ProcessStartInfo Build(
        string fileName,
        IEnumerable<string> arguments,
        bool redirectStdout = true,
        bool redirectStderr = true,
        bool createNoWindow = true)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = redirectStderr,
            CreateNoWindow = createNoWindow,
        };
        foreach (var arg in arguments)
        {
            // ArgumentList refuses null entries; coerce null → empty to keep ffmpeg
            // happy when an upstream caller forgets to fill a flag value.
            info.ArgumentList.Add(arg ?? string.Empty);
        }
        return info;
    }

    /// <summary>
    /// Build a <see cref="ProcessStartInfo"/> that runs an existing script file via
    /// <c>/bin/bash &lt;scriptPath&gt;</c>. The script content is the caller's
    /// responsibility — build it with <see cref="BashQuote"/> escaping for any
    /// user-supplied value embedded in the script body.
    /// </summary>
    public static ProcessStartInfo BuildBashScript(
        string scriptPath,
        bool redirectStdout = true,
        bool redirectStderr = true,
        bool createNoWindow = true)
    {
        return Build("/bin/bash", [scriptPath], redirectStdout, redirectStderr, createNoWindow);
    }

    /// <summary>
    /// POSIX single-quote escape for embedding a value in a bash command. Any embedded
    /// single quote is escaped via the canonical <c>'…'\''…'</c> trick. Required for the
    /// snapshot pipeline's <c>curl -fsS -m 4 -u user:pass URL</c> because credentials
    /// can legitimately contain apostrophes.
    /// </summary>
    public static string BashQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Launch <paramref name="info"/>. Returns the started process. Throws
    /// <see cref="InvalidOperationException"/> on launch failure with a clear message.
    /// </summary>
    public static Process Start(ProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {info.FileName}.");
        }
        return process;
    }
}
