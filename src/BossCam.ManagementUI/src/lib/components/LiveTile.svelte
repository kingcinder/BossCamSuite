<script lang="ts">
  import { onMount } from 'svelte';
  import type { DeviceIdentity } from '../types';
  import { AppState } from '../store.svelte';
  import { api } from '../api';

  let { device, index, appState }: { device: DeviceIdentity; index: number; appState: AppState } = $props();

  function labelOf(d: DeviceIdentity): string {
    return d.displayName || d.ipAddress || d.id;
  }

  // Authenticated MJPEG stream handling. An <img src> cannot send X-LAN-Token, so
  // consume the multipart response with fetch and expose only short-lived JPEG blob URLs.
  let streamUrl = $derived(api.liveMjpegUrl(device.id, appState.streamQuality));
  let streamImageUrl = $state('');
  let streamFailed = $state(false);
  let streamErrorMsg = $state('Connecting live stream…');
  let streamAbortController: AbortController | undefined;
  let streamObjectUrl: string | undefined;
  let streamRun = 0;
  let isDragging = $state(false);
  let isDragOver = $state(false);
  let snapshotTimer: ReturnType<typeof setTimeout> | undefined;

  const lanHeaders = () => ({
    'X-LAN-Token': localStorage.getItem('bosscam.lanToken') || '',
  });

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

  function publishFrame(frame: Uint8Array, run: number) {
    if (run !== streamRun) return;
    const nextUrl = URL.createObjectURL(new Blob([frame], { type: 'image/jpeg' }));
    const previous = streamObjectUrl;
    streamObjectUrl = nextUrl;
    streamImageUrl = nextUrl;
    if (previous) URL.revokeObjectURL(previous);
    streamFailed = false;
    appState.setStreamStatus(device.id, 'live');
  }

  function stopStream() {
    streamRun += 1;
    if (snapshotTimer) { clearTimeout(snapshotTimer); snapshotTimer = undefined; }
    streamAbortController?.abort();
    streamAbortController = undefined;
    if (streamObjectUrl) URL.revokeObjectURL(streamObjectUrl);
    streamObjectUrl = undefined;
    streamImageUrl = '';
    appState.setStreamStatus(device.id, 'connecting');
  }

  // When the MJPEG pipe fails (locked/degraded cameras), try the snapshot endpoint so the
  // tile still shows a live still frame instead of spinning forever. Refreshes periodically
  // while the stream stays down; the retry loop keeps hunting for the real stream.
  async function attemptSnapshotFallback(run: number) {
    if (run !== streamRun) return;
    try {
      const res = await fetch(api.snapshotUrl(device.id), {
        headers: lanHeaders(),
        cache: 'no-store',
        signal: streamAbortController?.signal,
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const bytes = new Uint8Array(await res.arrayBuffer());
      if (run !== streamRun || bytes.length < 100) return;
      const nextUrl = URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' }));
      const previous = streamObjectUrl;
      streamObjectUrl = nextUrl;
      streamImageUrl = nextUrl;
      if (previous) URL.revokeObjectURL(previous);
      appState.setStreamStatus(device.id, 'snapshot');
      streamFailed = false;
      streamErrorMsg = 'Stream unavailable — showing snapshot still';
      if (snapshotTimer) clearTimeout(snapshotTimer);
      snapshotTimer = setTimeout(() => { if (run === streamRun) void attemptSnapshotFallback(run); }, 4000);
    } catch {
      // Snapshot also unavailable — the stream retry loop keeps trying.
    }
  }

  async function startStream() {
    stopStream();
    appState.setStreamStatus(device.id, 'connecting');
    const run = streamRun;
    const controller = new AbortController();
    streamAbortController = controller;
    streamFailed = false;
    streamErrorMsg = 'Connecting live stream…';
    try {
      const response = await fetch(streamUrl, {
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
          publishFrame(pending.slice(soi, eoi + 2), run);
          cursor = eoi + 2;
          if (cursor >= pending.length) { pending = new Uint8Array(0); break; }
        }
        if (pending.length > 4 * 1024 * 1024) pending = pending.slice(-2);
      }
    } catch (error) {
      if (controller.signal.aborted || run !== streamRun) return;
      streamFailed = true;
      streamErrorMsg = `Stream failed — retrying (${String(error)})`;
      appState.setStreamStatus(device.id, 'retrying');
      void attemptSnapshotFallback(run);
      // 5s retry leaves room for the 4s snapshot refresh to fire before the next stream attempt.
      setTimeout(() => { if (run === streamRun) void startStream(); }, 5000);
    }
  }

  function select() {
    appState.selectedDeviceId = device.id;
  }

  function toggleStar(e: MouseEvent) {
    e.stopPropagation();
    e.preventDefault();
    appState.toggleStar(device.id);
    appState.showToast(appState.isStarred(device.id)
      ? `⭐ ${labelOf(device)} pinned to landing page`
      : `☆ ${labelOf(device)} unpinned`);
  }

  function openFullscreen() {
    appState.selectedDeviceId = device.id;
    appState.fullscreenDeviceId = device.id;
  }

  // Drag-n-drop
  function onDragStart(e: DragEvent) {
    isDragging = true;
    e.dataTransfer?.setData('text/plain', device.id);
    if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
  }
  function onDragEnd() {
    isDragging = false;
    isDragOver = false;
  }
  function onDragOver(e: DragEvent) {
    e.preventDefault();
    isDragOver = true;
  }
  function onDragLeave() {
    isDragOver = false;
  }
  function onDrop(e: DragEvent) {
    e.preventDefault();
    isDragOver = false;
    const fromId = e.dataTransfer?.getData('text/plain');
    if (!fromId || fromId === device.id) return;

    const order = [...appState.viewOrder];
    const fromIdx = order.indexOf(fromId);
    const toIdx = order.indexOf(device.id);
    if (fromIdx < 0 || toIdx < 0) return;

    order.splice(fromIdx, 1);
    order.splice(toIdx, 0, fromId);
    appState.viewOrder = order;
    appState.persistOrder();
  }

  async function snap() {
    try {
      await api.saveSnapshot(device.id);
      appState.showToast('Snapshot saved');
    } catch {
      window.open(api.snapshotUrl(device.id), '_blank');
    }
  }

  async function startRec() {
    try {
      await api.recordingStart({ deviceId: device.id });
      appState.showToast('Recording started');
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function stopRec() {
    const job = appState.recordingJobs.find(j => j.deviceId === device.id && j.isRunning);
    if (!job) {
      appState.showToast('No active recording for this camera', false);
      return;
    }
    try {
      await api.recordingStop(job.id);
      appState.showToast('Recording stopped');
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  // ── Recording status ─────────────────────────────────────────
  let recordingJob = $derived(appState.recordingJobs.find(j => j.deviceId === device.id && j.isRunning));
  let isRecording = $derived(!!recordingJob);

  onMount(() => {
    void startStream();
    return stopStream;
  });

</script>

<div
  class="view-tile"
  class:selected={device.id === appState.selectedDeviceId}
  class:dragging={isDragging}
  class:drag-over={isDragOver}
  role="button"
  tabindex="0"
  draggable="true"
  ondragstart={onDragStart}
  ondragend={onDragEnd}
  ondragover={onDragOver}
  ondragleave={onDragLeave}
  ondrop={onDrop}
  ondblclick={openFullscreen}
>
  <div class="view-tile-bar" class:recording={isRecording}>
    <div>
      <strong class:recording={isRecording}>
        {#if isRecording}<span class="rec-dot"></span>{/if}
        {labelOf(device)}
      </strong>
      <div class="sub">{device.ipAddress || ''} · {device.hardwareModel || ''}</div>
    </div>
    <button
      type="button"
      class="tile-star"
      class:starred={appState.isStarred(device.id)}
      onclick={toggleStar}
      data-tip-pos="below"
      data-tip={appState.isStarred(device.id) ? 'Pinned — auto-loads on the landing page. Click to unpin.' : 'Pin to landing page (auto-loads on startup).'}
      aria-label={appState.isStarred(device.id) ? `Unpin ${labelOf(device)}` : `Pin ${labelOf(device)} to landing page`}
    >
      {appState.isStarred(device.id) ? '★' : '☆'}
    </button>
    <span class="tile-status" class:live={!streamFailed && !!streamImageUrl} class:snap={appState.streamStatusByDevice[device.id] === 'snapshot'} class:rec={isRecording} data-tip-pos="below" data-tip={streamFailed ? streamErrorMsg : appState.streamStatusByDevice[device.id] === 'snapshot' ? 'Video stream unavailable — showing snapshot still' : isRecording ? 'Recording' : 'Live stream active'}>
      {#if isRecording}● REC{:else if appState.streamStatusByDevice[device.id] === 'snapshot'}📷 Still{:else if streamFailed}↻ Retrying{:else if streamImageUrl}● Live{:else}… Connecting{/if}
    </span>
  </div>
  <div class="view-tile-media">
    <img
      src={streamImageUrl}
      alt={labelOf(device)}
      class="live-mjpeg"
      decoding="async"
    />
    {#if streamFailed}
      <div class="fail">{streamErrorMsg}</div>
    {/if}
  </div>
  <div class="view-tile-actions">
    <button class="btn btn-sm" onclick={select} type="button" data-tip="Select this camera for settings">Select</button>
    <button class="btn btn-sm" onclick={snap} type="button" data-tip="Save a snapshot to disk">📸 Snapshot</button>
    {#if isRecording}
      <button onclick={stopRec} type="button" class="btn btn-sm btn-danger" data-tip="Stop recording this camera">⏹ Stop</button>
      <span class="badge bad"><span class="rec-dot"></span>REC</span>
    {:else}
      <button onclick={startRec} type="button" class="btn btn-sm btn-primary" data-tip="Start continuous recording">⏺ Record</button>
    {/if}
  </div>
</div>

<style>
  .view-tile {
    position: relative;
    background: #0b0809;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius);
    overflow: hidden;
    min-height: 170px;
    display: flex;
    flex-direction: column;
    cursor: grab;
    user-select: none;
    transition: border-color 0.2s ease, box-shadow 0.2s ease;
  }
  .view-tile:hover { border-color: var(--border); box-shadow: var(--shadow-2); }
  .view-tile.dragging { opacity: 0.55; border-color: var(--accent-strong); }
  .view-tile.drag-over { outline: 2px dashed var(--accent); outline-offset: -2px; }
  .view-tile.selected {
    border-color: var(--accent-strong);
    box-shadow: 0 0 0 1px var(--accent-glow) inset, var(--shadow-2);
  }
  .view-tile-bar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 8px;
    padding: 8px 11px;
    background: linear-gradient(180deg, #1e1315, #171012);
    font-size: var(--fs-md);
    z-index: 1;
    transition: background 0.3s;
    border-bottom: 1px solid var(--border-faint);
  }
  .view-tile-bar.recording {
    background: linear-gradient(180deg, #22170e, #1a1208);
    border-bottom-color: #ff3e3e44;
  }
  .view-tile-bar > div { min-width: 0; flex: 1; }
  .view-tile-bar strong { display: flex; align-items: center; gap: 5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text); }
  .view-tile-bar strong.recording { color: #ffb06a; }
  .view-tile-bar .sub { color: var(--faint); font-size: var(--fs-xs); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tile-star {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    flex-shrink: 0;
    border-radius: 50%;
    background: transparent;
    border: 1px solid var(--border-soft);
    color: #7a6a62;
    font-size: 1.05rem;
    line-height: 1;
    cursor: pointer;
    padding: 0;
    transition: color 0.15s ease, background 0.15s ease, border-color 0.15s ease, transform 0.1s ease, text-shadow 0.15s ease;
  }
  .tile-star:hover {
    border-color: #ffd25a99;
    color: #ffd25a;
    background: #2a2413;
  }
  .tile-star:active { transform: scale(0.88); }
  .tile-star.starred {
    color: #ffd25a;
    border-color: #ffd25a88;
    background: linear-gradient(180deg, #4a3c12, #2a2410);
    text-shadow: 0 0 8px rgba(255, 210, 90, 0.65);
  }
  .tile-status {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font-size: 0.66rem;
    font-weight: 800;
    letter-spacing: 0.03em;
    padding: 2px 8px;
    border-radius: 999px;
    background: #20181a;
    color: #9a8f8f;
    border: 1px solid #ffffff1f;
    white-space: nowrap;
    cursor: help;
    flex-shrink: 0;
  }
  .tile-status.live { background: var(--ok-dim); color: var(--ok-text); border-color: #3ecf8e66; }
  .tile-status.snap { background: var(--warn-dim); color: var(--warn-text); border-color: #cf9e3e66; }
  .tile-status.rec { background: var(--bad-dim); color: var(--bad-text); border-color: #ff3e3e66; }
  .rec-dot {
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--bad);
    box-shadow: 0 0 6px rgba(255, 62, 62, 0.8);
    animation: rec-pulse 1.5s infinite;
    flex-shrink: 0;
  }
  @keyframes rec-pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.3; }
  }
  .view-tile-media {
    flex: 1;
    min-height: 120px;
    background: var(--bg-deep);
    display: grid;
    place-items: center;
    position: relative;
  }
  .view-tile-media img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    background: var(--bg-deep);
    min-height: 120px;
  }
  .view-tile-media .fail {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    color: var(--muted);
    font-size: var(--fs-md);
    padding: 14px;
    text-align: center;
  }
  .view-tile-actions {
    display: flex;
    gap: 6px;
    padding: 8px 10px 10px;
    flex-wrap: wrap;
    border-top: 1px solid var(--border-faint);
    background: #0d0a0b;
  }
  .view-tile-bar .tile-star { align-self: center; }
  .view-tile-actions .btn {
    flex: 1 1 auto;
    min-width: 72px;
    padding: 6px 10px;
    font-size: var(--fs-sm);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .view-tile-actions .badge { margin-left: auto; }
</style>
