namespace BossCam.NativeBridge;

public sealed record NativeLibraryDescriptor(string Name, string Path, bool Exists, string Role);

public static class NativeLibraryCatalog
{
    public static IReadOnlyCollection<NativeLibraryDescriptor> Discover(string? ipcamSuiteDirectory, string? eseeCloudDirectory)
    {
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
