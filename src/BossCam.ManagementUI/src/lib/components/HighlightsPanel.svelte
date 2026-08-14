<script lang="ts">
  import { AppState } from '../store.svelte';
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
    <button onclick={prev} type="button" class="btn">◀ Prev</button>
    <button onclick={next} type="button" class="btn btn-primary">Next ▶</button>
    <button onclick={() => setStream('main')} type="button" class="btn btn-sm">Prefer Main</button>
    <button onclick={() => setStream('sub')} type="button" class="btn btn-sm">Prefer Sub</button>
    <button onclick={record} type="button" class="btn btn-sm">⏺ Record highlight</button>
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
    border: 1px solid var(--border-soft);
    border-radius: var(--radius);
    padding: 18px 20px;
    margin-bottom: 16px;
    box-shadow: var(--shadow-1);
  }
  .card h3 { margin: 0 0 8px; color: var(--text-strong); }
  .row { display: flex; gap: 10px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 8px; }
  .wrap { flex-wrap: wrap; }
  .banner {
    margin: 12px 0;
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    background: var(--panel-3);
    border: 1px solid var(--border-soft);
    word-break: break-word;
    font-size: var(--fs-md);
  }
  .tiles {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
    gap: 10px;
  }
  .tile {
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
    padding: 10px;
    background: var(--panel-2);
    cursor: pointer;
    transition: border-color 0.15s, background 0.15s, box-shadow 0.15s;
  }
  .tile:hover { border-color: var(--accent-strong); background: var(--panel-3); box-shadow: var(--shadow-1); }
  .tile.selected { border-color: var(--accent-strong); box-shadow: inset 0 0 0 1px var(--accent-glow); }
  .sub { color: var(--faint); font-size: var(--fs-xs); }
  .muted { color: var(--muted); font-size: var(--fs-md); }
  .small { font-size: var(--fs-sm); }
</style>
