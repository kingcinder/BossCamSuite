using BossCam.Contracts;

namespace BossCam.Core;

public sealed class CapabilityPromotionService(
    IApplicationStore store,
    IEndpointContractCatalog contractCatalog)
{
    public async Task<FirmwareCapabilityProfile?> PromoteForDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        if (fields.Count == 0)
        {
            return null;
        }

        var fingerprint = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? (device is null ? "unknown-firmware" : $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}");

        var contracts = device is null
            ? await contractCatalog.GetContractsAsync(cancellationToken)
            : await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var constraintProfiles = await store.GetFieldConstraintProfilesAsync(fingerprint, cancellationToken);
        var dependencyProfiles = await store.GetDependencyMatrixProfilesAsync(fingerprint, cancellationToken);
        var semantic = await store.GetSemanticWriteObservationsAsync(deviceId, 5000, cancellationToken);

        var profile = new FirmwareCapabilityProfile
        {
            FirmwareFingerprint = fingerprint,
            HardwareModel = device?.HardwareModel,
            SupportedEndpointFamilies = contracts.Select(static contract => contract.Surface.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SupportedSettingGroups = contracts.Select(static contract => contract.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            VerifiedWritableFields = fields.Where(static field => field.WriteVerified && field.SupportState == ContractSupportState.Supported).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            InferredWritableFields = fields.Where(static field => !field.WriteVerified && field.Validity == FieldValidityState.Inferred && field.SupportState != ContractSupportState.Unsupported).Select(static field => field.FieldKey)
                .Concat(constraintProfiles.Where(static profile => profile.Quality == EvidenceQuality.Inferred).Select(static profile => profile.FieldKey))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RebootRequiredFields = contracts.Where(static contract => contract.RequiresRebootToTakeEffect).SelectMany(static contract => contract.Fields.Select(field => field.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DangerousFields = fields.Where(static field => field.DisruptionClass is DisruptionClass.FactoryReset or DisruptionClass.FirmwareUpgrade or DisruptionClass.NetworkChanging).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ExpertOnlyFields = fields.Where(static field => field.ExpertOnly).Select(static field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            NativeFallbackRequiredFields = contracts.Where(static contract => contract.Surface == ContractSurface.NativeFallback).SelectMany(static contract => contract.Fields.Select(field => field.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            UncertainFields = fields.Where(static field => field.SupportState == ContractSupportState.Uncertain || field.Validity is FieldValidityState.Unverified or FieldValidityState.Invalid).Select(static field => field.FieldKey)
                .Concat(semantic.Where(static observation => observation.Status is SemanticWriteStatus.Uncertain or SemanticWriteStatus.ShapeMismatch or SemanticWriteStatus.TransportFailed).Select(static observation => observation.FieldKey))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FullObjectWriteFields = contracts.Where(static contract => contract.ObjectShape.FullObjectWriteRequired).SelectMany(static contract => contract.Fields.Where(field => field.Writable).Select(field => field.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        profile = profile with
        {
            SupportedSettingGroups = profile.SupportedSettingGroups
                .Concat(dependencyProfiles.Select(static dependency => $"{dependency.GroupName}-dependency"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        await store.SaveFirmwareCapabilityProfileAsync(profile, cancellationToken);
        return profile;
    }

    public Task<IReadOnlyCollection<FirmwareCapabilityProfile>> GetProfilesAsync(CancellationToken cancellationToken)
        => store.GetFirmwareCapabilityProfilesAsync(cancellationToken);
}
