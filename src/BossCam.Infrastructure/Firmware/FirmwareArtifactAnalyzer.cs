using System.Security.Cryptography;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Infrastructure.Firmware;

public sealed class FirmwareArtifactAnalyzer : IFirmwareArtifactAnalyzer
{
    public async Task<FirmwareArtifact> AnalyzeAsync(string filePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var httpPaths = ExtractPrintableMatches(bytes, static text => text.Contains("/NetSDK/", StringComparison.OrdinalIgnoreCase) || text.Contains("/cgi-bin/", StringComparison.OrdinalIgnoreCase) || text.Contains("/user/", StringComparison.OrdinalIgnoreCase));
        var modelStrings = ExtractPrintableMatches(bytes, static text => text.Contains("3523", StringComparison.OrdinalIgnoreCase) || text.Contains("5523", StringComparison.OrdinalIgnoreCase) || text.Contains("K8208", StringComparison.OrdinalIgnoreCase) || text.Contains("NVR", StringComparison.OrdinalIgnoreCase) || text.Contains("IPC", StringComparison.OrdinalIgnoreCase));
        var signatures = DetectSignatures(bytes);

        return new FirmwareArtifact
        {
            FilePath = filePath,
            FileName = info.Name,
            SizeBytes = info.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Family = modelStrings.FirstOrDefault(static value => value.Contains("5523", StringComparison.OrdinalIgnoreCase) || value.Contains("K8208", StringComparison.OrdinalIgnoreCase)) is { } match ? match : "Unknown",
            Signatures = signatures,
            HttpPaths = httpPaths,
            ModelStrings = modelStrings,
            Metadata = new Dictionary<string, string>
            {
                ["directory"] = info.DirectoryName ?? string.Empty,
                ["extension"] = info.Extension,
                ["packed"] = signatures.Contains("jffs2", StringComparer.OrdinalIgnoreCase) || signatures.Contains("gzip", StringComparer.OrdinalIgnoreCase) ? "likely" : "unknown"
            }
        };
    }

    private static List<string> DetectSignatures(ReadOnlySpan<byte> bytes)
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (bytes.IndexOf("ustar"u8) >= 0) signatures.Add("tar");
        if (bytes.IndexOf(new byte[] { 0x1F, 0x8B, 0x08 }) >= 0) signatures.Add("gzip");
        if (bytes.IndexOf(new byte[] { 0x85, 0x19 }) >= 0) signatures.Add("jffs2");
        if (bytes.IndexOf("hsqs"u8) >= 0) signatures.Add("squashfs");
        if (bytes.IndexOf("UBI#"u8) >= 0) signatures.Add("ubi");
        if (bytes.IndexOf("cramfs"u8) >= 0) signatures.Add("cramfs");
        return signatures.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractPrintableMatches(byte[] bytes, Func<string, bool> predicate)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var value in bytes)
        {
            if (value is >= 32 and <= 126)
            {
                builder.Append((char)value);
                continue;
            }

            Flush();
        }
        Flush();
        return matches.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).Take(200).ToList();

        void Flush()
        {
            if (builder.Length < 6)
            {
                builder.Clear();
                return;
            }

            var text = builder.ToString();
            if (predicate(text))
            {
                matches.Add(text);
            }
            builder.Clear();
        }
    }
}
