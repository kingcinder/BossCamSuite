using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class TypedControlStorePortTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bosscam-typed-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task Typed_control_store_port_persists_normalized_fields_through_application_store()
    {
        Directory.CreateDirectory(_directory);
        var applicationStore = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = Path.Combine(_directory, "typed.db")
        }));
        await applicationStore.InitializeAsync(CancellationToken.None);
        var port = new ApplicationStoreTypedControlStore(applicationStore);
        var deviceId = Guid.NewGuid();
        var field = new NormalizedSettingField
        {
            DeviceId = deviceId,
            FieldKey = "brightness",
            GroupKind = TypedSettingGroupKind.VideoImage,
            GroupName = "Video / Image",
            DisplayName = "Brightness",
            SourceEndpoint = "/video/input",
            TypedValue = System.Text.Json.Nodes.JsonValue.Create(50)
        };

        await port.SaveNormalizedSettingFieldsAsync([field], CancellationToken.None);
        var fields = await port.GetNormalizedSettingFieldsAsync(deviceId, CancellationToken.None);

        var saved = Assert.Single(fields);
        Assert.Equal(field.Id, saved.Id);
        Assert.Equal("brightness", saved.FieldKey);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch { }
    }
}
