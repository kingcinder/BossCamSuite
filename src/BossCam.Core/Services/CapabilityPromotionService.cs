using BossCam.Contracts;

namespace BossCam.Core;

public sealed class CapabilityPromotionService(IApplicationStore store)
{
    public async Task<FirmwareCapabilityProfile?> PromoteForDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        if (fields.Count == 0)
        {
            return null;
        }

        var fingerprint = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? "unknown-firmware";

        var profile = new FirmwareCapabilityProfile
        {
            FirmwareFingerprint = fingerprint,
            SupportedEndpointFamilies = fields.Select(static field => field.SourceEndpoint.StartsWith("/NetSDK/", StringComparison.OrdinalIgnoreCase) ? "NETSDK" : "PrivateCGI").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SupportedSettingGroups = fields.Select(static field => field.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            VerifiedWritableFields = fields.Where(static field => field.WriteVerified).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RebootRequiredFields = fields.Where(static field => field.DisruptionClass == DisruptionClass.Reboot).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DangerousFields = fields.Where(static field => field.DisruptionClass is DisruptionClass.FactoryReset or DisruptionClass.FirmwareUpgrade or DisruptionClass.NetworkChanging).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ExpertOnlyFields = fields.Where(static field => field.ExpertOnly).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            NativeFallbackRequiredFields = [],
            UncertainFields = fields.Where(static field => field.Validity is FieldValidityState.Unverified or FieldValidityState.Inferred).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        await store.SaveFirmwareCapabilityProfileAsync(profile, cancellationToken);
        return profile;
    }

    public Task<IReadOnlyCollection<FirmwareCapabilityProfile>> GetProfilesAsync(CancellationToken cancellationToken)
        => store.GetFirmwareCapabilityProfilesAsync(cancellationToken);
}
