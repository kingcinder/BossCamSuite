using System.Net;
using System.Text.Json.Nodes;
using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Pure contract validation and typed conversion for operator control payloads.
/// Transport, persistence, audit, and semantic verification stay in the orchestration module.
/// </summary>
public static class TypedPayloadValidationPolicy
{
    public static ContractValidationResult Validate(
        EndpointContract contract,
        JsonObject payload,
        IReadOnlyCollection<string> changedFields,
        bool expertOverride)
    {
        var errors = new List<string>();
        if (!contract.ObjectShape.PartialWriteAllowed && changedFields.Count > 1 && !contract.ObjectShape.FullObjectWriteRequired)
        {
            errors.Add("partial multi-field write attempted on endpoint without partial-write support");
        }

        foreach (var required in contract.ObjectShape.RequiredRootFields)
        {
            if (GetPathValue(payload, $"$.{required}") is null)
            {
                errors.Add($"required root field '{required}' missing");
            }
        }

        foreach (var requiredField in contract.Fields.Where(static item => item.Required))
        {
            if (GetPathValue(payload, requiredField.SourcePath) is null)
            {
                errors.Add($"required field '{requiredField.Key}' missing at {requiredField.SourcePath}");
            }
        }

        if (contract.ObjectShape.FullObjectWriteRequired && payload.Count == 0)
        {
            errors.Add("full object payload required but snapshot payload is empty");
        }

        foreach (var field in contract.Fields)
        {
            var node = GetPathValue(payload, field.SourcePath);
            if (node is null)
            {
                if (field.Required) errors.Add($"required field '{field.Key}' missing");
                continue;
            }

            var converted = Convert(node, field);
            if (!converted.Success) errors.Add($"{field.Key}: {converted.Message}");
        }

        if (contract.ContractKey.Equals("network.interfaces", StringComparison.OrdinalIgnoreCase))
        {
            var dhcp = GetPathValue(payload, "$.dhcp");
            var dhcpEnabled = dhcp is not null && bool.TryParse(dhcp.ToJsonString().Trim('"'), out var parsedDhcp) && parsedDhcp;
            if (!dhcpEnabled)
            {
                if (GetPathValue(payload, "$.gateway") is null) errors.Add("gateway is required when dhcpMode is false");
                if (GetPathValue(payload, "$.dns") is null) errors.Add("dns is required when dhcpMode is false");
            }
        }

        if (contract.ContractKey.Equals("network.wireless", StringComparison.OrdinalIgnoreCase))
        {
            var mode = GetPathValue(payload, "$.wirelessMode")?.ToJsonString().Trim('"');
            var apMode = GetPathValue(payload, "$.ap.mode")?.ToJsonString().Trim('"');
            if (string.Equals(mode, "AP", StringComparison.OrdinalIgnoreCase) || string.Equals(apMode, "On", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(GetPathValue(payload, "$.ap.ssid")?.ToJsonString().Trim('"'))) errors.Add("apSsid is required when AP mode is enabled");
                if (string.IsNullOrWhiteSpace(GetPathValue(payload, "$.ap.psk")?.ToJsonString().Trim('"'))) errors.Add("apPsk is required when AP mode is enabled");
            }
        }

        return new ContractValidationResult
        {
            IsValid = errors.Count == 0,
            Blocked = errors.Count > 0 && !expertOverride,
            ExpertOverrideUsed = expertOverride,
            ContractKey = contract.ContractKey,
            Endpoint = contract.Endpoint,
            Errors = errors
        };
    }

    public static (bool Success, JsonNode? Value, string? Message) Convert(JsonNode source, ContractField field)
    {
        try
        {
            return field.Kind switch
            {
                ContractFieldKind.Number => Number(source, field),
                ContractFieldKind.Integer => Integer(source, field),
                ContractFieldKind.Boolean => Boolean(source),
                ContractFieldKind.Enum => (true, JsonValue.Create(source.ToJsonString().Trim('"')), null),
                ContractFieldKind.IpAddress => IPAddress.TryParse(source.ToJsonString().Trim('"'), out _)
                    ? (true, JsonValue.Create(source.ToJsonString().Trim('"')), null)
                    : (false, null, "invalid IP address"),
                ContractFieldKind.Port => Integer(source, new ContractField { Validation = new ContractValidationRule { Min = 1, Max = 65535 } }),
                _ => (true, source.DeepClone(), null)
            };
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static JsonNode? GetPathValue(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path)) return null;
        var current = root;
        foreach (var segment in ParsePath(path))
        {
            if (segment.Index is int index)
            {
                if (current is not JsonArray array || index < 0 || index >= array.Count) return null;
                current = array[index];
            }
            else
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name!, out current)) return null;
            }
            if (current is null) return null;
        }
        return current;
    }

    private static (bool Success, JsonNode? Value, string? Message) Number(JsonNode source, ContractField field)
    {
        if (source is not JsonValue node || !node.TryGetValue<decimal>(out var value))
        {
            if (!decimal.TryParse(source.ToJsonString().Trim('"'), out value)) return (false, null, "expected numeric value");
        }
        if (field.Validation.Min is decimal min && value < min) return (false, null, $"value below min {min}");
        if (field.Validation.Max is decimal max && value > max) return (false, null, $"value above max {max}");
        return (true, JsonValue.Create(value), null);
    }

    private static (bool Success, JsonNode? Value, string? Message) Integer(JsonNode source, ContractField field)
    {
        var number = Number(source, field);
        if (!number.Success || number.Value is null) return number;
        if (number.Value is not JsonValue node || !node.TryGetValue<decimal>(out var value) || value % 1 != 0) return (false, null, "expected integer value");
        return (true, JsonValue.Create((int)value), null);
    }

    private static (bool Success, JsonNode? Value, string? Message) Boolean(JsonNode source)
    {
        if (source is JsonValue node && node.TryGetValue<bool>(out var value)) return (true, JsonValue.Create(value), null);
        var raw = source.ToJsonString().Trim('"');
        if (int.TryParse(raw, out var integer)) return (true, JsonValue.Create(integer != 0), null);
        if (bool.TryParse(raw, out value)) return (true, JsonValue.Create(value), null);
        if (raw.Equals("on", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)) return (true, JsonValue.Create(true), null);
        if (raw.Equals("off", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase)) return (true, JsonValue.Create(false), null);
        if (source is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("enabled", out var enabled) && enabled is not null) return Boolean(enabled);
            if (obj.TryGetPropertyValue("Enable", out var enable) && enable is not null) return Boolean(enable);
        }
        return (false, null, $"expected boolean value but got '{raw}'");
    }

    private static IReadOnlyCollection<PathSegment> ParsePath(string path)
    {
        var cleaned = path.Trim();
        if (cleaned.StartsWith("$.", StringComparison.Ordinal)) cleaned = cleaned[2..];
        else if (cleaned.StartsWith("$", StringComparison.Ordinal)) cleaned = cleaned[1..];
        var segments = new List<PathSegment>();
        foreach (var raw in cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var open = raw.IndexOf('[', StringComparison.Ordinal);
            if (open >= 0)
            {
                var close = raw.IndexOf(']', open);
                if (open > 0) segments.Add(new PathSegment(raw[..open], null));
                if (close > open && int.TryParse(raw[(open + 1)..close], out var index)) segments.Add(new PathSegment(null, index));
            }
            else segments.Add(new PathSegment(raw, null));
        }
        return segments;
    }

    private sealed record PathSegment(string? Name, int? Index);
}
