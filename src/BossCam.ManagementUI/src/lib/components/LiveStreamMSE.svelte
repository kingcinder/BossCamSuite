<script lang="ts">
  import { onMount } from 'svelte';
  import type { DeviceIdentity } from '../types';
  import { AppState } from '../store';
  import { api } from '../api';

  let { device, appState }: { device: DeviceIdentity; appState: AppState } = $props();

  let videoEl: HTMLVideoElement | undefined = $state();
  let mediaSource: MediaSource | undefined = $state();
  let sourceBuffer: SourceBuffer | undefined = $state();
  let streamStatus = $state('initializing…');
  let isActive = $state(false);

  // Determine the codec from device capabilities (default H.264 High)
  let codec = $state('video/mp4; codecs="avc1.64001f"');

  // The fMP4 URL — no cache busting needed for live
  let streamUrl = $derived(`/api/devices/${device.id}/live.mp4?quality=${encodeURIComponent(appState.streamQuality)}`);

  // Abort controller for fetch
  let abortController: AbortController | undefined;

  async function startStream() {
    if (!videoEl) return;
    isActive = true;
    streamStatus = 'Connecting…';

    // Create MediaSource
    mediaSource = new MediaSource();
    videoEl.src = URL.createObjectURL(mediaSource);

    mediaSource.addEventListener('sourceopen', async () => {
      try {
        if (!mediaSource) return;
        // Check if codec is supported
        const mime = codec;
        if (!MediaSource.isTypeSupported(mime)) {
          streamStatus = `Codec not supported: ${mime}. Fall back to MJPEG.`;
          isActive = false;
          return;
        }

        sourceBuffer = mediaSource.addSourceBuffer(mime);
        streamStatus = 'Buffering…';

        // Start fetching the fMP4 stream
        abortController = new AbortController();
        try {
          const response = await fetch(streamUrl, {
            signal: abortController.signal,
            headers: { 'X-LAN-Token': localStorage.getItem('bosscam.lanToken') || '' },
          });

          if (!response.ok || !response.body) {
            streamStatus = `Stream failed: ${response.status}`;
            isActive = false;
            return;
          }

          const reader = response.body.getReader();
          streamStatus = 'Streaming…';

          // Read chunks and append to SourceBuffer
          let buffer: Uint8Array = new Uint8Array(0);
          while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            // Accumulate chunks
            if (value) {
              const tmp = new Uint8Array(buffer.length + value.length);
              tmp.set(buffer);
              tmp.set(value, buffer.length);
              buffer = tmp;

              // Try to append to SourceBuffer when it's not updating
              if (sourceBuffer && !sourceBuffer.updating) {
                try {
                  sourceBuffer.appendBuffer(buffer);
                  buffer = new Uint8Array(0);
                } catch (e) {
                  // Buffer might be full — wait and try again
                }
              }
            }

            // Manage buffer: keep only the last 4 seconds
            if (videoEl && videoEl.buffered.length > 0) {
              const liveEdge = videoEl.buffered.end(videoEl.buffered.length - 1);
              if (liveEdge - videoEl.currentTime > 4.0) {
                videoEl.currentTime = liveEdge - 0.5;
              }
              if (sourceBuffer && !sourceBuffer.updating && videoEl.buffered.length > 0) {
                const safeStart = videoEl.buffered.start(0);
                const removeUntil = videoEl.currentTime - 2.0;
                if (removeUntil > safeStart + 0.5) {
                  try { sourceBuffer.remove(safeStart, removeUntil); } catch { /* ignore */ }
                }
              }
            }
          }
        } catch (e: unknown) {
          if ((e as Error).name !== 'AbortError') {
            streamStatus = 'Stream ended: ' + String(e);
          }
        }
      } catch (e: unknown) {
        streamStatus = 'MSE error: ' + String(e);
      }
      isActive = false;
    });
  }

  function stopStream() {
    isActive = false;
    abortController?.abort();
    if (mediaSource && mediaSource.readyState !== 'closed') {
      try { mediaSource.endOfStream(); } catch { /* ignore */ }
    }
    if (videoEl) {
      videoEl.src = '';
    }
    mediaSource = undefined;
    sourceBuffer = undefined;
    streamStatus = 'Stopped';
  }

  onMount(() => {
    return () => {
      stopStream();
    };
  });

  function toggle() {
    if (isActive) {
      stopStream();
    } else {
      startStream();
    }
  }
</script>

<div class="mse-wrapper">
  <video
    bind:this={videoEl}
    autoplay
    muted
    playsinline
    class="mse-video"
    controls={false}
  ></video>
  {#if !isActive}
    <div class="overlay">
      <p class="muted">{streamStatus}</p>
      <button onclick={toggle} type="button" class="play-btn">▶ MSE Stream</button>
    </div>
  {:else}
    <div class="status-bar">
      <span class="dot live"></span>
      <span class="muted small">{streamStatus}</span>
      <button onclick={toggle} type="button" class="stop-btn">⏹ Stop</button>
    </div>
  {/if}
</div>

<style>
  .mse-wrapper {
    position: relative;
    background: #000;
    border-radius: 8px;
    overflow: hidden;
    min-height: 160px;
    display: flex;
    flex-direction: column;
  }
  .mse-video {
    width: 100%;
    height: 100%;
    min-height: 140px;
    object-fit: contain;
    background: #000;
    display: block;
  }
  .overlay {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    background: rgba(0, 0, 0, 0.65);
    padding: 16px;
    text-align: center;
  }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
  .play-btn {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border: none;
    border-radius: 8px;
    padding: 10px 18px;
    color: #fff;
    font-weight: 700;
    cursor: pointer;
    font: inherit;
    margin-top: 8px;
  }
  .play-btn:hover { background: linear-gradient(180deg, #ff8f4a, #c84618); }
  .status-bar {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 8px;
    background: #1a100ecc;
    z-index: 1;
  }
  .dot {
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
  }
  .dot.live { background: #3ecf8e; box-shadow: 0 0 6px #3ecf8e88; }
  .stop-btn {
    margin-left: auto;
    background: transparent;
    border: 1px solid #ff5a1f55;
    border-radius: 6px;
    padding: 3px 8px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
    font-size: .82rem;
  }
  .stop-btn:hover { border-color: #ffa33e; }
</style>
