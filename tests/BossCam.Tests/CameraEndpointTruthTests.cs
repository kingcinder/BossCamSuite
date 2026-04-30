using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class CameraEndpointTruthTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-endpoint-truth-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public CameraEndpointTruthTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Verified_5523w_Profile_Preserves_Onvif_Rtsp_Mismatch_And_Ptz_Service_State()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), Name = "5523-W sample", IpAddress = "10.0.0.29", HardwareModel = "5523-W", LoginName = "admin", Password = "" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var service = CreateService(store);
        var profile = await service.SaveObservedProfileAsync(CameraEndpointTruthService.CreateVerified5523wSample(device.Id), CancellationToken.None);
        var loaded = await service.GetSummaryAsync(device.Id, CancellationToken.None);

        Assert.Equal(CameraCredentialState.Verified, profile.CredentialState);
        Assert.Contains(profile.Endpoints, endpoint => endpoint.Endpoint == "http://10.0.0.29:8888/onvif/media_service" && endpoint.State == CameraEndpointVerificationState.Verified);
        Assert.Contains(profile.OnvifDeclaredStreams, stream => stream.ProfileToken == "PROFILE_000" && stream.Encoding == "H264" && stream.Width == 2560 && stream.Height == 1920);
        Assert.Contains(profile.OnvifDeclaredStreams, stream => stream.ProfileToken == "PROFILE_001" && stream.Encoding == "JPEG");
        Assert.Contains(profile.RtspPlaybackStreams, stream => stream.ProfileToken == "PROFILE_001" && stream.Codec == "hevc" && stream.Uri == "rtsp://admin:@10.0.0.29:554/ch0_1.264");
        Assert.Contains(profile.RtspPlaybackStreams, stream => stream.ProfileToken == "ADMIN_NEGATIVE" && stream.CredentialState == CameraCredentialState.Rejected);
        Assert.True(profile.Ptz.GetStatusVerified);
        Assert.False(profile.Ptz.MovementControlsEnabled);
        Assert.Equal(MechanicalPtzCapability.NotInstalledOrUnknown, profile.Ptz.MechanicalCapability);
        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task Same_Model_Firmware_Endpoint_Maps_Are_Per_Camera_And_Can_Drift()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var service = CreateService(store);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await service.SaveObservedProfileAsync(CameraEndpointTruthService.CreateVerified5523wSample(firstId) with { HardwareModel = "5523-W", FirmwareVersion = "same" }, CancellationToken.None);
        await service.SaveObservedProfileAsync(new CameraEndpointTruthProfile
        {
            DeviceId = secondId,
            IpAddress = "10.0.0.30",
            HardwareModel = "5523-W",
            FirmwareVersion = "same",
            Endpoints = [new CameraEndpointObservation { Capability = "Media", Endpoint = "http://10.0.0.30/onvif/media_service", State = CameraEndpointVerificationState.Verified }]
        }, CancellationToken.None);

        var summary = await service.GetSummaryAsync(firstId, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.True(summary.EndpointDriftDetected);
        Assert.Contains(summary.SameModelProfiles, profile => profile.DeviceId == secondId);
    }

    [Fact]
    public async Task Rtsp_Source_Projection_Trusts_Probed_Codec_Not_Path_Suffix()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.29", Port = 80 };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveCameraEndpointTruthProfileAsync(CameraEndpointTruthService.CreateVerified5523wSample(device.Id), CancellationToken.None);

        var adapter = new StreamDescriptorAdapter(Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 1 }), store);
        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Contains(sources, source => source.Url == "rtsp://admin:@10.0.0.29:554/ch0_0.264" && source.Metadata["probedCodec"] == "h264");
        Assert.Contains(sources, source => source.Url == "rtsp://admin:@10.0.0.29:554/ch0_1.264" && source.Metadata["probedCodec"] == "hevc");
    }

    [Fact]
    public async Task Verified_Sample_Fallback_Is_Only_For_Matching_5523w_Host()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var adapter = new StreamDescriptorAdapter(Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 1 }), store);

        var sample = await adapter.GetSourcesAsync(new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.29" }, CancellationToken.None);
        var other = await adapter.GetSourcesAsync(new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.30" }, CancellationToken.None);

        Assert.Contains(sample, source => source.DisplayName == "Sub stream verified sample fallback" && source.Metadata["probedCodec"] == "hevc");
        Assert.DoesNotContain(other, source => source.Metadata.GetValueOrDefault("source") == "verified sample fallback");
    }

    [Fact]
    public async Task Camera_227_Source_Truth_Preserves_Empty_Password_Auth_Failure_And_Lowres_Snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var adapter = new StreamDescriptorAdapter(Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 1 }), store);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.227", LoginName = "admin", Password = "", HardwareModel = "5523-W" };

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        var main = Assert.Single(sources, source => source.StreamRole == "main" && source.ChannelId == "101");
        Assert.Equal("rtsp://admin:@10.0.0.227:554/ch0_0.264", main.Url);
        Assert.Equal(CredentialState.UsernameOnlyEmptyPassword, main.CredentialState);
        Assert.Equal(SourceTruthOutcome.FAIL_RTSP_EMPTY_PASSWORD_AUTH_NEGOTIATION, main.SourceTruthOutcome);
        Assert.Equal(2560, main.ExpectedWidth);
        Assert.Equal(1920, main.ExpectedHeight);
        Assert.Equal("H.264", main.ExpectedCodec);

        var snapshot = Assert.Single(sources, source => source.StreamRole == "snapshot");
        Assert.True(snapshot.LowResOnly);
        Assert.Equal(SourceTruthOutcome.PASS_LOWRES_ONLY, snapshot.SourceTruthOutcome);
        Assert.Equal(704, snapshot.ExpectedWidth);
        Assert.Equal(480, snapshot.ExpectedHeight);
        Assert.DoesNotContain(sources, source => source.Url == "rtsp://10.0.0.227:554");
    }

    [Fact]
    public async Task Guard_Prevents_Weaker_Candidate_And_Declared_Codec_From_Overwriting_Proof()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var service = CreateService(store);
        var id = Guid.NewGuid();
        await service.SaveObservedProfileAsync(CameraEndpointTruthService.CreateVerified5523wSample(id), CancellationToken.None);

        var weaker = new CameraEndpointTruthProfile
        {
            DeviceId = id,
            Endpoints = [new CameraEndpointObservation { Capability = "Media", Endpoint = "http://10.0.0.29:8888/onvif/media_service", State = CameraEndpointVerificationState.UnverifiedCandidate }],
            RtspPlaybackStreams = [new RtspPlaybackProbeMetadata { ProfileToken = "PROFILE_001", Uri = "rtsp://admin:@10.0.0.29:554/ch0_1.264", State = CameraEndpointVerificationState.Verified, Codec = "h264" }],
            Ptz = new PtzServiceState { ServiceState = CameraEndpointVerificationState.Verified, MovementControlsEnabled = true, MechanicalCapability = MechanicalPtzCapability.NotInstalledOrUnknown }
        };

        var saved = await service.SaveObservedProfileAsync(weaker, CancellationToken.None);

        Assert.Contains(saved.Endpoints, endpoint => endpoint.Endpoint.EndsWith("/onvif/media_service") && endpoint.State == CameraEndpointVerificationState.Verified);
        Assert.Contains(saved.RtspPlaybackStreams, stream => stream.ProfileToken == "PROFILE_001" && stream.Codec == "hevc");
        Assert.False(saved.Ptz.MovementControlsEnabled);
        Assert.NotEmpty(saved.DriftNotes);
    }

    [Fact]
    public async Task Refresh_Seeds_Candidates_Without_Failing_Import_Or_Erasing_Verified_Truth()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var service = CreateService(store);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.29", HardwareModel = "5523-W", FirmwareVersion = "same" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await service.SaveObservedProfileAsync(CameraEndpointTruthService.CreateVerified5523wSample(device.Id), CancellationToken.None);

        var refreshed = await service.RefreshAsync(device.Id, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Contains(refreshed.Endpoints, endpoint => endpoint.Endpoint == "http://10.0.0.29:8888/onvif/media_service" && endpoint.State == CameraEndpointVerificationState.Verified);
        Assert.Contains(refreshed.Endpoints, endpoint => endpoint.Endpoint.EndsWith("/onvif/imaging_service") && endpoint.State == CameraEndpointVerificationState.Failed);
        Assert.NotEmpty(refreshed.DriftNotes);
    }

    [Fact]
    public async Task Live_Refresh_Populates_From_Mocked_Onvif_And_Ffprobe()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.29", LoginName = "admin", Password = "", HardwareModel = "5523-W", FirmwareVersion = "same" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        var service = CreateService(store);

        var refreshed = await service.RefreshAsync(device.Id, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Contains(refreshed.OnvifDeclaredStreams, s => s.ProfileToken == "PROFILE_001" && s.Encoding == "JPEG");
        Assert.Contains(refreshed.RtspPlaybackStreams, s => s.ProfileToken == "PROFILE_001" && s.Codec == "hevc" && s.Width == 704 && s.Height == 480);
        Assert.Contains(refreshed.Endpoints, e => e.Capability == "Imaging" && e.State == CameraEndpointVerificationState.Failed);
        Assert.False(refreshed.Ptz.MovementControlsEnabled);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, true); } catch { }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static CameraEndpointTruthService CreateService(SqliteApplicationStore store)
        => new(
            store,
            new EndpointTruthLiveBuilder(new FakeFfprobe(), NullLogger<EndpointTruthLiveBuilder>.Instance),
            new FakeOnvifProbe(),
            NullLogger<CameraEndpointTruthService>.Instance);

    private sealed class FakeOnvifProbe : IEndpointTruthLiveProbeClient
    {
        public Task<LiveOnvifProbeResult> ProbeAsync(DeviceIdentity device, IReadOnlyCollection<CameraEndpointObservation> candidates, CancellationToken cancellationToken)
            => Task.FromResult(new LiveOnvifProbeResult
            {
                Endpoints =
                [
                    new CameraEndpointObservation { Capability = "Media", Endpoint = "http://10.0.0.29:8888/onvif/media_service", State = CameraEndpointVerificationState.Verified, CandidateSource = EndpointCandidateSource.LiveProbe, Evidence = "mock GetProfiles/GetStreamUri" },
                    new CameraEndpointObservation { Capability = "PTZ", Endpoint = "http://10.0.0.29:8888/onvif/ptz_service", State = CameraEndpointVerificationState.Verified, CandidateSource = EndpointCandidateSource.LiveProbe, Evidence = "mock GetStatus 200" },
                    new CameraEndpointObservation { Capability = "Imaging", Endpoint = "http://10.0.0.29:8888/onvif/imaging_service", State = CameraEndpointVerificationState.Failed, CandidateSource = EndpointCandidateSource.LiveProbe, Evidence = "mock failed imaging probe" }
                ],
                DeclaredStreams =
                [
                    new OnvifDeclaredStreamMetadata { ProfileToken = "PROFILE_000", Encoding = "H264", Width = 2560, Height = 1920, Fps = 15, BitrateKbps = 5120 },
                    new OnvifDeclaredStreamMetadata { ProfileToken = "PROFILE_001", Encoding = "JPEG", Width = 704, Height = 480, Fps = 15, BitrateKbps = 384 }
                ],
                StreamUrisByProfile = new Dictionary<string, string>
                {
                    ["PROFILE_000"] = "rtsp://10.0.0.29:554/ch0_0.264",
                    ["PROFILE_001"] = "rtsp://10.0.0.29:554/ch0_1.264"
                },
                Ptz = new PtzServiceState { ServiceState = CameraEndpointVerificationState.Verified, ServiceEndpoint = "http://10.0.0.29:8888/onvif/ptz_service", GetStatusVerified = true, MechanicalCapability = MechanicalPtzCapability.NotInstalledOrUnknown, MovementControlsEnabled = false }
            });
    }

    private sealed class FakeFfprobe : IFfprobePlaybackProbe
    {
        public Task<RtspPlaybackProbeMetadata> ProbeAsync(string profileToken, string uri, string? username, string? password, CancellationToken cancellationToken)
            => Task.FromResult(profileToken == "PROFILE_001"
                ? new RtspPlaybackProbeMetadata { ProfileToken = profileToken, Uri = "rtsp://admin:@10.0.0.29:554/ch0_1.264", State = CameraEndpointVerificationState.Verified, CredentialState = CameraCredentialState.Verified, VerifiedUsername = "admin", Codec = "hevc", Width = 704, Height = 480, Fps = "15/1" }
                : new RtspPlaybackProbeMetadata { ProfileToken = profileToken, Uri = "rtsp://admin:@10.0.0.29:554/ch0_0.264", State = CameraEndpointVerificationState.Verified, CredentialState = CameraCredentialState.Verified, VerifiedUsername = "admin", Codec = "h264", Width = 2560, Height = 1920, Fps = "13/1" });
    }
}
