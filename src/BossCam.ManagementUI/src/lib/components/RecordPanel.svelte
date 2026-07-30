<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';

  let { appState }: { appState: AppState } = $props();
  let pathContinuous = $state('');
  let pathHighlights = $state('');
  let pathSnapshots = $state('');
  let pathStatus = $state('');

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

  async function ensurePath(prefill: string): Promise<string> {
    let p = prefill || prompt('Enter server folder path:') || '';
    if (!p.trim()) throw new Error('Folder path required');
    return p.trim();
  }

  async function startAllRec() {
    try {
      const path = await ensurePath(pathContinuous);
      for (const d of appState.devices) {
        try {
          await api.recordingStart({
            deviceId: d.id,
            outputDirectory: `${path}/${(d.ipAddress || d.id).replace(/\\./g, '_')}`,
          });
        } catch { /* skip individual failures */ }
      }
      appState.showToast('Started recordings for registered cameras');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function refreshRec() {
    try {
      const jobs = await api.recordingJobs();
      appState.recordingJobs = JSON.stringify(jobs, null, 2);
    } catch (e: unknown) {
      appState.recordingJobs = String(e);
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
    try {
      const path = await ensurePath(pathContinuous);
      await api.recordingStart({ deviceId: appState.selectedDeviceId, outputDirectory: path });
      appState.showToast('Recording started');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function stopAllRec() {
    try {
      await api.recordingStopAll();
      appState.showToast('Stopped recordings');
      await refreshRec();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
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
  <h3>Recording <span class="muted small">(auto-refreshes via SignalR)</span></h3>
  <p class="muted">Continuous record uses the high-res main stream into the continuous folder.</p>
  <div class="row gap wrap">
    <button onclick={startSelectedRec} type="button" class="accent" disabled={!appState.selectedDevice}>Start selected</button>
    <button onclick={startAllRec} type="button">Start all cameras</button>
    <button onclick={stopAllRec} type="button">Stop all</button>
    <button onclick={refreshIndex} type="button">Refresh index</button>
  </div>
  <h4>Jobs</h4>
  <pre class="code">{appState.recordingJobs}</pre>
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
  .card h3, .card h4 { margin: 0 0 10px; }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
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
</style>
