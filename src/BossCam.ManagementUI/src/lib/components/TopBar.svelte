<script lang="ts">
  import { AppState } from '../store';

  let { appState }: { appState: AppState } = $props();

  function labelOf(d: { displayName?: string | null; ipAddress?: string | null; id: string } | null): string {
    return d?.displayName || d?.ipAddress || d?.id || 'View All';
  }
</script>

<div class="topbar">
  <div>
    <h2>{labelOf(appState.selectedDevice)}</h2>
    <p class="muted">
      {#if appState.selectedDevice}
        {appState.selectedDevice.ipAddress || ''} · {appState.selectedDevice.hardwareModel || ''} · fw {appState.selectedDevice.firmwareVersion || 'unknown'}
      {:else}
        Live multi-camera board · drag tiles to reorder
      {/if}
    </p>
  </div>
  <div class="row gap wrap">
    <button class="accent" disabled={!appState.selectedDevice} onclick={() => document.dispatchEvent(new CustomEvent('bosscam:save'))}>
      Save Settings
    </button>
    <button disabled={!appState.selectedDevice} onclick={() => document.dispatchEvent(new CustomEvent('bosscam:refresh-settings'))}>
      Reload Settings
    </button>
    <button disabled={!appState.selectedDevice} onclick={() => document.dispatchEvent(new CustomEvent('bosscam:snapshot'))}>
      Save Snapshot
    </button>
  </div>
</div>

<style>
  .topbar {
    display: flex;
    justify-content: space-between;
    gap: 16px;
    flex-wrap: wrap;
    align-items: flex-start;
    margin-bottom: 8px;
  }
  .topbar h2 { margin: 0 0 4px; word-break: break-word; }
  .muted { color: var(--muted); font-size: .9rem; }
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
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
</style>
