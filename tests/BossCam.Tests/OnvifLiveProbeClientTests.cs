using System.Net.Sockets;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class OnvifLiveProbeClientTests
{
    [Fact]
    public async Task GetStreamUri_Request_Carries_The_Profile_Token()
    {
        using var server = new FakeOnvifSoapServer();
        var client = new HttpOnvifLiveProbeClient(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 5 }),
            NullLogger<HttpOnvifLiveProbeClient>.Instance);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "127.0.0.1" };
        var candidates = new[]
        {
            new CameraEndpointObservation
            {
                Capability = "Media",
                Endpoint = server.Endpoint("media_service"),
                State = CameraEndpointVerificationState.UnverifiedCandidate,
                CandidateSource = EndpointCandidateSource.LiveProbe,
                TruthStrength = TruthStrength.Candidate
            }
        };

        var result = await client.ProbeAsync(device, candidates, CancellationToken.None);

        Assert.Equal(2, result.StreamUrisByProfile.Count);
        Assert.Contains("PROFILE_000", result.StreamUrisByProfile.Keys);
        Assert.Contains("PROFILE_001", result.StreamUrisByProfile.Keys);
        Assert.NotNull(server.LastGetStreamUriBody);
        Assert.Contains("<trt:ProfileToken>PROFILE_001</trt:ProfileToken>", server.LastGetStreamUriBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamUri_Response_Is_Keyed_By_Profile_Token()
    {
        using var server = new FakeOnvifSoapServer();
        var client = new HttpOnvifLiveProbeClient(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 5 }),
            NullLogger<HttpOnvifLiveProbeClient>.Instance);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "127.0.0.1" };
        var candidates = new[]
        {
            new CameraEndpointObservation
            {
                Capability = "Media",
                Endpoint = server.Endpoint("media_service"),
                State = CameraEndpointVerificationState.UnverifiedCandidate,
                CandidateSource = EndpointCandidateSource.LiveProbe,
                TruthStrength = TruthStrength.Candidate
            }
        };

        var result = await client.ProbeAsync(device, candidates, CancellationToken.None);

        Assert.Equal("rtsp://10.0.0.29:554/ch0_0.264", result.StreamUrisByProfile["PROFILE_000"]);
        Assert.Equal("rtsp://10.0.0.29:554/ch0_1.264", result.StreamUrisByProfile["PROFILE_001"]);
    }

    /// <summary>
    /// Minimal loopback SOAP fixture: answers GetSystemDateAndTime (200), GetProfiles (two
    /// profiles), and GetStreamUri (a Uri per token) while recording the last GetStreamUri body.
    /// </summary>
    private sealed class FakeOnvifSoapServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public FakeOnvifSoapServer()
        {
            _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _acceptLoop = Task.Run(() => AcceptLoopAsync());
        }

        public string BaseUrl { get; }

        public string? LastGetStreamUriBody { get; private set; }

        public string Endpoint(string path) => $"{BaseUrl}/{path}";

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var body = await ReadRequestBodyAsync(stream);
                    string responseBody;
                    if (body.Contains("GetSystemDateAndTime", StringComparison.Ordinal))
                    {
                        responseBody = """<?xml version="1.0"?><s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><GetSystemDateAndTimeResponse/></s:Body></s:Envelope>""";
                    }
                    else if (body.Contains("GetProfiles", StringComparison.Ordinal))
                    {
                        responseBody = """<?xml version="1.0"?><s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><GetProfilesResponse><Profiles token="PROFILE_000"><Encoding>H264</Encoding><Width>2560</Width><Height>1920</Height></Profiles><Profiles token="PROFILE_001"><Encoding>JPEG</Encoding><Width>704</Width><Height>480</Height></Profiles></GetProfilesResponse></s:Body></s:Envelope>""";
                    }
                    else if (body.Contains("GetStreamUri", StringComparison.Ordinal))
                    {
                        LastGetStreamUriBody = body;
                        // Serve a per-token URI so the test can prove the token actually reached
                        // the request body: PROFILE_001 must return ch0_1.264, not the default.
                        var uri = body.Contains("PROFILE_001", StringComparison.Ordinal) ? "rtsp://10.0.0.29:554/ch0_1.264" : "rtsp://10.0.0.29:554/ch0_0.264";
                        responseBody = $"""<?xml version="1.0"?><s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><GetStreamUriResponse><Uri>{uri}</Uri></GetStreamUriResponse></s:Body></s:Envelope>""";
                    }
                    else
                    {
                        responseBody = """<?xml version="1.0"?><s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body/></s:Envelope>""";
                    }

                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/soap+xml\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    var response = Encoding.ASCII.GetBytes(header).Concat(bytes).ToArray();
                    await stream.WriteAsync(response);
                    await stream.FlushAsync();
                }
                catch
                {
                    // Fixture only; client-side failures surface in the test.
                }
            }
        }

        private static async Task<string> ReadRequestBodyAsync(NetworkStream stream)
        {
            var all = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                all.Write(buffer, 0, read);
                var arr = all.GetBuffer();
                var length = (int)all.Length;
                for (var i = 0; i + 3 < length; i++)
                {
                    if (arr[i] == 13 && arr[i + 1] == 10 && arr[i + 2] == 13 && arr[i + 3] == 10)
                    {
                        headerEnd = i + 4;
                        break;
                    }
                }
            }

            if (headerEnd < 0)
            {
                return string.Empty;
            }

            var headerText = Encoding.ASCII.GetString(all.GetBuffer(), 0, headerEnd);
            var contentLength = 0;
            foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Split(':', 2)[1].Trim(), out contentLength);
                }
            }

            var body = new List<byte>();
            var have = (int)all.Length - headerEnd;
            if (have > 0)
            {
                for (var i = 0; i < have; i++)
                {
                    body.Add(all.GetBuffer()[headerEnd + i]);
                }
            }

            while (body.Count < contentLength)
            {
                var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, contentLength - body.Count));
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    body.Add(buffer[i]);
                }
            }

            return Encoding.UTF8.GetString(body.ToArray());
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }
}
