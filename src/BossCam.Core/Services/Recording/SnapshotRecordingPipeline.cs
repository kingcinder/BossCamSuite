using System.Diagnostics;
using System.Text;
using BossCam.Contracts;
using BossCam.Core.Utilities;

namespace BossCam.Core.Services.Recording;

/// <summary>
/// Snapshot pipeline. Polls JPEG snapshots from the camera and pipes them into ffmpeg's
/// libx264 segment writer. This is the default on 5523-W where <c>/NetSDK/.../snapShot</c>
/// returns valid image/jpg even when RTSP media bytes are zero after PLAY.
///
/// On Linux/macOS the pipeline is implemented as a single bash helper script that
/// loops <c>curl -fsS -m 4 -u user:pass URL</c> into ffmpeg via a pipeline
/// (<c>while true; do curl …; sleep 0.5; done | ffmpeg -f image2pipe …</c>). On
/// Windows the loop is collapsed into a single ffmpeg invocation with
/// <c>-loop 1 -re</c> because bash isn't reliably available.
///
/// All user-supplied values (user, password, URL, segment pattern, ffmpeg path) flow
/// through <see cref="ProcessLauncher.BashQuote"/> so embedded single quotes /
/// shell metacharacters can never break out of the quoting.
/// </summary>
public sealed class SnapshotRecordingPipeline : IRecordingPipeline
{
    public string Mode => "snapshot-pipeline";

    public RecordingHandle Start(RecordingPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var device = context.Device;
        var snapshotUrl = context.SourceUrl;
        var segmentPattern = context.SegmentPattern;

        var fps = 2;
        var interval = "0.5";
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;

        // Strip credentials from the URL before embedding it in the script — the helper
        // gets auth via the -u flag with credentials quoted via BashQuote, not via the
        // URL, both for clarity and to keep the script body simple.
        var plainSnapshot = snapshotUrl;
        try
        {
            if (Uri.TryCreate(snapshotUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                var b = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
                plainSnapshot = b.Uri.ToString();
            }
        }
        catch
        {
            // best effort; if URL parsing fails, fall back to the raw snapshotUrl
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"bosscam-rec-{device.Id:N}.sh");
            var script = new StringBuilder();
            script.AppendLine("#!/usr/bin/env bash");
            script.AppendLine("set -euo pipefail");
            script.Append("while true; do curl -fsS -m 4 -u ")
                .Append(ProcessLauncher.BashQuote($"{user}:{password}"))
                .Append(' ')
                .Append(ProcessLauncher.BashQuote(plainSnapshot))
                .Append(" || true; sleep ")
                .Append(interval)
                .AppendLine("; done \\");
            // MPEG-TS is robust under kill/restart; no trailing moov required.
            script.Append("| ")
                .Append(ProcessLauncher.BashQuote(context.FfmpegPath))
                .Append(" -hide_banner -loglevel warning -y -f image2pipe -framerate ")
                .Append(fps)
                .Append(" -c:v mjpeg -i - -c:v libx264 -preset veryfast -pix_fmt yuv420p ")
                .Append("-f segment -segment_time ")
                .Append(Math.Max(10, context.SegmentSeconds))
                .Append(" -segment_format mpegts -reset_timestamps 1 -strftime 1 ")
                .Append(ProcessLauncher.BashQuote(segmentPattern))
                .AppendLine();
            File.WriteAllText(scriptPath, script.ToString());
            try { Process.Start("chmod", $"+x {Path.GetFullPath(scriptPath)}")?.WaitForExit(2000); } catch { }
            var info = ProcessLauncher.BuildBashScript(scriptPath);
            var process = ProcessLauncher.Start(info);
            return new RecordingHandle(process, scriptPath);
        }

        // Windows direct-ffmpeg fallback (-loop 1 -re to keep the same image polling
        // semantic without bash). All arguments flow through ArgumentList so embedded
        // shell metacharacters in camera URLs can't break out.
        var infoWin = ProcessLauncher.Build(context.FfmpegPath, new[]
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-loop", "1", "-re",
            "-i", snapshotUrl,
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-t", "86400",
            "-f", "segment",
            "-segment_time", context.SegmentSeconds.ToString(),
            "-reset_timestamps", "1", "-strftime", "1",
            segmentPattern
        });
        var processWin = ProcessLauncher.Start(infoWin);
        return new RecordingHandle(processWin, HelperScriptPath: null);
    }

    public Task StopAsync(RecordingHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            if (!handle.Process.HasExited)
            {
                // entireProcessTree required so bash pipeline children (curl/ffmpeg) die too.
                handle.Process.Kill(entireProcessTree: true);
                handle.Process.WaitForExit(8000);
            }
        }
        catch
        {
            // Best-effort stop; cleanup must still happen so /tmp doesn't leak.
        }

        if (!string.IsNullOrEmpty(handle.HelperScriptPath))
        {
            try
            {
                if (File.Exists(handle.HelperScriptPath))
                {
                    File.Delete(handle.HelperScriptPath);
                }
            }
            catch
            {
                // best-effort /tmp cleanup; ignore IO failures during shutdown
            }
        }

        return Task.CompletedTask;
    }
}
