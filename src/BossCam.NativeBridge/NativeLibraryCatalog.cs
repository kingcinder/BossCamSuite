namespace BossCam.NativeBridge;

public sealed record NativeLibraryDescriptor(string Name, string Path, bool Exists, string Role);

public static class NativeLibraryCatalog
{
    public static IReadOnlyCollection<NativeLibraryDescriptor> Discover(string? ipcamSuiteDirectory, string? eseeCloudDirectory)
    {
        // The catalog enumerates Windows-only OEM DLLs (NetSdk.dll, the EseeCloud P2P transport
        // DLLs, a vendor ONVIF C helper). On Linux, none of these are apt-installable — see
        // scripts/install-ubuntu-deps.sh, which acknowledges the gap. The catalog intentionally
        // does NOT short-circuit on OperatingSystem.IsWindows(): callers (e.g.
        // VideoTransportAdapters' P2P/LinkVision lookup, NativeInteropProbe's DLL-loadability
        // probe) need to see the full descriptor list with `Exists=false` to make
        // "no native-fallback available" decisions on Linux instead of conflating that with
        // "catalog is broken".
        // CS8619 was raised when callers wrapped DiscoverOnvifStreamsAsync in RunAsync<T?>;
        // removed wrap and let SoapAsync's internal silent-swallow handle per-port tolerance.
        var libraries = new List<NativeLibraryDescriptor>();
        void Add(string? root, string fileName, string role)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var path = Path.Combine(root, fileName);
            libraries.Add(new NativeLibraryDescriptor(fileName, path, File.Exists(path), role));
        }

        Add(ipcamSuiteDirectory, "NetSdk.dll", "Native LAN SDK bridge candidate");
        Add(eseeCloudDirectory, "juanclient-new.dll", "ESEE/Juan P2P transport");
        Add(eseeCloudDirectory, "P2PSDKClient.dll", "KP2P transport");
        Add(eseeCloudDirectory, "LinkVisionGetUrl.dll", "LinkVision URL broker");
        Add(eseeCloudDirectory, "LinkVisionPullStream.dll", "LinkVision pull-stream transport");
        Add(eseeCloudDirectory, "libonvifc.dll", "Vendor ONVIF helper");

        return libraries;
    }
}
