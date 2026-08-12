<script lang="ts">
  import { onDestroy } from 'svelte';
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import type { AutoRecoveryStatus, CameraApInfo, CameraRecoveryRunStatus } from '../types';

  let { appState }: { appState: AppState } = $props();

  let aps = $state<CameraApInfo[]>([]);
  let scanning = $state(false);
  let statusText = $state('Scan to find factory-reset cameras broadcasting their own WiFi hotspot.');
  let runStatus = $state<CameraRecoveryRunStatus | null>(null);
  let activeSerial = $state('');
  let auto = $state<AutoRecoveryStatus | null>(null);
  let pollTimer: ReturnType<typeof setInterval> | undefined;
  let autoTimer: ReturnType<typeof setInterval> | undefined;

  async function loadAuto() {
    try {
      auto = await api.recoveryAutoStatus();
    } catch {
      auto = null;
    }
  }
  void loadAuto();
  // Keep the autonomous-scan banner live while the tab is open (the worker's
  // cadence is 45s, so a 15s refresh tracks it without churning the API).
  autoTimer = setInterval(() => void loadAuto(), 15000);

  async function scan() {
    scanning = true;
    statusText = 'Scanning WiFi for camera hotspots (IPCZ7C34…)…';
    try {
      const res = await api.recoveryScan();
      aps = res.aps || [];
      statusText = res.count > 0
        ? `${res.count} camera AP${res.count === 1 ? '' : 's'} found. Select one and click Recover & Enroll.`
        : 'No camera APs visible right now. Hold the reset button on the camera until it reboots, then rescan.';
    } catch (e: unknown) {
      aps = [];
      statusText = 'Scan failed: ' + String(e);
    }
    scanning = false;
  }

  async function recover(ap: CameraApInfo) {
    activeSerial = ap.serial;
    runStatus = {
      runId: '',
      serial: ap.serial,
      running: true,
      succeeded: false,
      exitCode: null,
      lanIp: null,
      message: 'Starting recovery…',
      logTail: '',
    };
    try {
      const res = await api.recoveryStart(ap.serial, ap.ssid);
      runStatus = { ...runStatus, runId: res.runId };
      pollTimer = setInterval(() => void poll(res.runId), 3000);
      void poll(res.runId);
    } catch (e: unknown) {
      runStatus = { ...runStatus, running: false, message: String(e) };
    }
  }

  async function poll(runId: string) {
    try {
      const s = await api.recoveryStatus(runId);
      runStatus = s;
      if (!s.running) {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = undefined; }
        if (s.succeeded) appState.showToast(`Camera ${s.serial} recovered & enrolled ✓`);
        else appState.showToast(`Recovery failed for ${s.serial}`, false);
        // Refresh device list so the newly enrolled camera appears.
        try { appState.devices = await api.devices(); appState.syncOrder(); } catch { /* non-fatal */ }
      }
    } catch {
      if (pollTimer) { clearInterval(pollTimer); pollTimer = undefined; }
      runStatus = { ...(runStatus ?? {}), running: false, message: 'Status poll failed.' } as CameraRecoveryRunStatus;
    }
  }

  onDestroy(() => {
    if (pollTimer) clearInterval(pollTimer);
    if (autoTimer) clearInterval(autoTimer);
  });
</script>

