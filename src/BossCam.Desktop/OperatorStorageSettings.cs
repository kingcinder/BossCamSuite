using System.IO;
using System.Text.Json;

namespace BossCam.Desktop;

public sealed record OperatorStorageSettings
{
    public string VideoRecordingPath { get; init; } = string.Empty;
    public string AudioRecordingPath { get; init; } = string.Empty;
    public string ScreenshotStoragePath { get; init; } = string.Empty;

    public static OperatorStorageSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new OperatorStorageSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<OperatorStorageSettings>(File.ReadAllText(path)) ?? new OperatorStorageSettings();
        }
        catch
        {
            return new OperatorStorageSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
