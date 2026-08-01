<script lang="ts">
  import { onMount } from 'svelte';
  import type { DeviceIdentity, LiveMediaManifest, LiveMediaMode } from '../types';
  import { AppState } from '../store';
  import { api } from '../api';

  let { device, appState }: { device: DeviceIdentity; appState: AppState } = $props();

  let videoEl: HTMLVideoElement | undefined = $state();
  let fallbackMode = $state<'mse' | 'mjpeg' | 'snapshot'>('mse');
  let manifest = $state<LiveMediaManifest | null>(null);
  let streamStatus = $state('Negotiating live media…');
  let selectedMode = $state<LiveMediaMode | 'Snapshot'>('H264Fmp4');
  let isActive = $state(false);
  let imgKey = $state(0);
  let abortController: AbortController | undefined;
  let mediaObjectUrl: string | undefined;
  let mediaSource: MediaSource | undefined;
  let sourceBuffer: SourceBuffer | undefined;
  let appendQueue: Uint8Array[] = [];
  let appending = false;
  let firstChunkReceived = false;
  let firstFrameReady = false;
  let mseFailure = false;
  let firstFrameWatchdog: ReturnType<typeof setTimeout> | undefined;
  let sourceOpenWatchdog: ReturnType<typeof setTimeout> | undefined;
  let activityWatchdog: ReturnType<typeof setTimeout> | undefined;
  let mseReject: ((reason?: unknown) => void) | undefined;
  let mseCleanup: (() => void) | undefined;
  let mseGeneration = 0;
  let snapshotRefreshTimer: ReturnType<typeof setTimeout> | undefined;
  let startedQuality = $state<string | undefined>(undefined);
  let fallbackAbortController: AbortController | undefined;
  let fallbackObjectUrl: string | undefined;
  let fallbackImageUrl = $state('');
  let fallbackRun = 0;

  const lanHeaders = () => ({
    'X-LAN-Token': localStorage.getItem('bosscam.lanToken') || '',
  });

  function modeLabel(mode: LiveMediaMode | 'Snapshot'): string {
    return mode === 'HevcFmp4' ? 'Direct HEVC' :
      mode === 'H264Fmp4' ? 'H.264 compatibility' :
      mode === 'H264MpegTs' ? 'H.264 MPEG-TS' :
      mode === 'Mjpeg' ? 'MJPEG fallback' : 'Snapshot degraded';
  }

  function mimeFor(mode: LiveMediaMode): string | null {
    if (mode === 'HevcFmp4') return 'video/mp4; codecs="hvc1.1.6.L93.B0"';
    if (mode === 'H264Fmp4') return 'video/mp4; codecs="avc1.42E01E"';
    return null;
  }

  function urlFor(mode: LiveMediaMode | 'Snapshot', value: LiveMediaManifest): string {
    if (mode === 'HevcFmp4') return value.hevcFmp4Url;
    if (mode === 'H264Fmp4') return value.h264Fmp4Url;
    if (mode === 'H264MpegTs') return value.mpegTsUrl;
    if (mode === 'Mjpeg') return `${value.mjpegUrl}${value.mjpegUrl.includes('?') ? '&' : '?'}t=${Date.now()}`;
    return `${value.snapshotUrl}${value.snapshotUrl.includes('?') ? '&' : '?'}t=${Date.now()}`;
  }

  function clearInitialWatchdogs() {
    if (firstFrameWatchdog) {
      clearTimeout(firstFrameWatchdog);
      firstFrameWatchdog = undefined;
    }
    if (sourceOpenWatchdog) {
      clearTimeout(sourceOpenWatchdog);
      sourceOpenWatchdog = undefined;
    }
  }

  function clearWatchdog() {
    clearInitialWatchdogs();
    if (activityWatchdog) {
      clearTimeout(activityWatchdog);
      activityWatchdog = undefined;
    }
  }

  function resetActivityWatchdog(mode: LiveMediaMode, generation = mseGeneration) {
    if (activityWatchdog) clearTimeout(activityWatchdog);
    activityWatchdog = setTimeout(() => {
      if (generation !== mseGeneration) return;
      if (abortController) abortController.abort();
      mseReject?.(new Error(`${modeLabel(mode)} stopped producing decodable media`));
    }, 8000);
  }

  function clearSnapshotRefresh() {
    if (snapshotRefreshTimer) {
      clearTimeout(snapshotRefreshTimer);
      snapshotRefreshTimer = undefined;
    }
  }

  function clearFallback() {
    fallbackRun += 1;
    fallbackAbortController?.abort();
    fallbackAbortController = undefined;
    if (fallbackObjectUrl) URL.revokeObjectURL(fallbackObjectUrl);
    fallbackObjectUrl = undefined;
    fallbackImageUrl = '';
  }

  function publishFallbackFrame(bytes: Uint8Array, run: number) {
    if (run !== fallbackRun) return;
    const nextUrl = URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' }));
    const previous = fallbackObjectUrl;
    fallbackObjectUrl = nextUrl;
    fallbackImageUrl = nextUrl;
    if (previous) URL.revokeObjectURL(previous);
    isActive = true;
    streamStatus = fallbackMode === 'snapshot' ? 'Snapshot fallback active' : 'MJPEG fallback active';
  }

  function findBytes(haystack: Uint8Array, needle: number[], start = 0): number {
    for (let i = start; i <= haystack.length - needle.length; i += 1) {
      let found = true;
      for (let j = 0; j < needle.length; j += 1) {
        if (haystack[i + j] !== needle[j]) { found = false; break; }
      }
      if (found) return i;
    }
    return -1;
  }

  function scheduleSnapshotRefresh() {
    clearSnapshotRefresh();
    if (fallbackMode !== 'snapshot' || !manifest || !isActive) return;
    snapshotRefreshTimer = setTimeout(() => {
      void loadSnapshotFrame();
    }, 2500);
  }

  async function loadSnapshotFrame() {
    if (fallbackMode !== 'snapshot' || !manifest) return;
    const run = fallbackRun;
    const controller = fallbackAbortController ?? new AbortController();
    fallbackAbortController = controller;
    try {
      const response = await fetch(urlFor('Snapshot', manifest), {
        signal: controller.signal,
        headers: lanHeaders(),
        cache: 'no-store',
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      publishFallbackFrame(new Uint8Array(await response.arrayBuffer()), run);
      scheduleSnapshotRefresh();
    } catch (error) {
      if (controller.signal.aborted || run !== fallbackRun) return;
      isActive = false;
      streamStatus = `Snapshot fallback unavailable: ${String(error)}`;
      snapshotRefreshTimer = setTimeout(() => { void loadSnapshotFrame(); }, 1500);
    }
  }

  async function startMjpegFallback() {
    if (!manifest) return;
    clearFallback();
    const run = fallbackRun;
    const controller = new AbortController();
    fallbackAbortController = controller;
    try {
      const response = await fetch(urlFor('Mjpeg', manifest), {
        signal: controller.signal,
        headers: lanHeaders(),
        cache: 'no-store',
      });
      if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
      const reader = response.body.getReader();
      let pending = new Uint8Array(0);
      while (true) {
        const { done, value } = await reader.read();
        if (done) throw new Error('MJPEG stream ended');
        if (!value?.length) continue;
        const merged = new Uint8Array(pending.length + value.length);
        merged.set(pending); merged.set(value, pending.length);
        pending = merged;
        let cursor = 0;
        while (true) {
          const soi = findBytes(pending, [0xff, 0xd8], cursor);
          if (soi < 0) {
            pending = pending.slice(Math.max(0, pending.length - 1));
            break;
          }
          const eoi = findBytes(pending, [0xff, 0xd9], soi + 2);
          if (eoi < 0) {
            pending = pending.slice(soi);
            break;
          }
          publishFallbackFrame(pending.slice(soi, eoi + 2), run);
          cursor = eoi + 2;
          if (cursor >= pending.length) { pending = new Uint8Array(0); break; }
        }
        if (pending.length > 4 * 1024 * 1024) pending = pending.slice(-2);
      }
    } catch (error) {
      if (controller.signal.aborted || run !== fallbackRun) return;
      isActive = false;
      streamStatus = `MJPEG fallback unavailable: ${String(error)}`;
      setTimeout(() => { if (run === fallbackRun) void startStream(); }, 1500);
    }
  }

  function clearMse() {
    mseGeneration += 1;
    mseCleanup?.();
    mseCleanup = undefined;
    mseReject = undefined;
    clearWatchdog();
    abortController?.abort();
    abortController = undefined;
    appendQueue = [];
    appending = false;
    firstChunkReceived = false;
    firstFrameReady = false;
    mseFailure = false;
    clearFallback();
    if (mediaSource && mediaSource.readyState !== 'closed') {
      try { mediaSource.endOfStream(); } catch { /* stream may already be closed */ }
    }
    if (mediaObjectUrl) URL.revokeObjectURL(mediaObjectUrl);
    mediaObjectUrl = undefined;
    mediaSource = undefined;
    sourceBuffer = undefined;
    if (videoEl) videoEl.removeAttribute('src');
  }

  function appendNext() {
    if (!sourceBuffer || sourceBuffer.updating || appendQueue.length === 0) {
      appending = false;
      return;
    }
    appending = true;
    try {
      sourceBuffer.appendBuffer(appendQueue.shift()!);
    } catch {
      appending = false;
      mseFailure = true;
      streamStatus = 'Browser buffer rejected media; switching to fallback…';
      abortController?.abort();
    }
  }

  async function runMse(mode: LiveMediaMode, value: LiveMediaManifest): Promise<boolean> {
    const mime = mimeFor(mode);
    if (!mime || !('MediaSource' in window) || !MediaSource.isTypeSupported(mime)) return false;
    const url = urlFor(mode, value);
    if (!url) return false;

    clearMse();
    clearSnapshotRefresh();
    fallbackMode = 'mse';
    mediaSource = new MediaSource();
    mediaObjectUrl = URL.createObjectURL(mediaSource);
    if (!videoEl) return false;
    videoEl.src = mediaObjectUrl;
    selectedMode = mode;
    streamStatus = `Connecting ${modeLabel(mode)}…`;

    await new Promise<void>((resolve, reject) => {
      const current = mediaSource!;
      const generation = ++mseGeneration;
      mseReject = reject;
      sourceOpenWatchdog = setTimeout(() => {
        if (generation === mseGeneration) reject(new Error(`${modeLabel(mode)} MediaSource did not open`));
      }, 5000);
      const onPlaying = () => resetActivityWatchdog(mode, generation);
      const onTimeUpdate = () => resetActivityWatchdog(mode, generation);
      const onReady = () => {
        if (generation !== mseGeneration || mediaSource !== current) return;
        firstFrameReady = true;
        clearInitialWatchdogs();
        resetActivityWatchdog(mode, generation);
        isActive = true;
        streamStatus = `${modeLabel(mode)} active`;
        void videoEl?.play().catch(() => undefined);
      };
      const onOpen = async () => {
        if (generation !== mseGeneration) return;
        if (sourceOpenWatchdog) {
          clearTimeout(sourceOpenWatchdog);
          sourceOpenWatchdog = undefined;
        }
        current.removeEventListener('sourceopen', onOpen);
        try {
          sourceBuffer = current.addSourceBuffer(mime);
          sourceBuffer.addEventListener('updateend', appendNext);
          videoEl?.addEventListener('loadeddata', onReady, { once: true });
          videoEl?.addEventListener('canplay', onReady, { once: true });
          videoEl?.addEventListener('playing', onPlaying);
          videoEl?.addEventListener('timeupdate', onTimeUpdate);
          abortController = new AbortController();
          const response = await fetch(url, { signal: abortController.signal, headers: lanHeaders(), cache: 'no-store' });
          if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);

          const reader = response.body.getReader();
          firstChunkReceived = false;
          firstFrameReady = false;
          mseFailure = false;
          firstFrameWatchdog = setTimeout(() => {
            if (generation !== mseGeneration) return;
            abortController?.abort();
            reject(new Error(`${modeLabel(mode)} produced no decoded frame`));
          }, 10000);
          resetActivityWatchdog(mode, generation);

          while (true) {
            const { done, value: chunk } = await reader.read();
            if (done) throw new Error(`${modeLabel(mode)} stream ended`);
            if (chunk?.length) {
              if (!firstChunkReceived) {
                firstChunkReceived = true;
                streamStatus = `${modeLabel(mode)} buffering…`;
              }
              // Never discard arbitrary bytes: that can remove an MP4 box boundary and
              // permanently corrupt the MSE stream. Restart/fallback instead.
              if (appendQueue.length >= 24) {
                mseFailure = true;
                abortController?.abort();
                throw new Error(`${modeLabel(mode)} browser buffer fell behind`);
              }
              appendQueue.push(chunk);
              if (!appending) appendNext();
            }
          }
          if (!firstChunkReceived || !firstFrameReady || mseFailure) throw new Error(`${modeLabel(mode)} ended without playable media`);
          resolve();
        } catch (error) {
          clearWatchdog();
          reject(error);
        } finally {
          if (mseReject === reject) mseReject = undefined;
        }
      };
      mseCleanup = () => {
        current.removeEventListener('sourceopen', onOpen);
        sourceBuffer?.removeEventListener('updateend', appendNext);
        videoEl?.removeEventListener('loadeddata', onReady);
        videoEl?.removeEventListener('canplay', onReady);
        videoEl?.removeEventListener('playing', onPlaying);
        videoEl?.removeEventListener('timeupdate', onTimeUpdate);
      };
      current.addEventListener('sourceopen', onOpen, { once: true });
    }).catch(() => {
      clearMse();
      isActive = false;
      throw new Error(`${modeLabel(mode)} unavailable`);
    });
    return true;
  }

  function fallbackToMjpeg() {
    clearMse();
    clearSnapshotRefresh();
    fallbackMode = 'mjpeg';
    selectedMode = 'Mjpeg';
    isActive = false;
    streamStatus = 'Connecting MJPEG fallback…';
    imgKey += 1;
    void startMjpegFallback();
  }

  function fallbackToSnapshot() {
    clearMse();
    fallbackMode = 'snapshot';
    selectedMode = 'Snapshot';
    isActive = false;
    streamStatus = 'Connecting snapshot fallback…';
    imgKey += 1;
    clearFallback();
    void loadSnapshotFrame();
  }

  async function startStream() {
    streamStatus = 'Negotiating live media…';
    isActive = false;
    fallbackMode = 'mse';
    clearSnapshotRefresh();
    try {
      manifest = await api.liveManifest(device.id, appState.streamQuality);
      if (!manifest) throw new Error('manifest unavailable');

      // The backend owns the ordered decision. MPEG-TS is intentionally skipped in a
      // browser because it is the Avalonia/native compatibility representation; the next
      // advertised browser mode is fMP4, then MJPEG, then snapshots.
      const candidates = [...new Set([manifest.preferredMode, ...manifest.fallbackModes])];
      for (const mode of candidates) {
        if (mode === 'Mjpeg') {
          fallbackToMjpeg();
          return;
        }
        if (mode === 'Snapshot' || mode === 'H264MpegTs') continue;
        try {
          if (await runMse(mode, manifest)) return;
        } catch {
          // Try the next backend-advertised representation.
        }
      }

      if (manifest.snapshotAvailable) fallbackToSnapshot();
      else fallbackToMjpeg();
    } catch (error) {
      streamStatus = `Live negotiation failed: ${String(error)}`;
      if (manifest?.snapshotAvailable) fallbackToSnapshot();
      else fallbackToMjpeg();
    }
  }

  function stopStream() {
    isActive = false;
    clearSnapshotRefresh();
    clearMse();
    fallbackMode = 'mse';
    streamStatus = 'Stopped';
  }

  function retry() {
    stopStream();
    void startStream();
  }

  $effect(() => {
    const quality = appState.streamQuality;
    if (startedQuality !== undefined && startedQuality !== quality) {
      startedQuality = quality;
      stopStream();
      void startStream();
    }
  });

  onMount(() => {
    startedQuality = appState.streamQuality;
    void startStream();
    return stopStream;
  });
</script>

<div class="mse-wrapper">
  {#if fallbackMode === 'mse'}
    <video bind:this={videoEl} autoplay muted playsinline class="mse-video" controls={false}></video>
  {:else if manifest}
    {#key imgKey}
      <img
        src={fallbackImageUrl}
        alt={device.displayName || device.ipAddress || 'Camera live fallback'}
        class="mse-video fallback-image"
        decoding="async"
        onerror={() => { streamStatus = 'Fallback frame unavailable — retrying…'; setTimeout(retry, 1500); }}
      />
    {/key}
  {/if}
  <div class="status-bar">
    <span class:live={isActive} class="dot"></span>
    <span class="muted small">{streamStatus}</span>
    {#if isActive}<span class="mode">{modeLabel(selectedMode)}</span>{/if}
    <button onclick={isActive ? stopStream : retry} type="button" class="stop-btn">{isActive ? '⏹ Stop' : '↻ Retry'}</button>
  </div>
</div>

<style>
  .mse-wrapper { position: relative; background: #000; border-radius: 8px; overflow: hidden; min-height: 160px; display: flex; flex-direction: column; }
  .mse-video { width: 100%; height: 100%; min-height: 140px; object-fit: contain; background: #000; display: block; }
  .fallback-image { flex: 1; }
  .status-bar { display: flex; align-items: center; gap: 6px; padding: 4px 8px; background: #1a100ecc; z-index: 1; }
  .muted { color: var(--muted); font-size: .82rem; }
  .small { font-size: .78rem; }
  .mode { margin-left: auto; color: #ffb06a; font-size: .72rem; font-weight: 700; }
  .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #777; }
  .dot.live { background: #3ecf8e; box-shadow: 0 0 6px #3ecf8e88; }
  .stop-btn { margin-left: auto; background: transparent; border: 1px solid #ff5a1f55; border-radius: 6px; padding: 3px 8px; cursor: pointer; color: var(--text); font: inherit; font-size: .78rem; }
  .stop-btn:hover { border-color: #ffa33e; }
</style>
