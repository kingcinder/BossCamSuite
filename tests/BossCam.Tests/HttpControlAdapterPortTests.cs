using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class HttpControlAdapterPortTests
{
    [Fact]
    public void NetSdk_Endpoints_Use_Http_Port_When_Device_Row_Port_Is_Onvif()
    {
        var adapter = new TestHttpAdapter();
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.227",
            Port = 8888,
            LoginName = "admin",
            Password = string.Empty
        };

        var uri = adapter.ExposeBuildDeviceUri(device, "/NetSDK/Video/input/channel/1");

        Assert.Equal("http://10.0.0.227/NetSDK/Video/input/channel/1", uri.ToString());
    }

    [Fact]
    public void NetSdk_Endpoints_Preserve_Explicit_Http_Metadata_Port()
    {
        var adapter = new TestHttpAdapter();
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.227",
            Port = 8888,
            Metadata = new Dictionary<string, string> { ["httpPort"] = "8080" }
        };

        var uri = adapter.ExposeBuildDeviceUri(device, "/NetSDK/Video/input/channel/1");

        Assert.Equal("http://10.0.0.227:8080/NetSDK/Video/input/channel/1", uri.ToString());
    }

    private sealed class TestHttpAdapter()
        : HttpControlAdapterBase(Microsoft.Extensions.Options.Options.Create(new BossCamRuntimeOptions()), NullLogger.Instance)
    {
        public Uri ExposeBuildDeviceUri(DeviceIdentity device, string endpoint) => BuildDeviceUri(device, endpoint);
    }
}
