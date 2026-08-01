using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class TypedPayloadValidationPolicyTests
{
    [Fact]
    public void Rejects_missing_required_root_field_without_expert_override()
    {
        var contract = new EndpointContract
        {
            ContractKey = "network.interfaces",
            ObjectShape = new ContractObjectShape
            {
                RequiredRootFields = ["ip", "gateway"],
                FullObjectWriteRequired = true
            },
            Fields =
            [
                new ContractField { Key = "ip", SourcePath = "$.ip", Kind = ContractFieldKind.IpAddress, Required = true }
            ]
        };

        var result = TypedPayloadValidationPolicy.Validate(
            contract,
            new JsonObject { ["ip"] = "192.168.1.10" },
            ["ip"],
            expertOverride: false);

        Assert.False(result.IsValid);
        Assert.True(result.Blocked);
        Assert.Contains(result.Errors, error => error.Contains("gateway", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Allows_contract_errors_with_expert_override_but_records_them()
    {
        var contract = new EndpointContract
        {
            ContractKey = "video.input",
            ObjectShape = new ContractObjectShape { RequiredRootFields = ["id"] }
        };

        var result = TypedPayloadValidationPolicy.Validate(
            contract,
            new JsonObject(),
            ["brightness"],
            expertOverride: true);

        Assert.False(result.IsValid);
        Assert.False(result.Blocked);
        Assert.True(result.ExpertOverrideUsed);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Converts_and_range_checks_integer_contract_values()
    {
        var field = new ContractField
        {
            Key = "brightness",
            Kind = ContractFieldKind.Integer,
            Validation = new ContractValidationRule { Min = 0, Max = 100 }
        };

        var converted = TypedPayloadValidationPolicy.Convert(JsonValue.Create(61), field);
        var rejected = TypedPayloadValidationPolicy.Convert(JsonValue.Create(101), field);

        Assert.True(converted.Success);
        Assert.Equal(61, converted.Value!.GetValue<int>());
        Assert.False(rejected.Success);
        Assert.Contains("max", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }
}
