<script lang="ts">
  import { AppState } from '../store.svelte';

  let { appState }: { appState: AppState } = $props();

  function labelOf(d: { displayName?: string | null; ipAddress?: string | null; id: string } | null): string {
    return d?.displayName || d?.ipAddress || d?.id || 'Fleet Overview';
  }

  let subtitle = $derived.by(() => {
    const d = appState.selectedDevice;
    if (!d) {
      return 'Every camera streams automatically — click any tile to configure, snapshot, or record it';
    }
    const bits = [d.ipAddress || ''];
    if (d.hardwareModel) bits.push(d.hardwareModel);
    if (d.firmwareVersion) bits.push(`fw ${d.firmwareVersion}`);
    return bits.filter(Boolean).join(' · ');
  });
</script>

<div class="topbar">
  <div class="title-block">
    <div class="title-row">
      <h2>{labelOf(appState.selectedDevice)}</h2>
      {#if appState.selectedDevice}
        <span class="badge neutral" data-tip={appState.selectedDevice.id}>ID {appState.selectedDevice.id.slice(0, 8)}</span>
      {/if}
    </div>
    <p class="muted">{subtitle}</p>
  </div>
  <div class="actions">
    {#if appState.offlineMode}
      <span class="pill lan" data-tip="BossCam:OfflineMode=true — cloud/P2P tunnels disabled; LAN cameras, streaming, and recording keep working.">
        <span class="dot ok"></span> LAN-only mode
      </span>
    {:else if appState.internetConnectivity === 'Offline'}
      <span class="pill down" data-tip="Internet/cloud access is temporarily unavailable. LAN recording and streaming continue; cloud paths return automatically.">
        <span class="dot warn"></span> WAN offline · auto recovery
      </span>
    {:else if appState.internetConnectivity === 'Online'}
      <span class="pill up" data-tip="Internet/cloud access available. LAN paths remain preferred.">
        <span class="dot ok"></span> WAN online
      </span>
    {/if}
    <button class="btn btn-primary" disabled={!appState.selectedDevice} onclick={() => document.dispatchEvent(new CustomEvent('bosscam:save'))}>
      💾 Save Settings
    </button>
    <button class="btn" disabled={!appState.selectedDevice} onclick={() => document.dispatchEvent(new CustomEvent('bosscam:refresh-settings'))}>
      Reload
    </button>
    <button class="btn btn-ghost" disabled={!appState.selectedDevice} onclick={() => document.dispatchEvent(new CustomEvent('bosscam:snapshot'))} data-tip="Save a still snapshot of the selected camera">
      📸 Snapshot
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
    margin-bottom: 12px;
    padding-bottom: 12px;
    border-bottom: 1px solid var(--border-faint);
  }
  .title-block { min-width: 0; }
  .title-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .topbar h2 { margin: 0 0 3px; word-break: break-word; font-size: var(--fs-2xl); letter-spacing: 0.01em; color: var(--text-strong); }
  .muted { color: var(--muted); font-size: var(--fs-md); margin: 0; }
  .actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }

  .pill {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    font-size: var(--fs-xs);
    font-weight: 700;
    letter-spacing: 0.04em;
    padding: 6px 11px;
    border-radius: 999px;
    cursor: help;
    white-space: nowrap;
  }
  .pill.lan { background: #0f2e1a; border: 1px solid #3ecf8e55; color: var(--ok-text); }
  .pill.down { background: var(--warn-dim); border: 1px solid #cf9e3e66; color: var(--warn-text); }
  .pill.up { background: #0f2e1a; border: 1px solid #3ecf8e55; color: var(--ok-text); }
</style>
