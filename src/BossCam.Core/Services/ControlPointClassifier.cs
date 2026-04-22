using BossCam.Contracts;

namespace BossCam.Core;

internal sealed record ControlPointDescriptor
{
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public ContractFieldKind Kind { get; init; } = ContractFieldKind.Opaque;
    public bool Writable { get; init; }
    public bool ExpertOnly { get; init; }
    public bool FullObjectWriteRequired { get; init; }
    public bool PartialWriteAllowed { get; init; }
    public bool IsSyntheticRoot { get; init; }
    public IReadOnlyCollection<string> EnumValues { get; init; } = [];
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public IReadOnlyCollection<FieldDependencyRule> DependencyRules { get; init; } = [];
    public ForcedFieldClassification? GroupedClassification { get; init; }
    public GroupedApplyBehavior? GroupedBehavior { get; init; }
    public string? ExistingWidget { get; init; }
}

internal sealed record ControlPointTypingResult
{
    public ControlPointPrimitiveType PrimitiveType { get; init; } = ControlPointPrimitiveType.Unknown;
    public ControlPointValueType? ControlType { get; init; }
    public IReadOnlyCollection<ControlPointTrait> Traits { get; init; } = [];
    public IReadOnlyCollection<string> AllowedValues { get; init; } = [];
    public string? RequiredFormat { get; init; }
    public bool ValuesBounded { get; init; }
    public bool InterFieldDependent { get; init; }
    public bool GroupedWriteRequired { get; init; }
    public string WriteShape { get; init; } = string.Empty;
    public ControlPointWidgetKind RecommendedWidget { get; init; } = ControlPointWidgetKind.HiddenInNormalUi;
    public bool NormalUiEligible { get; init; }
    public string? TypeBlocker { get; init; }
}

