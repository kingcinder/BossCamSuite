using BossCam.Core;
using BossCam.Infrastructure.Firmware;
using BossCam.Infrastructure.Imports;
using BossCam.Infrastructure.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class ProtocolManifestProviderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Loads_EndpointCatalog_Array_As_Manifest()
    {
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "endpoint_catalog.json"), "[{\"tag\":\"System\",\"endpoint\":\"/NetSDK/System/deviceInfo\",\"methods\":[\"GET\"],\"details\":{\"GET\":{\"description\":\"desc\",\"content\":\"req\",\"success_return\":\"resp\"}}}]");

        var provider = new JsonProtocolManifestProvider(Options.Create(new BossCamRuntimeOptions { ProtocolAssetsPath = _tempDirectory }), NullLogger<JsonProtocolManifestProvider>.Instance);
        var manifests = await provider.LoadAsync(CancellationToken.None);

        var manifest = Assert.Single(manifests);
        Assert.Equal("endpoint_catalog", manifest.ManifestId);
        Assert.Single(manifest.Endpoints);
        Assert.Equal("/NetSDK/System/deviceInfo", manifest.Endpoints[0].Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }
}

public sealed class ImportProviderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Parses_IpcamSuite_Mainset_Into_Devices()
    {
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "MAINSET.INI"), "[Priview]\ncsUsername=admin\ncsPasswd=secret\n[m_chiptype]\ntype=3516C-C3\n[ipc0]\nip=192.168.42.5\ndeviceid=IP_camera123\nport=80\n");
        var provider = new IpcamSuiteImportProvider(Options.Create(new BossCamRuntimeOptions { IpcamSuiteDirectory = _tempDirectory }));

        var devices = await provider.ImportAsync(CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal("192.168.42.5", device.IpAddress);
        Assert.Equal("admin", device.LoginName);
        Assert.Equal("3516C-C3", device.HardwareModel);
        Assert.Contains(device.TransportProfiles, profile => profile.Kind == BossCam.Contracts.TransportKind.LanRest);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }
}

public sealed class FirmwareArtifactAnalyzerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Detects_HttpPaths_And_ModelStrings()
    {
        Directory.CreateDirectory(_tempDirectory);
        var filePath = Path.Combine(_tempDirectory, "firmware.rom");
        await File.WriteAllBytesAsync(filePath, System.Text.Encoding.ASCII.GetBytes("prefix /NetSDK/System/deviceInfo hello 5523-w K8208 /cgi-bin/upload.cgi suffix"));

        var analyzer = new FirmwareArtifactAnalyzer();
        var artifact = await analyzer.AnalyzeAsync(filePath, CancellationToken.None);

        Assert.Contains(artifact.HttpPaths, path => path.Contains("/NetSDK/System/deviceInfo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(artifact.ModelStrings, value => value.Contains("5523", StringComparison.OrdinalIgnoreCase) || value.Contains("K8208", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(artifact.Sha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }
}
