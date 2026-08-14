using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Server-side star (pinned-to-landing) persistence. This is the authoritative set that
/// both the web SPA and the desktop app mirror, so it must survive restarts and round-trip
/// cleanly across star/unstar cycles.
/// </summary>
public sealed class StarredDevicesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bosscam-stars-{Guid.NewGuid():N}");

    private SqliteApplicationStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = Path.Combine(_directory, "stars.db")
        }));
    }

    [Fact]
    public async Task Empty_by_default()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        Assert.Empty(await store.GetStarredDeviceIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Star_then_unstar_round_trips()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var deviceId = Guid.NewGuid();

        await store.SetDeviceStarredAsync(deviceId, true, CancellationToken.None);
        var starred = await store.GetStarredDeviceIdsAsync(CancellationToken.None);
        Assert.Contains(deviceId, starred);

        await store.SetDeviceStarredAsync(deviceId, false, CancellationToken.None);
        var afterUnstar = await store.GetStarredDeviceIdsAsync(CancellationToken.None);
        Assert.DoesNotContain(deviceId, afterUnstar);
    }

    [Fact]
    public async Task Multiple_stars_persist_and_survive_reopen()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        foreach (var id in ids)
        {
            await store.SetDeviceStarredAsync(id, true, CancellationToken.None);
        }
        // Re-starring an already-starred id must not duplicate or drop others.
        await store.SetDeviceStarredAsync(ids[0], true, CancellationToken.None);

        // Fresh store instance on the same DB file ⇒ persisted across "restart".
        var reopened = CreateStore();
        await reopened.InitializeAsync(CancellationToken.None);

        var loaded = (await reopened.GetStarredDeviceIdsAsync(CancellationToken.None)).ToList();
        Assert.Equal(ids.Count, loaded.Count);
        foreach (var id in ids)
        {
            Assert.Contains(id, loaded);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch { }
    }
}