internal static class ControlPointClassifier
{
    private static readonly HashSet<string> SliderFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "brightness",
        "contrast",
        "saturation",
        "sharpness",
        "hue",
        "gamma",
        "manualSharpness",
        "denoise",
        "denoise3dStrength",
        "wdr",
        "wdrStrength",
        "whiteLight",
        "infrared",
        "motionSensitivity"
    };

    private static readonly HashSet<string> CompositeProxyFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ports",
        "privacyMaskEnabled",
        "privacyMaskX",
        "privacyMaskY",
        "privacyMaskWidth",
        "privacyMaskHeight",
        "motionGrid",
        "motionRegion",
        "userList"
    };

    private static readonly HashSet<string> HigherOrderFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ports",
        "motionRegion",
        "userList"
    };

    private static readonly HashSet<string> FreeformFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "channelName",
        "osd",
        "osdChannelNameText",
        "apSsid",
        "apPsk",
        "wirelessApBssId",
        "wirelessApEssId",
        "wirelessApPsk",
        "wirelessEssId",
        "wirelessPsk",
        "pppoeUserName",
        "pppoePassword",
        "deviceName",
        "username",
        "newPassword",
        "ntpServerDomain",
        "esee",
        "eseeId",
        "serial",
        "model",
        "firmware",
        "mac"
    };

    public static ControlPointTypingResult Classify(ControlPointDescriptor descriptor)
    {
        var primitive = MapPrimitive(descriptor.Kind);
        var allowedValues = descriptor.EnumValues.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var bounded = descriptor.Min is not null || descriptor.Max is not null;
        var interFieldDependent = descriptor.DependencyRules.Count > 0 || IsEncodeDependencyField(descriptor.FieldKey, descriptor.ContractKey);
        var groupedWriteRequired = descriptor.FullObjectWriteRequired || descriptor.GroupedClassification == ForcedFieldClassification.RequiresGroupedWrite;
        var controlType = DetermineControlType(descriptor, allowedValues, interFieldDependent);
        var traits = BuildTraits(descriptor, allowedValues, bounded, interFieldDependent, groupedWriteRequired);
        var widget = RecommendWidget(descriptor, controlType, primitive, bounded);
        var blocker = controlType is null
            ? "Insufficient schema evidence to safely pick a widget."
            : null;

        return new ControlPointTypingResult
        {
            PrimitiveType = primitive,
            ControlType = controlType,
            Traits = traits,
            AllowedValues = allowedValues,
            RequiredFormat = DetermineFormat(descriptor),
            ValuesBounded = bounded,
            InterFieldDependent = interFieldDependent,
            GroupedWriteRequired = groupedWriteRequired,
            WriteShape = DetermineWriteShape(controlType, descriptor, interFieldDependent),
            RecommendedWidget = controlType is null ? ControlPointWidgetKind.HiddenInNormalUi : widget,
            NormalUiEligible = controlType is not null,
            TypeBlocker = blocker
        };
    }

    public static string ToLegacyEditorKind(ControlPointTypingResult typing)
        => typing.RecommendedWidget switch
        {
            ControlPointWidgetKind.Toggle => "bool",
            ControlPointWidgetKind.Dropdown or ControlPointWidgetKind.Checklist => "enum",
            ControlPointWidgetKind.NumericInput or ControlPointWidgetKind.Slider => "number",
            ControlPointWidgetKind.TextInput => "text",
            ControlPointWidgetKind.StructuredPanel => "object",
            ControlPointWidgetKind.DependencyPanel => "composite",
            _ => "text"
        };

    private static ControlPointValueType? DetermineControlType(
        ControlPointDescriptor descriptor,
        IReadOnlyCollection<string> allowedValues,
        bool interFieldDependent)
    {
        if (descriptor.IsSyntheticRoot)
        {
            return descriptor.FullObjectWriteRequired && (interFieldDependent || IsHigherOrderContract(descriptor.ContractKey, descriptor.Endpoint))
                ? ControlPointValueType.HigherOrderComposite
                : ControlPointValueType.CompositeControl;
        }

        if (HigherOrderFieldKeys.Contains(descriptor.FieldKey))
        {
            return ControlPointValueType.HigherOrderComposite;
        }

        if (CompositeProxyFieldKeys.Contains(descriptor.FieldKey))
        {
            return descriptor.FieldKey.Equals("userList", StringComparison.OrdinalIgnoreCase)
                ? ControlPointValueType.HigherOrderComposite
                : ControlPointValueType.CompositeControl;
        }

        if (descriptor.Kind == ContractFieldKind.Object)
        {
            return ControlPointValueType.CompositeControl;
        }

        if (descriptor.Kind == ContractFieldKind.Array)
        {
            return descriptor.SourcePath.Contains('[', StringComparison.Ordinal)
                || descriptor.FieldKey.Contains("region", StringComparison.OrdinalIgnoreCase)
                || descriptor.FieldKey.Contains("user", StringComparison.OrdinalIgnoreCase)
                ? ControlPointValueType.HigherOrderComposite
                : ControlPointValueType.CompositeControl;
        }

        if (descriptor.Kind == ContractFieldKind.Boolean)
        {
            return ControlPointValueType.BooleanToggle;
        }

        if (allowedValues.Count > 0 || descriptor.Kind == ContractFieldKind.Enum)
        {
            return ControlPointValueType.SingleSelectSet;
        }

        if (descriptor.Kind is ContractFieldKind.Number or ContractFieldKind.Integer or ContractFieldKind.Port)
        {
            return ControlPointValueType.ScalarOrCodeValue;
        }

        if (descriptor.Kind == ContractFieldKind.IpAddress)
        {
            return ControlPointValueType.ScalarOrCodeValue;
        }

        if (descriptor.Kind == ContractFieldKind.Password || FreeformFieldKeys.Contains(descriptor.FieldKey))
        {
            return ControlPointValueType.FreeformSemanticValue;
        }

        if (descriptor.Kind is ContractFieldKind.String or ContractFieldKind.Opaque)
        {
            if (descriptor.FieldKey.Contains("resolution", StringComparison.OrdinalIgnoreCase)
                || descriptor.FieldKey.Contains("format", StringComparison.OrdinalIgnoreCase)
                || descriptor.FieldKey.Contains("type", StringComparison.OrdinalIgnoreCase)
                || descriptor.FieldKey.Contains("mode", StringComparison.OrdinalIgnoreCase))
            {
                return ControlPointValueType.ScalarOrCodeValue;
            }

            return ControlPointValueType.FreeformSemanticValue;
        }

        return null;
    }

    private static IReadOnlyCollection<ControlPointTrait> BuildTraits(
        ControlPointDescriptor descriptor,
        IReadOnlyCollection<string> allowedValues,
        bool bounded,
        bool interFieldDependent,
        bool groupedWriteRequired)
    {
        var traits = new List<ControlPointTrait>();
        if (bounded)
        {
            traits.Add(ControlPointTrait.Bounded);
        }

        if (allowedValues.Count > 0)
        {
            traits.Add(ControlPointTrait.EnumBacked);
        }

        if (descriptor.Kind is ContractFieldKind.String or ContractFieldKind.Password or ContractFieldKind.IpAddress or ContractFieldKind.Opaque)
        {
            traits.Add(ControlPointTrait.StringCoded);
        }

        if (groupedWriteRequired)
        {
            traits.Add(ControlPointTrait.GroupedWriteRequired);
        }

        if (descriptor.GroupedBehavior is GroupedApplyBehavior.DelayedApplied
            or GroupedApplyBehavior.RequiresSecondWrite
            or GroupedApplyBehavior.RequiresRelatedFieldWrite
            or GroupedApplyBehavior.RequiresCommitTrigger)
        {
            traits.Add(ControlPointTrait.CommitTriggerSensitive);
        }

        if (descriptor.GroupedClassification == ForcedFieldClassification.WritableNeedsCommitTrigger
            || descriptor.GroupedClassification == ForcedFieldClassification.RequiresCommitTrigger)
        {
            traits.Add(ControlPointTrait.CommitTriggerSensitive);
        }

        if (descriptor.GroupedClassification == ForcedFieldClassification.WritableNeedsCommitTrigger
            || descriptor.SourcePath.Equals("$", StringComparison.Ordinal))
        {
            traits.Add(ControlPointTrait.EndpointDependent);
        }

        if (interFieldDependent)
        {
            traits.Add(ControlPointTrait.InterFieldDependent);
        }

        return traits.Distinct().ToArray();
    }

    private static ControlPointWidgetKind RecommendWidget(
        ControlPointDescriptor descriptor,
        ControlPointValueType? controlType,
        ControlPointPrimitiveType primitive,
        bool bounded)
    {
        return controlType switch
        {
            ControlPointValueType.BooleanToggle => ControlPointWidgetKind.Toggle,
            ControlPointValueType.SingleSelectSet => ControlPointWidgetKind.Dropdown,
            ControlPointValueType.MultiSelectSet => ControlPointWidgetKind.Checklist,
            ControlPointValueType.FreeformSemanticValue => ControlPointWidgetKind.TextInput,
            ControlPointValueType.CompositeControl => ControlPointWidgetKind.StructuredPanel,
            ControlPointValueType.HigherOrderComposite => ControlPointWidgetKind.DependencyPanel,
            ControlPointValueType.ScalarOrCodeValue when primitive is ControlPointPrimitiveType.Integer or ControlPointPrimitiveType.Float
                => SliderFieldKeys.Contains(descriptor.FieldKey) && bounded
                    ? ControlPointWidgetKind.Slider
                    : ControlPointWidgetKind.NumericInput,
            ControlPointValueType.ScalarOrCodeValue => ControlPointWidgetKind.TextInput,
            _ => ControlPointWidgetKind.HiddenInNormalUi
        };
    }

    private static string DetermineWriteShape(ControlPointValueType? controlType, ControlPointDescriptor descriptor, bool interFieldDependent)
        => controlType switch
        {
            ControlPointValueType.CompositeControl => descriptor.FullObjectWriteRequired ? "full-object composite write" : "structured object write",
            ControlPointValueType.HigherOrderComposite => descriptor.FullObjectWriteRequired || interFieldDependent
                ? "dependency-aware grouped transaction"
                : "ordered composite write",
            _ when descriptor.FullObjectWriteRequired => "grouped full-object write",
            _ when descriptor.PartialWriteAllowed => "partial/scalar write",
            _ => "scalar write"
        };

    private static string? DetermineFormat(ControlPointDescriptor descriptor)
    {
        if (descriptor.Kind == ContractFieldKind.IpAddress)
        {
            return "IPv4 address";
        }

        if (descriptor.Kind == ContractFieldKind.Port)
        {
            return "TCP/UDP port number";
        }

        if (descriptor.Kind == ContractFieldKind.Password)
        {
            return "credential string";
        }

        if (descriptor.FieldKey.Contains("resolution", StringComparison.OrdinalIgnoreCase))
        {
            return "WIDTHxHEIGHT token";
        }

        if (descriptor.FieldKey.Contains("dateFormat", StringComparison.OrdinalIgnoreCase))
        {
            return "vendor date-format token";
        }

        if (descriptor.FieldKey.Contains("timeFormat", StringComparison.OrdinalIgnoreCase))
        {
            return "vendor time-format token";
        }

        if (descriptor.FieldKey.Contains("mac", StringComparison.OrdinalIgnoreCase))
        {
            return "MAC address";
        }

        if (descriptor.FieldKey.Contains("serial", StringComparison.OrdinalIgnoreCase))
        {
            return "device serial token";
        }

        return null;
    }

    private static ControlPointPrimitiveType MapPrimitive(ContractFieldKind kind)
        => kind switch
        {
            ContractFieldKind.Boolean => ControlPointPrimitiveType.Boolean,
            ContractFieldKind.Integer or ContractFieldKind.Port => ControlPointPrimitiveType.Integer,
            ContractFieldKind.Number => ControlPointPrimitiveType.Float,
            ContractFieldKind.Object => ControlPointPrimitiveType.Object,
            ContractFieldKind.Array => ControlPointPrimitiveType.Array,
            ContractFieldKind.String or ContractFieldKind.Enum or ContractFieldKind.IpAddress or ContractFieldKind.Password or ContractFieldKind.Opaque
                => ControlPointPrimitiveType.String,
            _ => ControlPointPrimitiveType.Unknown
        };

    private static bool IsEncodeDependencyField(string fieldKey, string contractKey)
        => contractKey.StartsWith("video.encode", StringComparison.OrdinalIgnoreCase)
            || fieldKey is "codec" or "profile" or "resolution" or "bitrateMode" or "bitrate" or "frameRate" or "keyframeInterval";

    private static bool IsHigherOrderContract(string contractKey, string endpoint)
        => contractKey.StartsWith("video.encode", StringComparison.OrdinalIgnoreCase)
            || contractKey.StartsWith("network.interfaces", StringComparison.OrdinalIgnoreCase)
            || contractKey.StartsWith("network.wireless", StringComparison.OrdinalIgnoreCase)
            || contractKey.StartsWith("alarm.", StringComparison.OrdinalIgnoreCase)
            || contractKey.StartsWith("storage.", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains("/Video/encode/", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains("/Network/interfaces", StringComparison.OrdinalIgnoreCase);
}
