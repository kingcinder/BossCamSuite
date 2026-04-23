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
    public void WritePlan_Defaults_DoNotRollbackOperatorCompositeSaves()
    {
        var plan = new WritePlan();

        Assert.False(plan.AllowRollback);
        Assert.True(plan.SnapshotBeforeWrite);
        Assert.True(plan.RequireWriteVerification);
    }
}
