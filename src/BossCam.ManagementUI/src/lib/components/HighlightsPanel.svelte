<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';
  import type { HighlightState } from '../types';

  let { appState }: { appState: AppState } = $props();
  let hlState = $state<HighlightState | null>(null);

  $effect(() => {
    if (appState.activeTab === 'highlights') {
      loadHighlights();
    }
  });

  // Auto-refresh every 10 s while the Highlights tab is visible,
  // in case highlight state changes on the server (e.g. via another client).
  $effect(() => {
    if (appState.activeTab !== 'highlights') return;
    const iv = setInterval(loadHighlights, 10_000);
    return () => clearInterval(iv);
  });

  async function loadHighlights() {
    try {
      hlState = await api.highlights();
    } catch (e: unknown) {
      hlState = null;
      appState.showToast(String(e), false);
    }
  }

  async function selectDevice(deviceId: string) {
    try {
      hlState = await api.highlightSelect(deviceId);
      appState.selectedDeviceId = deviceId;
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function next() {
    hlState = await api.highlightNext();
  }

  async function prev() {
    hlState = await api.highlightPrev();
  }

  async function setStream(mode: 'main' | 'sub') {
    hlState = await api.highlightStream(mode);
  }

  async function record() {
    try {
      const id = hlState?.selected?.id || (hlState as Record<string, unknown> | null)?.['selectedDeviceId'];
      if (!id) {
        appState.showToast('No highlight selected', false);
        return;
      }
      const path = prompt('Highlight recording folder (server path):') || '';
      if (!path.trim()) return;
      await api.recordingStart({ deviceId: String(id), outputDirectory: path.trim() });
      appState.showToast('Highlight recording started');
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }
</script>

<div class="card">
  <h3>Highlight board <span class="muted small">(SignalR push updates)</span></h3>
  <div class="row gap wrap">
    <button onclick={prev} type="button">Prev</button>
    <button onclick={next} type="button" class="accent">Next</button>
    <button onclick={() => setStream('main')} type="button">Prefer Main</button>
    <button onclick={() => setStream('sub')} type="button">Prefer Sub</button>
    <button onclick={record} type="button">Record highlight → highlights folder</button>
  </div>

  {#if hlState}
    <p class="banner">
      {#if hlState.selected}
        Selected #{hlState.selectedIndex}: {hlState.selected.displayName} ({hlState.selected.ipAddress}) · {hlState.preferredStream}
      {:else}
        No highlight selected
      {/if}
    </p>

    <div class="tiles">
      {#each hlState.tiles as t, i}
        <div
          class="tile"
          class:selected={i === hlState!.selectedIndex}
          onclick={() => selectDevice(t.deviceId)}
          role="button"
          tabindex="0"
          onkeydown={(e) => e.key === 'Enter' && selectDevice(t.deviceId)}
        >
          <strong>{t.displayName}</strong>
          <div class="sub">{t.ipAddress} · {t.hardwareModel || ''}</div>
        </div>
      {/each}
    </div>
  {:else}
    <p class="muted">No highlights available.</p>
  {/if}
</div>

<style>
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
  }
  .card h3 { margin: 0 0 10px; }
  .row { display: flex; gap: 10px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 8px; }
  .wrap { flex-wrap: wrap; }
  button {
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
  }
  button:hover { border-color: #ffa33e; background: #331713; }
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
  .banner {
    margin: 12px 0;
    padding: 10px 12px;
    border-radius: 8px;
    background: #2a150f;
    border: 1px solid var(--border);
    word-break: break-word;
  }
  .tiles {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
    gap: 10px;
  }
  .tile {
    border: 1px solid #ff5a1f44;
    border-radius: 10px;
    padding: 10px;
    background: #100c0d;
    cursor: pointer;
  }
  .tile:hover { border-color: var(--accent); background: #2a130f; }
  .tile.selected { border-color: var(--accent); }
  .sub { color: var(--muted); font-size: .82rem; }
  .muted { color: var(--muted); font-size: .9rem; }
  .small { font-size: .82rem; }
</style>
