using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class TypedControlWritePolicyTests
{
    [Fact]
    public void Blocks_Invalid_Or_ExpertOnly_Field_Without_Override()
    {
        var field = new NormalizedSettingField
        {
            FieldKey = "ip",
            Validity = FieldValidityState.Invalid,
            ExpertOnly = false,
            WriteVerified = true,
            SupportState = ContractSupportState.Supported
        };

        var decision = TypedControlWritePolicy.Decide(field, grouped: null, expertOverride: false);

        Assert.False(decision.Allowed);
        Assert.Contains("invalid", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_Grouped_Writable_Field_When_Write_Is_Not_Independently_Verified()
    {
        var field = new NormalizedSettingField
        {
            FieldKey = "brightness",
            Validity = FieldValidityState.Proven,
            WriteVerified = false,
            SupportState = ContractSupportState.Supported
        };
        var grouped = new GroupedUnsupportedRetestResult
        {
            FieldKey = "brightness",
            Classification = ForcedFieldClassification.Writable
        };

        var decision = TypedControlWritePolicy.Decide(field, grouped, expertOverride: false);

        Assert.True(decision.Allowed);
        Assert.Contains("grouped", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expert_Override_Allows_A_Field_That_Normal_Mode_Blocks()
    {
        var field = new NormalizedSettingField
        {
            FieldKey = "ip",
            Validity = FieldValidityState.Unverified,
            ExpertOnly = true,
            WriteVerified = false,
            SupportState = ContractSupportState.Unsupported
        };

        var decision = TypedControlWritePolicy.Decide(field, grouped: null, expertOverride: true);

        Assert.True(decision.Allowed);
        Assert.Contains("override", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
