namespace BossCam.Contracts;

public sealed record CompositePermutationCoverageRow
{
    public string SystemKey { get; init; } = string.Empty;
    public string SystemName { get; init; } = string.Empty;
    public string Classification { get; init; } = string.Empty;
    public int ValidStateCount { get; init; }
    public string BoundedBy { get; init; } = string.Empty;
    public string Rules { get; init; } = string.Empty;
}

public sealed record DeferredCompositeAttemptRow
{
    public string AttemptFamily { get; init; } = string.Empty;
    public string TransportPath { get; init; } = string.Empty;
    public string PayloadShape { get; init; } = string.Empty;
    public string LiveResponse { get; init; } = string.Empty;
    public string ReadbackBehavior { get; init; } = string.Empty;
    public string EvidenceLocation { get; init; } = string.Empty;
}

public sealed record DeferredCompositeObjectiveRow
{
    public string SystemKey { get; init; } = string.Empty;
    public string SystemName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string BlockerClassification { get; init; } = string.Empty;
    public string ExactFailureReason { get; init; } = string.Empty;
    public string CurrentUiPolicy { get; init; } = string.Empty;
    public IReadOnlyCollection<DeferredCompositeAttemptRow> Attempts { get; init; } = [];
}

public static class CompositeInteractionRules
{
    public const string PrivacyMaskBlockedReason =
        "5523-W firmware exposes readable privacy-mask objects, but properties are read-only; child/list writes return NETSDK statusCode=6 Invalid Document, full parent writes are accepted but readback remains unchanged, alternate known families do not own privacy masks, and the native/private bridge has no callable privacy-mask config ABI.";

    public const string MotionRegionBlockedReason =
        "5523-W firmware exposes motion detection as a writable grid composite only; detectionRegion is absent from live parent/properties readback, child region endpoints return 404, and full parent writes that inject detectionRegion or switch detectionType=region are accepted but readback remains grid with no region object.";

    public static bool IsStaticNetworkConfigurationVisible(bool dhcpEnabled)
        => !dhcpEnabled;

