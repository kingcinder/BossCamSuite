using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class RecordingStorePortTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bosscam-recording-port-{Guid.NewGuid():N}");

    [Fact]
    public async Task Recording_store_port_persists_and_reads_recording_profiles()
    {
        Directory.CreateDirectory(_directory);
        var applicationStore = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = Path.Combine(_directory, "recordings.db")
        }));
        await applicationStore.InitializeAsync(CancellationToken.None);
        var port = new ApplicationStoreRecordingStore(applicationStore);
        var profile = new RecordingProfile
        {
            DeviceId = Guid.NewGuid(),
            Name = "Night",
            OutputDirectory = Path.Combine(_directory, "clips")
        };

        await port.SaveRecordingProfilesAsync([profile], CancellationToken.None);
        var profiles = await port.GetRecordingProfilesAsync(profile.DeviceId, CancellationToken.None);

        var saved = Assert.Single(profiles);
        Assert.Equal(profile.Id, saved.Id);
        Assert.Equal(profile.OutputDirectory, saved.OutputDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch { }
    }
}
