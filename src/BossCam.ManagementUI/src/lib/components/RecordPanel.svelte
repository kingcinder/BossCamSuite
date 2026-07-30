<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';
  import type { RecordingJob } from '../types';

  let { appState }: { appState: AppState } = $props();
  let pathContinuous = $state('');
  let pathHighlights = $state('');
  let pathSnapshots = $state('');
  let pathStatus = $state('');
  let stopLoading = $state<string | null>(null);

  // Auto-refresh recording jobs when SignalR pushes job events.
  // Also refresh on tab switch (existing $effect behavior).
  $effect(() => {
    if (appState.activeTab === 'record') {
      loadPaths();
      refreshRec();
    }
  });

  // Register a periodic refresh every 15 s while the Record tab is visible.
  // This catches index changes that don't fire SignalR (e.g. housekeeping).
  $effect(() => {
    if (appState.activeTab !== 'record') return;
    const iv = setInterval(refreshRec, 15_000);
    return () => clearInterval(iv);
  });

  async function loadPaths() {
    try {
      const p = await api.storagePaths();
      pathContinuous = p.continuousRecordings || '';
      pathHighlights = p.highlights || '';
      pathSnapshots = p.snapshots || '';
      pathStatus = 'Paths loaded from server.';
    } catch (e: unknown) {
      pathStatus = 'Could not load paths: ' + String(e);
    }
  }

  async function savePaths() {
    if (!pathContinuous.trim() || !pathHighlights.trim() || !pathSnapshots.trim()) {
      appState.showToast('Fill all three folder paths', false);
      return;
    }
    try {
      const p = await api.saveStoragePaths({
        continuousRecordings: pathContinuous.trim(),
        highlights: pathHighlights.trim(),
        snapshots: pathSnapshots.trim(),
      });
      pathContinuous = p.continuousRecordings || pathContinuous.trim();
      pathHighlights = p.highlights || pathHighlights.trim();
      pathSnapshots = p.snapshots || pathSnapshots.trim();
      appState.showToast('Save folders stored on server');
      pathStatus = 'Saved.';
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  function promptAll() {
    const c = prompt('Continuous recordings folder (server path):', pathContinuous);
    if (c === null) return;
    const h = prompt('Highlights folder (server path):', pathHighlights);
    if (h === null) return;
    const s = prompt('Snapshots folder (server path):', pathSnapshots);
    if (s === null) return;
    pathContinuous = c.trim();
    pathHighlights = h.trim();
    pathSnapshots = s.trim();
    savePaths();
  }

  async function startAllRec() {
    try {
      await api.recordingStartAll();
      appState.showToast('Started recordings for all cameras');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function refreshRec() {
    try {
      const jobs = await api.recordingJobs();
      appState.recordingJobs = jobs;
    } catch (e: unknown) {
      appState.recordingJobs = [];
    }
    try {
      const idx = await api.recordingIndex(40);
      appState.recordingIndex = JSON.stringify(idx, null, 2);
    } catch (e: unknown) {
      appState.recordingIndex = String(e);
    }
  }

  async function startSelectedRec() {
    if (!appState.selectedDeviceId) return;
    if (!pathContinuous.trim()) {
      appState.showToast('Set a continuous recordings folder path first', false);
      return;
    }
    try {
      await api.recordingStart({ deviceId: appState.selectedDeviceId, outputDirectory: pathContinuous.trim() });
      appState.showToast('Recording started');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function stopAllRec() {
    try {
      await api.recordingStopAll();
      appState.showToast('Stopped all recordings');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function stopJob(jobId: string) {
    stopLoading = jobId;
    try {
      await api.recordingStop(jobId);
      appState.showToast('Recording stopped');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
    stopLoading = null;
  }

  async function refreshIndex() {
    try {
      await api.recordingIndexRefresh();
      await refreshRec();
      appState.showToast('Index refreshed');
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  // ── Derived helpers ──────────────────────────────────────────
  let runningJobs = $derived(appState.recordingJobs.filter(j => j.isRunning));
  let stoppedJobs = $derived(appState.recordingJobs.filter(j => !j.isRunning));

  function deviceName(id: string): string {
    const d = appState.devices.find(d => d.id === id);
    return d?.displayName || d?.ipAddress || id.slice(0, 8);
  }

  function duration(startedAt: string): string {
    const start = new Date(startedAt).getTime();
    const elapsed = Date.now() - start;
    const mins = Math.floor(elapsed / 60000);
    const secs = Math.floor((elapsed % 60000) / 1000);
    return `${mins}:${String(secs).padStart(2, '0')}`;
  }

  function timeAgo(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleTimeString();
  }
</script>

<div class="card">
  <h3>Save folders</h3>
  <p class="muted">Server-side Linux paths. Set paths here (or use Browse defaults).</p>
  <div class="form-grid paths-grid">
    <div class="form-item">
      <label for="pathContinuous">Continuous recordings folder</label>
      <div class="row">
        <input id="pathContinuous" type="text" bind:value={pathContinuous} placeholder="/home/you/Videos/BossCam/continuous" />
        <button onclick={loadPaths} type="button">Default</button>
      </div>
    </div>
    <div class="form-item">
      <label for="pathHighlights">Highlights folder</label>
      <div class="row">
        <input id="pathHighlights" type="text" bind:value={pathHighlights} placeholder="/home/you/Videos/BossCam/highlights" />
        <button onclick={loadPaths} type="button">Default</button>
      </div>
    </div>
    <div class="form-item">
      <label for="pathSnapshots">Snapshots folder</label>
      <div class="row">
        <input id="pathSnapshots" type="text" bind:value={pathSnapshots} placeholder="/home/you/Pictures/BossCam" />
        <button onclick={loadPaths} type="button">Default</button>
      </div>
    </div>
  </div>
  <div class="row" style="margin-top:12px">
    <button onclick={savePaths} type="button" class="accent">Save folders</button>
    <button onclick={promptAll} type="button">Prompt all three…</button>
  </div>
  <p class="muted small">{pathStatus}</p>
</div>

<div class="card">
  <h3>
    Recording
    <span class="muted small">· auto-refreshes via SignalR</span>
    {#if runningJobs.length > 0}
      <span class="badge-live">{runningJobs.length} active</span>
    {/if}
  </h3>
  <p class="muted">Continuous record uses the high-res main stream into the continuous folder.</p>
  <div class="row gap wrap" style="margin-bottom:12px">
    <button onclick={startSelectedRec} type="button" class="accent" disabled={!appState.selectedDevice}>Start selected</button>
    <button onclick={startAllRec} type="button">Start all cameras</button>
    <button onclick={stopAllRec} type="button" disabled={runningJobs.length === 0}>Stop all</button>
    <button onclick={refreshIndex} type="button">Refresh index</button>
  </div>

  {#if runningJobs.length > 0}
    <h4>Active recordings <span class="badge-live">{runningJobs.length}</span></h4>
    <div class="job-list">
      {#each runningJobs as job (job.id)}
        <div class="job-row live">
          <div class="job-indicator"></div>
          <div class="job-info">
            <strong>{deviceName(job.deviceId)}</strong>
            <span class="sub">{duration(job.startedAt)} · started {timeAgo(job.startedAt)}</span>
          </div>
          <div class="job-meta">
            <span class="chip">{job.segmentSeconds}s segments</span>
            {#if job.sourceUrl}
              <span class="chip sub" title={job.sourceUrl}>src</span>
            {/if}
          </div>
          <button
            onclick={() => stopJob(job.id)}
            type="button"
            class="stop-btn"
            disabled={stopLoading === job.id}
          >
            {stopLoading === job.id ? '⏳' : '⏹ Stop'}
          </button>
        </div>
      {/each}
    </div>
  {:else}
    <div class="empty-rec">
      <p class="muted">No active recordings. Select a camera and click Start selected, or Start all cameras.</p>
    </div>
  {/if}

  {#if stoppedJobs.length > 0}
    <details class="stopped-section">
      <summary>Stopped recordings ({stoppedJobs.length})</summary>
      <div class="job-list">
        {#each stoppedJobs as job (job.id)}
          <div class="job-row stopped">
            <div class="job-indicator stopped"></div>
            <div class="job-info">
              <strong>{deviceName(job.deviceId)}</strong>
              <span class="sub">stopped {timeAgo(job.stoppedAt || job.startedAt)}</span>
            </div>
            <div class="job-meta">
              <span class="chip">{job.segmentSeconds}s</span>
            </div>
          </div>
        {/each}
      </div>
    </details>
  {/if}

  <h4>Indexed segments</h4>
  <pre class="code">{appState.recordingIndex}</pre>
</div>

<style>
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
    overflow: hidden;
  }
  .card h3, .card h4 { margin: 0 0 10px; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
  .sub { color: var(--muted); font-size: .78rem; }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }
  .form-grid { display: grid; gap: 12px; }
  .paths-grid { grid-template-columns: 1fr; }
  .form-item { display: grid; gap: 6px; min-width: 0; }
  .form-item label { color: var(--muted); font-size: .85rem; }
  .form-item input[type="text"] {
    flex: 1;
    min-width: 0;
    background: #0b090bcc;
    border: 1px solid #ff5a1f55;
    border-radius: 8px;
    padding: 8px;
    color: var(--text);
    font: inherit;
  }
  button {
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
  }
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
  .code {
    background: #0b0d10;
    border-radius: 8px;
    padding: 10px;
    overflow: auto;
    max-height: 280px;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    font-size: .82rem;
    border: 1px solid #ffffff12;
  }
  .badge-live {
    display: inline-block;
    background: #1a3a1a;
    color: #3ecf8e;
    font-size: .75rem;
    font-weight: 600;
    padding: 1px 8px;
    border-radius: 10px;
  }
  .job-list {
    display: grid;
    gap: 6px;
    margin-bottom: 12px;
  }
  .job-row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 10px 12px;
    border-radius: 8px;
    border: 1px solid #ff5a1f33;
    background: #0a0809;
    flex-wrap: wrap;
  }
  .job-row.live {
    border-color: #3ecf8e44;
    background: #0a120a;
  }
  .job-row.stopped {
    opacity: 0.65;
  }
  .job-indicator {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: #3ecf8e;
    box-shadow: 0 0 6px #3ecf8e88;
    flex-shrink: 0;
    animation: pulse 2s infinite;
  }
  .job-indicator.stopped {
    background: #666;
    box-shadow: none;
    animation: none;
  }
  @keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.4; }
  }
  .job-info {
    flex: 1;
    min-width: 0;
  }
  .job-info strong { display: block; word-break: break-word; }
  .job-meta {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
  }
  .chip {
    background: #1a1010;
    border-radius: 4px;
    padding: 2px 6px;
    font-size: .72rem;
    color: var(--muted);
  }
  .stop-btn {
    padding: 4px 10px;
    font-size: .82rem;
    border-color: #cf3e3e66;
    color: #ff8f8f;
  }
  .stop-btn:hover:not(:disabled) { border-color: #cf3e3e; background: #3a1a1a; }
  .empty-rec {
    display: grid;
    place-items: center;
    min-height: 80px;
    border: 2px dashed #ff5a1f33;
    border-radius: 8px;
    margin-bottom: 12px;
    padding: 16px;
  }
  .stopped-section {
    margin: 8px 0;
  }
  .stopped-section summary {
    cursor: pointer;
    color: var(--muted);
    font-size: .85rem;
    padding: 4px 0;
  }
  .stopped-section summary:hover { color: var(--text); }
</style>