    public static bool IsWirelessApConfigurationVisible(string? wirelessMode, string? apMode)
        => string.Equals(wirelessMode, "AP", StringComparison.OrdinalIgnoreCase)
           || string.Equals(apMode, "On", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresWirelessCredentials(string? wirelessMode, string? apMode)
        => IsWirelessApConfigurationVisible(wirelessMode, apMode);

    public static bool IsMotionConfigurationVisible(bool motionEnabled)
        => motionEnabled;

    public static bool IsPrivacyMaskRegionVisible(bool privacyMaskEnabled)
        => privacyMaskEnabled;

    public static bool IsOverlayNameConfigurationVisible(bool overlayEnabled)
        => overlayEnabled;

    public static bool IsOverlayDateTimeConfigurationVisible(bool overlayEnabled)
        => overlayEnabled;

    public static IReadOnlyCollection<CompositePermutationCoverageRow> BuildCoverageSummary()
        => new[]
        {
            new CompositePermutationCoverageRow
            {
                SystemKey = "network.interface",
                SystemName = "Network Interface",
                Classification = ControlPointValueType.HigherOrderComposite.ToString(),
                ValidStateCount = 8,
                BoundedBy = "DHCP/static path x ESEE on/off x NTP on/off; address fields are treated as one validated static bundle.",
                Rules = "When DHCP is off, IP, netmask, gateway, and DNS must remain populated before save."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "network.wireless",
                SystemName = "Wireless / AP",
                Classification = ControlPointValueType.HigherOrderComposite.ToString(),
                ValidStateCount = 4,
                BoundedBy = "Disabled, Station, AP-off, AP-on(valid credentials bundle).",
                Rules = "AP mode requires SSID and PSK; AP channel is only relevant on the AP branch."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "video.overlay",
                SystemName = "Overlay / OSD",
                Classification = ControlPointValueType.HigherOrderComposite.ToString(),
                ValidStateCount = 15,
                BoundedBy = "Channel-name toggle x populated text bundle plus date/time toggle x 3 date formats x 2 time formats x weekday on/off.",
                Rules = "Name text is only operator-relevant when channel-name overlay is enabled; date/time format controls are only relevant when date/time overlay is enabled."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "motion.detection",
                SystemName = "Motion Detection",
                Classification = ControlPointValueType.HigherOrderComposite.ToString(),
                ValidStateCount = 12,
                BoundedBy = "Motion off plus motion on(grid/region) x trigger on/off x buzzer on/off; duration is treated as a validated scalar bundle.",
                Rules = "Detection type only matters when motion is enabled; trigger outputs remain bounded by the motion-enabled branch."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "video.privacy.mask",
                SystemName = "Privacy Mask",
                Classification = ControlPointValueType.CompositeControl.ToString(),
                ValidStateCount = 2,
                BoundedBy = "Mask off or mask on(valid region bundle).",
                Rules = "X, Y, width, and height are only valid when the privacy mask is enabled."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "users.password",
                SystemName = "User Password Reset",
                Classification = ControlPointValueType.HigherOrderComposite.ToString(),
                ValidStateCount = 1,
                BoundedBy = "User selection plus minimum-length password validation is treated as one guided action flow.",
                Rules = "A target username and an 8+ character password are required before apply."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "storage.playback",
                SystemName = "Playback Workflow",
                Classification = ControlPointValueType.HigherOrderComposite.ToString(),
                ValidStateCount = 8,
                BoundedBy = "Eight supported playback/search operations share one validated request bundle.",
                Rules = "Session/channel/timerange inputs form the common request object; filename, cursor, handle, and save path are operation-specific."
            },
            new CompositePermutationCoverageRow
            {
                SystemKey = "video.snapshot",
                SystemName = "Snapshot",
                Classification = ControlPointValueType.CompositeControl.ToString(),
                ValidStateCount = 2,
                BoundedBy = "Snapshot enabled/disabled with image-type enum bounded by the contract.",
                Rules = "Snapshot type is only meaningful when snapshot generation is enabled."
            }
        };

    public static IReadOnlyCollection<DeferredCompositeObjectiveRow> BuildDeferredObjectiveLedger()
        =>
        [
            new DeferredCompositeObjectiveRow
            {
                SystemKey = "video.privacy.mask",
                SystemName = "Privacy Mask",
                Status = "Blocked",
                BlockerClassification = "Firmware limitation plus missing native/private implementation",
                ExactFailureReason = PrivacyMaskBlockedReason,
                CurrentUiPolicy = "Keep the structured editor visible as a read-only truth surface; block normal saves and keep raw JSON only in Advanced diagnostics.",
                Attempts =
                [
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "A - REST child/list endpoint variants",
                        TransportPath = "/NetSDK/Video/input/channel/1/privacyMask/1; /privacyMask/1/properties; /privacyMasks; /privacyMasks/properties; /privacyMask/0",
                        PayloadShape = "Exact VideoPrivacyMask object and exact array of VideoPrivacyMask objects using id/enabled/regionX/regionY/regionWidth/regionHeight/regionColor.",
                        LiveResponse = "GET works for mask 1 and list; GET /properties reports every mask field mode=ro; PUT variants return statusCode=6 Invalid Document; mask 0 variants return 404.",
                        ReadbackBehavior = "Parent /NetSDK/Video/input/channel/1 readback remains enabled=false and 0x0 for all four masks.",
                        EvidenceLocation = "artifacts/live-validation/privacy-mask-closure-live-validation.json"
                    },
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "B - Full parent-object write with structural fidelity",
                        TransportPath = "/NetSDK/Video/input/channel/1",
                        PayloadShape = "Exact live VideoInputChannel object with unchanged siblings; only privacyMask[0] changed to enabled=true, region 0,0 size 1x1.",
                        LiveResponse = "PUT returns statusCode=0 OK.",
                        ReadbackBehavior = "Readback after 1s, 3s, and 5s remains enabled=false, regionWidth=0, regionHeight=0; restore also returns OK.",
                        EvidenceLocation = "artifacts/live-validation/privacy-mask-parent-exact-closure-validation.json"
                    },
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "C - Alternate grouped family ownership",
                        TransportPath = "/NetSDK/Image; /NetSDK/Image/0; /NetSDK/Video/encode/channel/101/properties; channelNameOverlay; datetimeOverlay; /NetSDK/Video/motionDetection/channel/1",
                        PayloadShape = "Exact GET shape inspection only; no speculative flattened privacy writes were sent to families whose live shapes lack privacy fields.",
                        LiveResponse = "Image, encode, overlay, and motion family GETs either succeed without privacyMask fields or 404 for /NetSDK/Image/0.",
                        ReadbackBehavior = "No alternate already-known endpoint family exposes a privacy-mask owner field to mutate.",
                        EvidenceLocation = "artifacts/live-validation/privacy-mask-closure-live-validation.json"
                    },
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "D - Native/private path recovery",
                        TransportPath = "NativeFallbackAdapter; C:/Users/ceide/Documents/BossCamSuite_SDK_AND_SUPPORT_FILES/NVR_SDK_v1.1.0.8/HISISDK.dll",
                        PayloadShape = "Native SDK bridge assessment plus export inspection; no JSON payload path exists.",
                        LiveResponse = "NativeFallbackAdapter.ApplyAsync is catalog-only and returns not implemented; installed IPCam Suite NetSdk.dll path is absent; HISISDK.dll exports generic HISI_DVR_GetDVRConfig/SetDVRConfig but no privacy-specific binding in BossCamSuite.",
                        ReadbackBehavior = "No callable native/private privacy-mask path exists in the current runtime.",
                        EvidenceLocation = "src/BossCam.Infrastructure/Control/RemoteAndNativeControlAdapters.cs; objdump export scan of HISISDK.dll"
                    }
                ]
            },
            new DeferredCompositeObjectiveRow
            {
                SystemKey = "motion.detection.region",
                SystemName = "Motion Region",
                Status = "Blocked",
                BlockerClassification = "Firmware limitation on this motion-detection mode",
                ExactFailureReason = MotionRegionBlockedReason,
                CurrentUiPolicy = "Keep the grid editor as the normal operator path; show the region branch as a structured read-only blocker when detectionType=region is selected.",
                Attempts =
                [
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "A - REST child endpoint variants",
                        TransportPath = "/NetSDK/Video/motionDetection/channel/1/detectionRegion/1; /detectionRegions; /detectionRegion/1/properties",
                        PayloadShape = "VideoMotionDetectionRegion object with id/enabled/regionX/regionY/regionWidth/regionHeight/sensitivityLevel.",
                        LiveResponse = "GET child/list/properties variants return 404; PUT /detectionRegion/1 also returns 404.",
                        ReadbackBehavior = "Parent readback remains detectionType=grid with detectionGrid and no detectionRegion.",
                        EvidenceLocation = "artifacts/live-validation/motion-region-closure-live-validation.json"
                    },
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "B - Full parent-object write with region injection",
                        TransportPath = "/NetSDK/Video/motionDetection/channel/1",
                        PayloadShape = "Exact live motion parent object with unchanged detectionType=grid plus a detectionRegion array.",
                        LiveResponse = "PUT returns statusCode=0 OK.",
                        ReadbackBehavior = "Readback drops detectionRegion and remains detectionType=grid.",
                        EvidenceLocation = "artifacts/live-validation/motion-region-closure-live-validation.json"
                    },
                    new DeferredCompositeAttemptRow
                    {
                        AttemptFamily = "C - Full parent-object region-mode switch",
                        TransportPath = "/NetSDK/Video/motionDetection/channel/1",
                        PayloadShape = "Exact live motion parent object with detectionType=region plus a detectionRegion array.",
                        LiveResponse = "PUT returns statusCode=0 OK.",
                        ReadbackBehavior = "Readback after 1s and 3s remains detectionType=grid with no detectionRegion; restore preserves the original grid configuration.",
                        EvidenceLocation = "artifacts/live-validation/motion-region-closure-live-validation.json"
                    }
                ]
            }
        ];
}
