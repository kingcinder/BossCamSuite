using System.Diagnostics;
using System.Text;
using BossCam.Contracts;
using BossCam.Core.Utilities;

namespace BossCam.Core.Services.Recording;

/// <summary>
/// Bubble FLV recording pipeline. Fetches the auth-free <c>/bubble/live</c> endpoint
/// (content-type <c>video/bubble</c>), strips the XML descriptor header and padding
/// bytes, and feeds the raw H.265 stream to ffmpeg's segment writer at 15 fps.
///
/// This is the passwordless recording path for locked 5523-W cameras whose RTSP
/// and NetSDK snapshot endpoints reject all known credentials — bubble/live serves
/// H.265 video without any HTTP authentication.
///
/// The pipeline is a single bash command:
/// <c>curl … | python3 strip-xml-to-first-start-code | ffmpeg -f hevc -r 15 -i - -c:v copy …</c>.
///
/// All user-supplied values flow through <see cref="ProcessLauncher.BashQuote"/>
/// so shell metacharacters in camera URLs can never break out of quoting.
///
/// Windows is not currently supported for bubble recording — PowerShell pipes
/// corrupt binary H.265 data. Windows deployments fall through to the snapshot
/// pipeline (which also requires working credentials).
/// </summary>
public sealed class BubbleFlvRecordingPipeline : IRecordingPipeline
{
    public string Mode => "bubble-flv";

    // Python streaming strip: reads chunks from stdin, accumulates until the
    // closing XML tag and first H.265 Annex B start code are found, then
    // forwards bytes to stdout immediately (critical: read() on a live stream
    // would block forever since curl never closes the connection).
    // Verified against live 5523-W captures:
    // </bubble>\x00 is followed by ~35 bytes of 0x23 padding before video.
    // State machine: xml_stripped → have_start_code → streaming.
    // xml_stripped prevents re-searching for </bubble> in the H.265 bitstream.
    private const string StripXmlPython =
        "import sys\n" +
        "buf=b''; started=False; xml_stripped=False\n" +
        "while True:\n" +
        "  chunk=sys.stdin.buffer.read(65536)\n" +
        "  if not chunk: break\n" +
        "  buf+=chunk\n" +
        "  if not started:\n" +
        "    if not xml_stripped:\n" +
        "      i=buf.find(b'</bubble>')\n" +
        "      if i<0: continue\n" +
        "      buf=buf[i+10:]; xml_stripped=True\n" +
        "    h=buf.find(b'\x00\x00\x00\x01')\n" +
        "    if h<0: h=buf.find(b'\x00\x00\x01')\n" +
        "    if h>=0:\n" +
        "      buf=buf[h:]; started=True\n" +
        "  if started and buf:\n" +
        "    sys.stdout.buffer.write(buf)\n" +
        "    sys.stdout.buffer.flush()\n" +
        "    buf=b''\n" +
        "if not started and buf: sys.stdout.buffer.write(buf)\n";

    public RecordingHandle Start(RecordingPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceUrl = context.SourceUrl;
        var segmentPattern = context.SegmentPattern;
        var segmentSeconds = Math.Max(10, context.SegmentSeconds);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Bubble FLV recording requires bash+python3+curl. " +
                "Windows deployments should fall through to the snapshot pipeline.");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"bosscam-rec-bubble-{context.Device.Id:N}.sh");
        var script = new StringBuilder();
        script.AppendLine("#!/usr/bin/env bash");
        script.AppendLine("set -euo pipefail");
        // No -m max-time on curl — the process tree gets killed on stop.
        // --connect-timeout 10 handles transient network stalls; a healthy
        // stream stays open indefinitely.
        script.Append("curl -fsS --connect-timeout 10 ")
            .Append(ProcessLauncher.BashQuote(sourceUrl))
            .Append(" | python3 -c ")
            .Append(ProcessLauncher.BashQuote(StripXmlPython))
            .Append(" | ")
            .Append(ProcessLauncher.BashQuote(context.FfmpegPath))
            .Append(" -hide_banner -loglevel warning -y -f hevc -r 15 -i - ")
            .Append("-c:v copy -map 0:v:0 ")
            .Append("-f segment -segment_time ")
            .Append(segmentSeconds)
            .Append(" -segment_format mpegts -reset_timestamps 1 -strftime 1 ")
            .Append(ProcessLauncher.BashQuote(segmentPattern))
            .AppendLine();
        File.WriteAllText(scriptPath, script.ToString());
        try { Process.Start("chmod", $"+x {Path.GetFullPath(scriptPath)}")?.WaitForExit(2000); } catch { }
        var info = ProcessLauncher.BuildBashScript(scriptPath);
        var process = ProcessLauncher.Start(info);
        return new RecordingHandle(process, scriptPath);
    }

    public Task StopAsync(RecordingHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            if (!handle.Process.HasExited)
            {
                handle.Process.Kill(entireProcessTree: true);
                handle.Process.WaitForExit(8000);
            }
        }
        catch
        {
            // best-effort stop
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
                // best-effort /tmp cleanup
            }
        }

        return Task.CompletedTask;
    }
}
