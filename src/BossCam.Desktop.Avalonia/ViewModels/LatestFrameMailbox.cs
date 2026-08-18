namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Bounded latest-frame handoff between an ffmpeg reader and the Avalonia UI thread.
/// The reader owns a free buffer while filling it; the UI owns a rendering buffer while
/// painting it. If several frames arrive before the UI runs, only the newest pending frame
/// is retained and one UI callback is scheduled. This keeps latency bounded without
/// back-pressuring ffmpeg's stdout pipe or allocating a byte array per frame.
/// </summary>
internal sealed class LatestFrameMailbox : IDisposable
{
    private const int Free = 0;
    private const int Acquired = 1;
    private const int Pending = 2;
    private const int Rendering = 3;

    private readonly object _gate = new();
    private readonly byte[][] _buffers;
    private readonly int[] _states;
    private readonly Action<Action> _schedule;
    private readonly Action<byte[]> _render;
    private int _latestPending = -1;
    private bool _callbackScheduled;
    private bool _disposed;

    internal LatestFrameMailbox(
        int frameSize,
        Action<Action> schedule,
        Action<byte[]> render,
        int bufferCount = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferCount, 3);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(render);

        _buffers = Enumerable.Range(0, bufferCount)
            .Select(_ => new byte[frameSize])
            .ToArray();
        _states = new int[bufferCount];
        _schedule = schedule;
        _render = render;
    }

    /// <summary>Claims a free buffer for the decoder to fill.</summary>
    internal bool TryAcquire(out int slot, out byte[] buffer)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                slot = -1;
                buffer = Array.Empty<byte>();
                return false;
            }

            for (var i = 0; i < _states.Length; i++)
            {
                if (_states[i] == Free)
                {
                    _states[i] = Acquired;
                    slot = i;
                    buffer = _buffers[i];
                    return true;
                }
            }
        }

        slot = -1;
        buffer = Array.Empty<byte>();
        return false;
    }

    /// <summary>Returns a decoder buffer that could not be filled completely.</summary>
    internal void Release(int slot)
    {
        lock (_gate)
        {
            if (!_disposed && slot >= 0 && slot < _states.Length && _states[slot] == Acquired)
            {
                _states[slot] = Free;
            }
        }
    }

    /// <summary>
    /// Publishes a filled buffer. Older pending frames are released immediately; the UI
    /// callback scheduled for the mailbox always receives the newest frame available.
    /// </summary>
    internal void Publish(int slot)
    {
        Action? callback = null;
        lock (_gate)
        {
            if (_disposed || slot < 0 || slot >= _states.Length || _states[slot] != Acquired)
            {
                return;
            }

            _states[slot] = Pending;
            if (_latestPending >= 0 && _latestPending != slot)
            {
                _states[_latestPending] = Free;
            }
            _latestPending = slot;

            if (!_callbackScheduled)
            {
                _callbackScheduled = true;
                callback = DrainOneAsync;
            }
        }

        if (callback is not null)
        {
            try
            {
                _schedule(callback);
            }
            catch
            {
                // A shutting-down dispatcher may reject a callback. Return the pending
                // buffer to the pool so a reconnect can create a fresh mailbox cleanly.
                lock (_gate)
                {
                    _callbackScheduled = false;
                    if (_latestPending >= 0)
                    {
                        _states[_latestPending] = Free;
                        _latestPending = -1;
                    }
                }
            }
        }
    }

    private void DrainOneAsync()
    {
        byte[]? frame = null;
        lock (_gate)
        {
            // A callback may already be queued when teardown begins. Drain that one
            // latest frame even after Dispose; the owning view's render method performs
            // its own disposed check, while reconnect teardown is allowed to present the
            // final valid frame instead of converting it into a blank gap.
            if (_latestPending >= 0)
            {
                var slot = _latestPending;
                _latestPending = -1;
                _states[slot] = Rendering;
                frame = _buffers[slot];
            }
            else
            {
                _callbackScheduled = false;
                return;
            }
        }

        try
        {
            _render(frame);
        }
        finally
        {
            Action? next = null;
            lock (_gate)
            {
                var slot = Array.IndexOf(_buffers, frame);
                if (slot >= 0 && _states[slot] == Rendering)
                {
                    _states[slot] = Free;
                }

                if (_disposed)
                {
                    _callbackScheduled = false;
                }
                else if (_latestPending >= 0)
                {
                    // Keep the callback flag set while chaining the next single render.
                    next = DrainOneAsync;
                }
                else
                {
                    _callbackScheduled = false;
                }
            }

            if (next is not null)
            {
                try
                {
                    _schedule(next);
                }
                catch
                {
                    lock (_gate)
                    {
                        _callbackScheduled = false;
                        if (_latestPending >= 0)
                        {
                            _states[_latestPending] = Free;
                            _latestPending = -1;
                        }
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Keep the already-published pending slot and callback alive so DrainOneAsync
            // can present the final frame. New decoder acquisitions are rejected by the
            // disposed check in TryAcquire/Publish.
        }
    }
}
