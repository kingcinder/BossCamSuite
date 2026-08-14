<script lang="ts">
  import { AppState } from '../store.svelte';
  import LiveTile from './LiveTile.svelte';
  import LiveStreamMSE from './LiveStreamMSE.svelte';

  let { appState }: { appState: AppState } = $props();

  // 0 = All: every camera on one auto-fit board (the intuitive default).
  const layouts = [0, 1, 2, 4, 6, 8];

  const layoutClasses: Record<number, string> = {
    0: 'layout-all', 1: 'layout-1', 2: 'layout-2', 4: 'layout-4',
    6: 'layout-6', 8: 'layout-8',
  };

  let allDevices = $derived(appState.orderedDevices);
  let shownDevices = $derived(
    appState.layout === 0 ? allDevices : allDevices.slice(0, appState.layout)
  );

  // Live board summary — computed from ACTUAL per-tile stream state (reported by LiveTile
  // when a frame arrives or the stream retries) + recording jobs + connectivity fallback.
  let liveCount = $derived(allDevices.filter(d => appState.streamStatusByDevice[d.id] === 'live').length);
  let snapshotCount = $derived(allDevices.filter(d => appState.streamStatusByDevice[d.id] === 'snapshot').length);
  let retryingCount = $derived(allDevices.filter(d => appState.streamStatusByDevice[d.id] === 'retrying').length);
  let offlineCount = $derived(allDevices.filter(d => appState.connectivitySnapshots[d.id]?.status === 'Offline').length);
  let recordingCount = $derived(appState.recordingJobs.filter(j => j.isRunning).length);

  let fullscreenSupported = $state(typeof document !== 'undefined' && !!document.documentElement.requestFullscreen);

  // Per-device MSE stream toggle
  let mseDeviceIds = $state(new Set<string>());

  function toggleMse(deviceId: string) {
    const next = new Set(mseDeviceIds);
    if (next.has(deviceId)) {
      next.delete(deviceId);
    } else {
      next.add(deviceId);
    }
    mseDeviceIds = next;
  }

  function resetOrder() {
    appState.resetOrder();
    appState.showToast('View order reset');
  }

  // Full-screen mode (replaces WPF full-screen)
  async function toggleFullscreen() {
    if (!document.fullscreenElement) {
      await document.documentElement.requestFullscreen();
      appState.fullscreenEnabled = true;
    } else {
      await document.exitFullscreen();
      appState.fullscreenEnabled = false;
    }
  }

  // Desktop notifications via Web Notification API (replaces WPF OS toasts)
  function requestNotify() {
    if (!('Notification' in window)) {
      appState.showToast('Notifications not supported in this browser', false);
      return;
    }
    if (Notification.permission === 'granted') {
      appState.notificationsEnabled = !appState.notificationsEnabled;
      appState.showToast(appState.notificationsEnabled ? 'Notifications on' : 'Notifications off');
    } else {
      Notification.requestPermission().then(perm => {
        appState.notificationsEnabled = perm === 'granted';
        if (perm === 'granted') {
          appState.showToast('Desktop notifications enabled');
          new Notification('BossCamSuite', { body: 'Notifications are now active.' });
        } else {
          appState.showToast('Notification permission denied', false);
        }
      });
    }
  }
</script>

