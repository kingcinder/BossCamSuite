using BossCam.Contracts;

namespace BossCam.Tests;

public sealed class CompositeInteractionRulesTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StaticNetworkVisibility_FollowsDhcpState(bool dhcpEnabled, bool expectedVisible)
    {
        Assert.Equal(expectedVisible, CompositeInteractionRules.IsStaticNetworkConfigurationVisible(dhcpEnabled));
    }

    [Theory]
    [InlineData("AP", "Off", true)]
    [InlineData("Station", "On", true)]
    [InlineData("Disabled", "Off", false)]
    public void WirelessApVisibility_TracksEitherApBranch(string wirelessMode, string apMode, bool expectedVisible)
    {
        Assert.Equal(expectedVisible, CompositeInteractionRules.IsWirelessApConfigurationVisible(wirelessMode, apMode));
        Assert.Equal(expectedVisible, CompositeInteractionRules.RequiresWirelessCredentials(wirelessMode, apMode));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void MotionAndPrivacyVisibility_FollowEnableFlags(bool enabled, bool expectedVisible)
    {
        Assert.Equal(expectedVisible, CompositeInteractionRules.IsMotionConfigurationVisible(enabled));
        Assert.Equal(expectedVisible, CompositeInteractionRules.IsPrivacyMaskRegionVisible(enabled));
    }

    [Fact]
    public void CoverageSummary_ContainsStructuredSystemsWithBoundedPermutations()
    {
        var rows = CompositeInteractionRules.BuildCoverageSummary();

        Assert.True(rows.Count >= 7);
        Assert.Contains(rows, row => row.SystemKey == "network.interface" && row.ValidStateCount == 8);
        Assert.Contains(rows, row => row.SystemKey == "network.wireless" && row.ValidStateCount == 4);
        Assert.Contains(rows, row => row.SystemKey == "video.overlay" && row.ValidStateCount == 15);
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.SystemName));
            Assert.False(string.IsNullOrWhiteSpace(row.BoundedBy));
            Assert.False(string.IsNullOrWhiteSpace(row.Rules));
        });
    }

    [Fact]
    public void DeferredObjectiveLedger_ClosesPrivacyMaskWithDistinctEvidenceFamilies()
    {
        var ledger = CompositeInteractionRules.BuildDeferredObjectiveLedger();
        var privacy = ledger.Single(row => row.SystemKey == "video.privacy.mask");

        Assert.Equal("Blocked", privacy.Status);
        Assert.Contains("Invalid Document", privacy.ExactFailureReason);
        Assert.Contains("readback remains unchanged", privacy.ExactFailureReason);
        Assert.Contains("native/private bridge has no callable", privacy.ExactFailureReason);
        Assert.Equal(4, privacy.Attempts.Count);
        Assert.Contains(privacy.Attempts, attempt => attempt.AttemptFamily.StartsWith("A - REST", StringComparison.Ordinal));
        Assert.Contains(privacy.Attempts, attempt => attempt.AttemptFamily.StartsWith("B - Full parent", StringComparison.Ordinal));
        Assert.Contains(privacy.Attempts, attempt => attempt.AttemptFamily.StartsWith("C - Alternate", StringComparison.Ordinal));
        Assert.Contains(privacy.Attempts, attempt => attempt.AttemptFamily.StartsWith("D - Native", StringComparison.Ordinal));
        Assert.All(privacy.Attempts, attempt =>
        {
            Assert.False(string.IsNullOrWhiteSpace(attempt.TransportPath));
            Assert.False(string.IsNullOrWhiteSpace(attempt.PayloadShape));
            Assert.False(string.IsNullOrWhiteSpace(attempt.LiveResponse));
            Assert.False(string.IsNullOrWhiteSpace(attempt.ReadbackBehavior));
            Assert.False(string.IsNullOrWhiteSpace(attempt.EvidenceLocation));
        });
    }

    [Fact]
    public void DeferredObjectiveLedger_ClosesMotionRegionWithDistinctEvidenceFamilies()
    {
        var ledger = CompositeInteractionRules.BuildDeferredObjectiveLedger();
        var motionRegion = ledger.Single(row => row.SystemKey == "motion.detection.region");

        Assert.Equal("Blocked", motionRegion.Status);
        Assert.Contains("child region endpoints return 404", motionRegion.ExactFailureReason);
        Assert.Contains("readback remains grid", motionRegion.ExactFailureReason);
        Assert.Equal(3, motionRegion.Attempts.Count);
        Assert.Contains(motionRegion.Attempts, attempt => attempt.AttemptFamily.StartsWith("A - REST", StringComparison.Ordinal));
        Assert.Contains(motionRegion.Attempts, attempt => attempt.AttemptFamily.StartsWith("B - Full parent", StringComparison.Ordinal));
        Assert.Contains(motionRegion.Attempts, attempt => attempt.AttemptFamily.StartsWith("C - Full parent", StringComparison.Ordinal));
        Assert.All(motionRegion.Attempts, attempt =>
        {
            Assert.False(string.IsNullOrWhiteSpace(attempt.TransportPath));
            Assert.False(string.IsNullOrWhiteSpace(attempt.PayloadShape));
            Assert.False(string.IsNullOrWhiteSpace(attempt.LiveResponse));
            Assert.False(string.IsNullOrWhiteSpace(attempt.ReadbackBehavior));
            Assert.False(string.IsNullOrWhiteSpace(attempt.EvidenceLocation));
        });
    }

    [Fact]
    public void WritePlan_Defaults_DoNotRollbackOperatorCompositeSaves()
    {
        var plan = new WritePlan();

        Assert.False(plan.AllowRollback);
        Assert.True(plan.SnapshotBeforeWrite);
        Assert.True(plan.RequireWriteVerification);
    }
}
