using System.Globalization;
using BossCam.Contracts;

namespace BossCam.Core.Utilities;

/// <summary>
/// Persisted NetSDK family-probe verdict cache.
///
/// A successful deviceInfo probe is expensive and network-dependent, yet every stream
/// request (sources, live-info, manifest, live.ts, live.mjpeg, live.mp4, recordings)
/// resolves through <c>TransportBroker.GetSourcesAsync</c> — which used to re-probe the
/// camera on EVERY call. This helper persists the verdict (marker + proven port + probe
/// timestamp) into the device's store metadata so adapters can skip the probe while the
/// verdict is fresh, re-probe on TTL expiry, and have the verdict invalidated by failed
/// playback. Keys live in <see cref="DeviceIdentity.Metadata"/> so they round-trip
/// through the store's JSON payload column without schema changes.
/// </summary>
public static class NetSdkProbeVerdictCache
{
    /// <summary>Family marker metadata key — must match <see cref="NativeNetSdkStreamAdapter.NativeNetSdkMarker"/>
    /// (the adapter is in Infrastructure; this constant is the Core-side source of truth).</summary>
    public const string MarkerKey = "nativeNetSdk";

    /// <summary>Metadata key holding the proven NetSDK HTTP port (int as invariant string).</summary>
    public const string ProbePortKey = "nativeNetSdkProbePort";

    /// <summary>Metadata key holding the probe time (ISO-8601 round-trip, UTC).</summary>
    public const string ProbedAtKey = "nativeNetSdkProbedAt";

    /// <summary>Metadata key recording whether the RTSP digest handshake proved the MAIN ch0_0.264 path.
    /// "true"/"false"; absent means the verdict predates the handshake round (see the backfill path).</summary>
    public const string RtspMainVerifiedKey = "nativeNetSdkRtspMain";

    /// <summary>Metadata key recording whether the RTSP digest handshake proved the SUB ch0_1.264 path.</summary>
    public const string RtspSubVerifiedKey = "nativeNetSdkRtspSub";

    /// <summary>True when the device carries a family marker at all (fresh or stale).</summary>
    public static bool HasVerdict(DeviceIdentity device)
        => device.Metadata.TryGetValue(MarkerKey, out var marker) && marker == "true";

    /// <summary>
    /// Returns true when the device carries a family verdict stamped within
    /// <paramref name="ttl"/> of <paramref name="now"/>, exposing the proven port.
    /// A missing marker, missing/malformed port, or stale timestamp all return false so
    /// the caller re-probes.
    /// </summary>
    public static bool TryGetFreshProbePort(DeviceIdentity device, TimeSpan ttl, DateTimeOffset now, out int probePort)
    {
        probePort = 0;
        if (!HasVerdict(device)
            || !device.Metadata.TryGetValue(ProbePortKey, out var portText)
            || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out probePort)
            || probePort <= 0
            || !device.Metadata.TryGetValue(ProbedAtKey, out var atText)
            || !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var probedAt))
        {
            return false;
        }

        // Age must be within [0, ttl]: a negative age (corrupted future timestamp — clock skew
        // or a transient bit-flip, a hardening theme of this project) must NOT pin the verdict as
        // fresh forever; it forces a re-probe instead of serving stale sources indefinitely.
        var age = now - probedAt;
        return age >= TimeSpan.Zero && age <= ttl;
    }

    /// <summary>Stamps the verdict (marker + proven port + now) onto the device's metadata.</summary>
    public static void Stamp(DeviceIdentity device, int probePort, DateTimeOffset now)
    {
        device.Metadata[MarkerKey] = "true";
        device.Metadata[ProbePortKey] = probePort.ToString(CultureInfo.InvariantCulture);
        device.Metadata[ProbedAtKey] = now.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Records the RTSP digest-handshake verdict for one stream ("main" = ch0_0.264,
    /// "sub" = ch0_1.264) onto the device's metadata. The value is explicitly "false" on a
    /// failed handshake so a cache hit can gate that path out without re-probing the plane.
    /// An unknown stream key is silently not recorded (never silently written onto the wrong
    /// stream's flag).
    /// </summary>
    public static void StampRtspVerdict(DeviceIdentity device, string streamKey, bool verified)
    {
        if (!TryGetRtspVerdictKey(streamKey, out var key))
        {
            return;
        }

        device.Metadata[key] = verified ? "true" : "false";
    }

    /// <summary>
    /// Reads the recorded RTSP handshake verdict for one stream. Returns false (known=false)
    /// when the stream key is unknown or the key is absent — a verdict persisted before the
    /// handshake round — so the caller can decide to backfill. When the key IS present, verified
    /// is true only for the literal "true" value: any corruption or malformed value resolves to
    /// not-verified (the safe direction for a credential probe).
    /// </summary>
    public static bool TryGetRtspVerdict(DeviceIdentity device, string streamKey, out bool verified)
    {
        verified = false;
        if (!TryGetRtspVerdictKey(streamKey, out var key)
            || !device.Metadata.TryGetValue(key, out var raw))
        {
            return false;
        }

        verified = raw == "true";
        return true;
    }

    private static bool TryGetRtspVerdictKey(string streamKey, out string key)
    {
        if (string.Equals(streamKey, "main", StringComparison.Ordinal))
        {
            key = RtspMainVerifiedKey;
            return true;
        }

        if (string.Equals(streamKey, "sub", StringComparison.Ordinal))
        {
            key = RtspSubVerifiedKey;
            return true;
        }

        key = string.Empty;
        return false;
    }

    /// <summary>Returns a copy of the device with all verdict keys removed (other metadata preserved).</summary>
    public static DeviceIdentity Clear(DeviceIdentity device)
        => device with
        {
            Metadata = device.Metadata
                .Where(pair => pair.Key is not MarkerKey and not ProbePortKey and not ProbedAtKey
                    and not RtspMainVerifiedKey and not RtspSubVerifiedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };

    /// <summary>Stamps the verdict and persists the device back to the store (password stays at rest).</summary>
    public static async Task SaveVerdictAsync(IApplicationStore store, DeviceIdentity device, int probePort, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Stamp(device, probePort, now);
        await store.UpsertDevicesAsync([device], cancellationToken);
    }

    /// <summary>
    /// Removes the persisted verdict for the device (failed-playback / failed-probe path) so the
    /// next source resolution re-probes and fallback tiers are not suppressed by stale knowledge.
    /// No-op when the device carries no verdict.
    /// </summary>
    public static async Task InvalidateAsync(IApplicationStore store, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null || !HasVerdict(device))
        {
            return;
        }

        await store.UpsertDevicesAsync([Clear(device)], cancellationToken);
    }
}