<div class="card">
  <div class="row gap wrap" style="margin-bottom: 8px;">
    <h3 style="margin: 0;">Camera Recovery</h3>
    <span class="muted small">Factory-reset camera → WiFi hotspot → LAN → Suite</span>
  </div>

  <p class="muted small">
    A factory reset wipes a camera's WiFi credentials, so it drops off the LAN and
    broadcasts its own hotspot (SSID <code>IPCZ7C34…</code>). Recovery joins that
    hotspot, points the camera at your network, then enrolls it here.
  </p>

  {#if auto}
    <div class="auto-bar" class:on={auto.enabled}>
      {#if auto.enabled}
        <span class="auto-dot"></span>
        <strong>Autonomous scan active</strong>
        <span class="auto-detail">
          every {auto.intervalSeconds}s{auto.currentSsid ? ` · on '${auto.currentSsid}'` : ''}{auto.activeSerial ? ` · recovering ${auto.activeSerial}` : ''}
        </span>
        {#if auto.lastAction}
          <span class="auto-action" data-tip="Last cycle: {auto.lastAction}">{auto.lastAction}</span>
        {/if}
      {:else}
        <span class="auto-off">Autonomous scan disabled</span>
        <span class="auto-detail">Enable with <code>BossCam:RecoveryAutoScanEnabled=true</code></span>
      {/if}
    </div>
  {/if}

  <div class="row gap wrap" style="margin-bottom: 12px;">
    <button onclick={scan} type="button" disabled={scanning || runStatus?.running}>
      {scanning ? '⏳ Scanning…' : '📡 Scan for camera hotspots'}
    </button>
    {#if runStatus?.running}
      <span class="recovery-active">⏳ Recovery in progress…</span>
    {/if}
  </div>

  {#if statusText}
    <p class="muted small">{statusText}</p>
  {/if}

  {#if aps.length > 0}
    <div class="ap-list">
      {#each aps as ap (ap.ssid)}
        <div class="ap-row">
          <div class="ap-info">
            <strong>{ap.ssid}</strong>
            <span class="sub">serial {ap.serial} · sig {ap.signal}% · {ap.security}</span>
            <span class="sub bssid">{ap.bssid}</span>
          </div>
          <button
            type="button"
            class="recover-btn"
            onclick={() => recover(ap)}
            disabled={runStatus?.running}
          >
            🔧 Recover &amp; Enroll
          </button>
        </div>
      {/each}
    </div>
  {/if}

  {#if runStatus}
    <div class="run-card" class:failed={runStatus.running === false && !runStatus.succeeded} class:done={runStatus.running === false && runStatus.succeeded}>
      <div class="run-head">
        <span class="dot" class:live={runStatus.running} class:good={runStatus.running === false && runStatus.succeeded}></span>
        <strong>{runStatus.serial}</strong>
        <span class="run-state">
          {runStatus.running ? 'running…' : runStatus.succeeded ? '✔ succeeded' : '✘ failed'}
        </span>
      </div>
      {#if runStatus.message}
        <p class="run-msg">{runStatus.message}</p>
      {/if}
      {#if runStatus.lanIp}
        <p class="run-msg">LAN IP: <code>{runStatus.lanIp}</code></p>
      {/if}
      {#if runStatus.logTail}
        <pre class="run-log">{runStatus.logTail}</pre>
      {/if}
    </div>
  {/if}

  <div class="note">
    <p class="muted small">
      ⚙ Prefer the terminal for full control: <code>bash scripts/recover-and-enroll-camera.sh JAZ7C34…</code>
      · scan-only: <code>--list</code> · camera already on LAN: <code>--enroll-only &lt;ip&gt;</code>
    </p>
  </div>
</div>

<style>
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
  }
  .card h3 { margin: 0 0 10px; }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }
  code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: .8rem; }

  button {
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
    transition: border-color 0.15s, background 0.15s;
  }
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }

  .recovery-active {
    color: #8fbfff;
    font-size: .85rem;
    animation: pulse 1s infinite;
  }
  @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: .5; } }

  .auto-bar {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    background: #0e140f;
    border: 1px solid #3ecf8e44;
    border-radius: 10px;
    padding: 8px 12px;
    margin-bottom: 12px;
    font-size: .85rem;
  }
  .auto-bar:not(.on) { background: #171212; border-color: #ff8f3e33; }
  .auto-dot {
    width: 9px; height: 9px; border-radius: 50%;
    background: #3ecf8e;
    box-shadow: 0 0 8px #3ecf8e88;
    animation: pulse 2s infinite;
  }
  .auto-detail { color: var(--muted); font-size: .78rem; }
  .auto-action {
    color: #8fbfff;
    font-size: .76rem;
    margin-left: auto;
    max-width: 60%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .auto-off { color: #cf6f6f; font-weight: 600; }

  .ap-list { display: flex; flex-direction: column; gap: 8px; margin-top: 10px; }
  .ap-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    background: #0e0a0b;
    border: 1px solid #ff5a1f33;
    border-radius: 10px;
    padding: 10px 12px;
    flex-wrap: wrap;
  }
  .ap-info { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  .ap-info strong { font-size: .95rem; }
  .ap-info .sub { color: var(--muted); font-size: .78rem; }
  .ap-info .bssid { opacity: .7; }
  .recover-btn { background: #2a1a0a; border-color: #ff8f3e88; }

  .run-card {
    margin-top: 12px;
    background: #0e0a0b;
    border: 1px solid #3e8ecf55;
    border-radius: 10px;
    padding: 10px 12px;
  }
  .run-card.done { border-color: #3ecf8e66; }
  .run-card.failed { border-color: #ff6b6b66; }
  .run-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .run-head .dot { width: 8px; height: 8px; border-radius: 50%; background: #777; }
  .run-head .dot.live { background: #3e8ecf; box-shadow: 0 0 6px #3e8ecf88; animation: pulse 1s infinite; }
  .run-head .dot.good { background: #3ecf8e; box-shadow: 0 0 6px #3ecf8e88; }
  .run-state { margin-left: auto; font-size: .78rem; color: var(--muted); }
  .run-msg { color: var(--text); font-size: .85rem; margin: 6px 0 0; }
  .run-log {
    margin: 8px 0 0;
    padding: 8px;
    background: #050506;
    border: 1px solid #ff5a1f22;
    border-radius: 8px;
    max-height: 240px;
    overflow: auto;
    font-size: .72rem;
    line-height: 1.45;
    color: #c9b7ad;
    white-space: pre-wrap;
    word-break: break-word;
  }
  .note { margin-top: 12px; }
</style>
