using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia.Tests;

public sealed class LatestFrameMailboxTests
{
    [Fact]
    public void Publish_Coalesces_Queued_Frames_And_Renders_The_Latest()
    {
        var scheduled = new Queue<Action>();
        var rendered = new List<byte[]>();
        using var mailbox = new LatestFrameMailbox(
            frameSize: 4,
            schedule: callback => scheduled.Enqueue(callback),
            render: frame => rendered.Add(frame.ToArray()));

        Assert.True(mailbox.TryAcquire(out var firstSlot, out var first));
        first[0] = 1;
        mailbox.Publish(firstSlot);

        Assert.True(mailbox.TryAcquire(out var secondSlot, out var second));
        second[0] = 2;
        mailbox.Publish(secondSlot);

        // Two decoded frames must create one UI callback, not two queued full-frame copies.
        Assert.Single(scheduled);

        scheduled.Dequeue().Invoke();

        Assert.Single(rendered);
        Assert.Equal(2, rendered[0][0]);
        Assert.Empty(scheduled);
    }

    [Fact]
    public void Dispose_Drains_A_Already_Scheduled_Latest_Frame()
    {
        var scheduled = new Queue<Action>();
        var rendered = new List<byte[]>();
        var mailbox = new LatestFrameMailbox(
            frameSize: 1,
            schedule: callback => scheduled.Enqueue(callback),
            render: frame => rendered.Add(frame.ToArray()));

        Assert.True(mailbox.TryAcquire(out var slot, out var frame));
        frame[0] = 7;
        mailbox.Publish(slot);
        mailbox.Dispose();

        // Teardown must not erase a frame already queued for the UI; this prevents a
        // reconnect boundary from producing a blank/stale tile.
        scheduled.Dequeue().Invoke();

        Assert.Equal([new byte[] { 7 }], rendered);
    }

    [Fact]
    public void TryAcquire_Uses_Three_Slots_While_One_Is_Rendering_And_One_Is_Pending()
    {
        var scheduled = new Queue<Action>();
        using var mailbox = new LatestFrameMailbox(
            frameSize: 1,
            schedule: callback => scheduled.Enqueue(callback),
            render: _ => { });

        Assert.True(mailbox.TryAcquire(out var firstSlot, out _));
        mailbox.Publish(firstSlot);
        Assert.True(mailbox.TryAcquire(out var secondSlot, out _));
        mailbox.Publish(secondSlot);

        // The third slot remains available even while the first callback is queued and
        // the second frame is the pending latest frame.
        Assert.True(mailbox.TryAcquire(out _, out _));
    }
}
