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
>
  <div class="view-tile-bar" class:recording={isRecording}>
    <div>
      <strong class:recording={isRecording}>
        {#if isRecording}<span class="rec-dot"></span>{/if}
        {labelOf(device)}
      </strong>
      <div class="sub">{device.ipAddress || ''} · {device.hardwareModel || ''}</div>
    </div>
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
    <button onclick={select} type="button" data-tip="Select this camera for settings">Select</button>
    <button onclick={snap} type="button" data-tip="Save a snapshot to disk">Snapshot</button>
    {#if isRecording}
      <button onclick={stopRec} type="button" class="stop" data-tip="Stop recording this camera">⏹ Stop rec</button>
      <span class="rec-badge">● REC</span>
    {:else}
      <button onclick={startRec} type="button" data-tip="Start continuous recording">Record</button>
    {/if}
  </div>
</div>

<style>
  .view-tile {
    position: relative;
    background: #0a0809;
    border: 1px solid #ff5a1f55;
    border-radius: 12px;
    overflow: hidden;
    min-height: 160px;
    display: flex;
    flex-direction: column;
    cursor: grab;
    user-select: none;
  }
  .view-tile.dragging { opacity: 0.55; border-color: #ffa33e; }
  .view-tile.drag-over { outline: 2px dashed var(--accent); }
  .view-tile.selected {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px #ff6a1f66 inset;
  }
  .view-tile-bar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 8px;
    padding: 6px 10px;
    background: #1a100ecc;
    font-size: .85rem;
    z-index: 1;
    transition: background 0.3s;
  }
  .view-tile-bar.recording {
    background: #1a1a0ecc;
  }
  .view-tile-bar > div { min-width: 0; flex: 1; }
  .view-tile-bar strong { display: flex; align-items: center; gap: 4px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .view-tile-bar strong.recording { color: #ffb06a; }
  .view-tile-bar .sub { color: var(--muted); font-size: .78rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tile-status {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    font-size: .68rem;
    font-weight: 700;
    padding: 2px 7px;
    border-radius: 999px;
    background: #1a1a1a;
    color: #999;
    border: 1px solid #66666655;
    white-space: nowrap;
    cursor: help;
    flex-shrink: 0;
  }
  .tile-status.live { background: #1a3a1a; color: #8fdd8f; border-color: #3ecf8e66; }
  .tile-status.snap { background: #3a2a1a; color: #ddcf8f; border-color: #cf9e3e66; }
  .tile-status.rec { background: #3a1a1a; color: #ff8f8f; border-color: #ff3e3e66; }
  .rec-dot {
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: #ff3e3e;
    box-shadow: 0 0 6px #ff3e3e88;
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
    background: #000;
    display: grid;
    place-items: center;
    position: relative;
  }
  .view-tile-media img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    background: #000;
    min-height: 120px;
  }
  .view-tile-media .fail {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    color: var(--muted);
    font-size: .85rem;
    padding: 12px;
    text-align: center;
  }
  .view-tile-actions {
    display: flex;
    gap: 6px;
    padding: 6px 10px 10px;
    flex-wrap: wrap;
  }
  .view-tile-actions button {
    flex: 1 1 auto;
    min-width: 76px;
    padding: 6px 10px;
    font-size: .8rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    text-align: center;
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
  }
  .view-tile-actions button:hover { border-color: #ffa33e; background: #331713; }
  .view-tile-actions button.stop { border-color: #cf3e3e66; color: #ff8f8f; }
  .view-tile-actions button.stop:hover { border-color: #cf3e3e; background: #3a1a1a; }
  .rec-badge {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    font-size: .72rem;
    font-weight: 600;
    color: #ff3e3e;
    padding: 2px 6px;
    border: 1px solid #ff3e3e44;
    border-radius: 4px;
    background: #3a1a1a66;
  }
</style>
