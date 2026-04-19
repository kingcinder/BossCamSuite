using System.Runtime.InteropServices;

namespace BossCam.NativeBridge;

public sealed record NativeExportProbe(string ExportName, bool Present);

public sealed record NativeInteropProbeResult(
    string Name,
    string Path,
    bool Exists,
    bool Loaded,
    string? LoadError,
    IReadOnlyCollection<NativeExportProbe> Exports,
    string Role);

public static class NativeInteropProbe
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> ExpectedExports = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["NetSdk.dll"] = ["NET_SDK_Init", "NET_SDK_Login", "NET_SDK_Logout", "NET_SDK_Cleanup"],
        ["juanclient-new.dll"] = ["Init", "Login", "Logout"],
        ["P2PSDKClient.dll"] = ["KP2P_Initialize", "KP2P_DeInitialize"],
        ["LinkVisionGetUrl.dll"] = ["GetStreamUrl", "Init"],
        ["LinkVisionPullStream.dll"] = ["StartPullStream", "StopPullStream"],
        ["libonvifc.dll"] = ["OnvifSearch", "OnvifGetProfiles"]
    };

    public static IReadOnlyCollection<NativeInteropProbeResult> Probe(string? ipcamSuiteDirectory, string? eseeCloudDirectory)
    {
        var discovered = NativeLibraryCatalog.Discover(ipcamSuiteDirectory, eseeCloudDirectory);
        var results = new List<NativeInteropProbeResult>();
        foreach (var library in discovered)
        {
            if (!library.Exists)
            {
                results.Add(new NativeInteropProbeResult(library.Name, library.Path, false, false, "missing", [], library.Role));
                continue;
            }

            IntPtr handle;
            if (!NativeLibrary.TryLoad(library.Path, out handle))
            {
                results.Add(new NativeInteropProbeResult(library.Name, library.Path, true, false, "load-failed", [], library.Role));
                continue;
            }

            try
            {
                var expected = ExpectedExports.TryGetValue(library.Name, out var exports) ? exports : [];
                var exportResults = expected
                    .Select(export => new NativeExportProbe(export, NativeLibrary.TryGetExport(handle, export, out _)))
                    .ToList();

                results.Add(new NativeInteropProbeResult(
                    library.Name,
                    library.Path,
                    true,
                    true,
                    null,
                    exportResults,
                    library.Role));
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }

        return results;
    }
}
