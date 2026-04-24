using BossCam.Contracts;

namespace BossCam.Tests;

public sealed class NvrLayerTests
{
    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(3, 3, 2, 3)]
    [InlineData(4, 4, 2, 2)]
    [InlineData(5, 5, 3, 3)]
    [InlineData(6, 6, 2, 3)]
    [InlineData(9, 9, 3, 3)]
    public void LayoutCatalog_DefinesExpectedTileSlots(int layout, int expectedSlots, int expectedRows, int expectedColumns)
    {
        var slots = NvrLayoutCatalog.GetSlots(layout);
        var (rows, columns) = NvrLayoutCatalog.GetGridSize(layout);

        Assert.Equal(expectedSlots, slots.Count);
        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedColumns, columns);
        Assert.Equal(expectedSlots, slots.Select(static slot => slot.TileId).Distinct().Count());
        Assert.All(slots, slot =>
        {
            Assert.InRange(slot.Row, 0, rows - 1);
            Assert.InRange(slot.Column, 0, columns - 1);
            Assert.InRange(slot.Row + slot.RowSpan, 1, rows);
            Assert.InRange(slot.Column + slot.ColumnSpan, 1, columns);
        });
    }

    [Fact]
    public void TileStream_TracksUnifiedLiveAndPlaybackState()
    {
        var deviceId = Guid.NewGuid();
        var live = new NvrTileStream
        {
            TileId = 0,
            DeviceId = deviceId,
            Mode = NvrStreamMode.Live,
            Source = "rtsp://camera/live",
            Status = NvrStreamStatus.Running
        };

        var playback = live with
        {
            Mode = NvrStreamMode.Playback,
            Source = "C:\\recordings\\segment.mp4",
            BeginTime = DateTimeOffset.Parse("2026-04-23T08:00:00-07:00"),
            EndTime = DateTimeOffset.Parse("2026-04-23T08:10:00-07:00")
        };

        Assert.Equal(0, playback.TileId);
        Assert.Equal(deviceId, playback.DeviceId);
        Assert.Equal(NvrStreamMode.Playback, playback.Mode);
        Assert.Equal(NvrStreamStatus.Running, playback.Status);
        Assert.NotNull(playback.BeginTime);
        Assert.NotNull(playback.EndTime);
    }
}
