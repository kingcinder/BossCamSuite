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

public static class CompositeInteractionRules
{
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
}
