using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Decides whether a normalized control point may be written by the normal operator path.
/// It contains no transport, persistence, or logging concerns; callers own those effects.
/// </summary>
public static class TypedControlWritePolicy
{
    public static TypedControlWriteDecision Decide(
        NormalizedSettingField field,
        GroupedUnsupportedRetestResult? grouped,
        bool expertOverride)
    {
        if (expertOverride)
        {
            return new(true, "Allowed by expert override.");
        }

        if (field.Validity == FieldValidityState.Invalid)
        {
            return new(false, "Field is invalid.");
        }

        if (field.ExpertOnly)
        {
            return new(false, "Field is expert-only.");
        }

        if (field.WriteVerified && field.SupportState == ContractSupportState.Supported)
        {
            return new(true, "Allowed by verified writable support.");
        }

        if (grouped?.Classification is ForcedFieldClassification.Writable
            or ForcedFieldClassification.WritableNeedsCommitTrigger
            or ForcedFieldClassification.DelayedApply)
        {
            return new(true, "Allowed by grouped writable evidence.");
        }

        return new(false, "Write is not proven, grouped-tested writable, and supported.");
    }
}

public sealed record TypedControlWriteDecision(bool Allowed, string Reason);
