<script lang="ts">
  import { AppState } from '../store.svelte';

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
        Every camera streams automatically — click any tile to configure, snapshot, or record it
      {/if}
    </p>
  </div>
  <div class="row gap wrap">
    {#if appState.offlineMode}
      <span class="offline-badge" data-tip="BossCam:OfflineMode=true (or BOSSCAM_OFFLINE=1) — cloud/P2P tunnels and the remote relay are disabled; LAN cameras, streaming, and recording keep working.">
        ⚡ LAN-only mode
      </span>
    {:else if appState.internetConnectivity === 'Offline'}
      <span class="wan-badge down" data-tip="Internet/cloud access is temporarily unavailable. LAN recording and streaming continue; cloud/P2P paths will return automatically when connectivity is restored.">
        ◌ WAN offline · auto recovery
      </span>
    {:else if appState.internetConnectivity === 'Online'}
      <span class="wan-badge up" data-tip="Internet/cloud access is available. LAN paths remain preferred; cloud/P2P is available only as an optional fallback.">
        ● WAN online
      </span>
    {/if}
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
  .offline-badge, .wan-badge {
    font-size: .72rem;
    font-weight: 700;
    letter-spacing: .04em;
    padding: 6px 10px;
    border-radius: 999px;
    background: #14240f;
    border: 1px solid #3ecf8e66;
    color: #7fe0ac;
    cursor: help;
    white-space: nowrap;
  }
  .wan-badge { white-space: nowrap; cursor: help; }
  .wan-badge.down { color: #ffd08a; background: #3a2a12; border-color: #cf9e3e66; }
  .wan-badge.up { color: #8fdd8f; background: #1a3a1a; border-color: #3ecf8e66; }
</style>