<div class="card view-toolbar">
  <div class="toolbar-row">
    <div class="group">
      <span class="group-label">Layout</span>
      <div class="layout-btns" role="group" aria-label="Grid layout">
        {#each layouts as n}
          <button
            type="button"
            class:active={appState.layout === n}
            onclick={() => appState.setLayout(n)}
            data-tip={n === 0 ? 'Show every camera, all streaming at once' : `Show ${n} cameras`}
          >{n === 0 ? 'All' : n}</button>
        {/each}
      </div>
    </div>

    <div class="group">
      <span class="group-label">Stream mode</span>
      <select class="select" bind:value={appState.streamQuality} aria-label="Stream mode">
        <option value="sub">Multi-view (recommended)</option>
        <option value="rtsp">Full motion RTSP (1–2 cams)</option>
        <option value="main">HD RTSP main (heavy)</option>
      </select>
    </div>

    <label class="inline-check">
      <input type="checkbox" bind:checked={appState.liveRefreshEnabled} />
      Live refresh
    </label>

    <select class="select refresh" bind:value={appState.liveInterval} aria-label="Refresh interval">
      <option value={1000}>1s</option>
      <option value={2000}>2s</option>
      <option value={5000}>5s</option>
      <option value={10000}>10s</option>
    </select>

    <button class="btn btn-ghost btn-sm" onclick={resetOrder} type="button">Reset order</button>

    {#if fullscreenSupported}
      <button class="btn btn-ghost btn-sm" onclick={toggleFullscreen} type="button" class:active={appState.fullscreenEnabled}>
        {appState.fullscreenEnabled ? 'Exit fullscreen' : '⛶ Fullscreen'}
      </button>
    {/if}

    <button class="btn btn-ghost btn-sm" onclick={requestNotify} type="button" class:active={appState.notificationsEnabled}>
      {'Notification' in window && Notification.permission === 'granted' ? (appState.notificationsEnabled ? '🔔 On' : '🔕 Off') : '🔔 Enable notifications'}
    </button>
  </div>
  <p class="faint small toolbar-hint">
    Continuous live streams (RTSP→MJPEG via ffmpeg). Drag title bar to rearrange. Click a tile to select for settings/record.
  </p>
</div>

<div class="board-summary" role="status">
  <span class="sum-chip live" data-tip="Cameras with a live video frame currently on screen">
    <span class="dot ok"></span>{liveCount} streaming
  </span>
  {#if snapshotCount > 0}
    <span class="sum-chip snap" data-tip="Video stream unavailable — showing a periodic snapshot still instead">
      📷 {snapshotCount} still
    </span>
  {/if}
  <span class="sum-chip rec" data-tip="Cameras currently recording">
    <span class="rec-dot"></span>{recordingCount} recording
  </span>
  {#if retryingCount > 0}
    <span class="sum-chip warn" data-tip="Cameras whose stream is unavailable — click a tile for details">
      ↻ {retryingCount} retrying
    </span>
  {/if}
  {#if offlineCount > 0}
    <span class="sum-chip dead" data-tip="Cameras unreachable">
      ✕ {offlineCount} offline
    </span>
  {/if}
  <span class="sum-note faint small">All cameras auto-stream · click any tile to control it</span>
</div>

<div class="view-grid {layoutClasses[appState.layout] || 'layout-all'}">
  {#each shownDevices as d, i (d.id)}
    <div class="tile-wrapper">
      {#if mseDeviceIds.has(d.id)}          <LiveStreamMSE device={d} appState={appState} />
        <button onclick={() => toggleMse(d.id)} class="mse-switch" data-tip-pos="below" data-tip="Switch to MJPEG">📹 MJPEG</button>
      {:else}          <LiveTile device={d} index={i} appState={appState} />
        <button onclick={() => toggleMse(d.id)} class="mse-switch" data-tip-pos="below" data-tip="Switch to MSE stream">🎬 MSE</button>
      {/if}
    </div>
  {:else}
    <div class="empty-state">
      <span class="empty-icon">📹</span>
      <span class="empty-title">No cameras yet</span>
      <span class="empty-hint">Add or register a camera to start viewing. Use Quick add in the sidebar, Discover, or Scan subnet.</span>
    </div>
  {/each}
</div>

<style>
  .view-toolbar { margin-bottom: 12px; padding: 14px 16px; }
  .toolbar-row {
    display: flex;
    gap: 14px;
    flex-wrap: wrap;
    align-items: center;
  }
  .group { display: flex; align-items: center; gap: 7px; }
  .group-label { font-size: var(--fs-xs); font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase; color: var(--faint); }
  .toolbar-hint { margin: 10px 0 0; }

  .layout-btns { display: flex; gap: 3px; flex-wrap: wrap; }
  .layout-btns button {
    min-width: 34px;
    font-weight: 700;
    font-size: var(--fs-sm);
    background: #1d1315;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-xs);
    padding: 6px 10px;
    cursor: pointer;
    color: var(--muted);
    font: inherit;
    transition: background 0.15s, border-color 0.15s, color 0.15s, box-shadow 0.15s;
  }
  .layout-btns button:hover { border-color: var(--accent-strong); background: #2a1a16; color: var(--text); }
  .layout-btns button.active {
    background: linear-gradient(180deg, var(--accent-strong), var(--accent-deep));
    border-color: #ffb06a99;
    color: #fff;
    box-shadow: 0 2px 10px var(--accent-glow);
  }

  .inline-check {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    color: var(--muted);
    font-size: var(--fs-sm);
    cursor: pointer;
  }
  .select { width: auto; padding: 6px 10px; font-size: var(--fs-sm); }
  .select.refresh { min-width: 58px; }

  .view-grid {
    display: grid;
    gap: 12px;
    min-height: 50vh;
    align-content: start;
  }
  .view-grid.layout-all { grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); }
  .view-grid.layout-1 { grid-template-columns: 1fr; }
  .view-grid.layout-2 { grid-template-columns: 1fr 1fr; }
  .view-grid.layout-4 { grid-template-columns: 1fr 1fr; }
  .view-grid.layout-6 { grid-template-columns: 1fr 1fr 1fr; }
  .view-grid.layout-8 { grid-template-columns: 1fr 1fr 1fr 1fr; }

  .tile-wrapper {
    position: relative;
    background: var(--bg-deep);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius);
    overflow: hidden;
    min-height: 170px;
    box-shadow: var(--shadow-1);
    transition: box-shadow 0.2s ease, border-color 0.2s ease;
  }
  .tile-wrapper:hover { box-shadow: var(--shadow-2); border-color: var(--border); }

  /* ── Live board summary bar ─────────────────────────── */
  .board-summary {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    align-items: center;
    padding: 9px 12px;
    margin-bottom: 12px;
    background: var(--panel-2);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius);
  }
  .sum-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: var(--fs-sm);
    font-weight: 700;
    padding: 3px 10px;
    border-radius: 999px;
    cursor: help;
    border: 1px solid transparent;
  }
  .sum-chip.live { background: var(--ok-dim); color: var(--ok-text); border-color: #3ecf8e55; }
  .sum-chip.snap { background: var(--warn-dim); color: var(--warn-text); border-color: #cf9e3e55; }
  .sum-chip.rec { background: var(--bad-dim); color: var(--bad-text); border-color: #ff3e3e55; }
  .sum-chip.warn { background: var(--warn-dim); color: var(--warn-text); border-color: #cf9e3e55; }
  .sum-chip.dead { background: #20181a; color: #9a8f8f; border-color: #ffffff1f; }
  .rec-dot {
    display: inline-block;
    width: 8px; height: 8px;
    border-radius: 50%;
    background: var(--bad);
    box-shadow: 0 0 6px rgba(255, 62, 62, 0.8);
    animation: rec-pulse 1.5s infinite;
  }
  .sum-note { margin-left: auto; }

  .mse-switch {
    position: absolute;
    top: 54px;
    right: 6px;
    z-index: 10;
    background: rgba(0, 0, 0, 0.7);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-xs);
    padding: 3px 8px;
    cursor: pointer;
    color: #ddd;
    font: inherit;
    font-size: var(--fs-sm);
    backdrop-filter: blur(4px);
    transition: background 0.15s, border-color 0.15s;
  }
  .mse-switch:hover {
    background: rgba(30, 15, 10, 0.9);
    border-color: var(--accent-strong);
  }

  @media (max-width: 1000px) {
    .view-grid.layout-4,
    .view-grid.layout-6,
    .view-grid.layout-8 { grid-template-columns: 1fr 1fr; }
  }
</style>
