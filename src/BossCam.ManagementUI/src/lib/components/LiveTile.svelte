<script lang="ts">
  import type { DeviceIdentity } from '../types';
  import { AppState } from '../store';
  import { api } from '../api';

  let { device, index, appState }: { device: DeviceIdentity; index: number; appState: AppState } = $props();

  function labelOf(d: DeviceIdentity): string {
    return d.displayName || d.ipAddress || d.id;
  }

  // MJPEG stream handling
  let streamUrl = $derived(api.liveMjpegUrl(device.id, appState.streamQuality));
  let streamFailed = $state(false);
  let streamErrorMsg = $state('Connecting live stream…');
  let imgKey = $state(0);
  let isDragging = $state(false);
  let isDragOver = $state(false);

  function onStreamLoad() {
    streamFailed = false;
  }

  function onStreamError() {
    streamFailed = true;
    streamErrorMsg = 'Stream failed — retrying…';
    setTimeout(() => {
      imgKey += 1;
      streamFailed = false;
    }, 1500);
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
    <span class="sub">#{index + 1}</span>
  </div>
  <div class="view-tile-media">
    {#key imgKey}
      <img
        src={streamUrl}
        alt={labelOf(device)}
        class="live-mjpeg"
        decoding="async"
        onload={onStreamLoad}
        onerror={onStreamError}
      />
    {/key}
    {#if streamFailed}
      <div class="fail">{streamErrorMsg}</div>
    {/if}
  </div>
  <div class="view-tile-actions">
    <button onclick={select} type="button">Select</button>
    <button onclick={snap} type="button">Snapshot</button>
    {#if isRecording}
      <button onclick={stopRec} type="button" class="stop">⏹ Stop rec</button>
      <span class="rec-badge">● REC</span>
    {:else}
      <button onclick={startRec} type="button">Record</button>
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
  .view-tile-bar strong { word-break: break-word; display: flex; align-items: center; gap: 4px; }
  .view-tile-bar strong.recording { color: #ffb06a; }
  .view-tile-bar .sub { color: var(--muted); font-size: .78rem; }
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
    gap: 4px;
    padding: 4px 8px 8px;
    flex-wrap: wrap;
  }
  .view-tile-actions button {
    padding: 4px 8px;
    font-size: .78rem;
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
