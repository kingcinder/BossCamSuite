<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import { signalR as signalRClient } from '../signalr';
  import DeviceList from './DeviceList.svelte';

  let { appState }: { appState: AppState } = $props();
  let quickIp = $state('');
  let quickPass = $state('');
  let connected = $state(false);
  let scanEnabled = $state(false);
  let addOpen = $state(false);

  // Poll the SignalR connection state every 2 s for the indicator dot.
  // The singleton BossCamSignalR.connected is a plain boolean set by
  // the hub lifecycle callbacks (onreconnected / onclose / onreconnecting).
  $effect(() => {
    const iv = setInterval(() => {
      connected = signalRClient.connected;
    }, 2_000);
    return () => clearInterval(iv);
  });

  // Monitoring effect: reset discovery status to idle after 10s from last 'complete' signal
  $effect(() => {
    const status = appState.discoveryStatus;
    if (status?.complete) {
      const timer = setTimeout(() => { appState.discoveryStatus = null; }, 10_000);
      return () => clearTimeout(timer);
    }
  });

  async function discover() {
    try {
      await api.discover();
      // DevicesChanged SignalR event will automatically update appState.devices
      // — but if SignalR isn't connected, fall back to explicit fetch.
      if (!signalRClient.connected) {
        appState.devices = await api.devices();
        appState.syncOrder();
      }
      if (!appState.discoveryStatus?.complete) {
        appState.showToast('Discovery complete');
      }
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function scanSubnet() {
    scanEnabled = true;
    try {
      // Force the subnet sweep (ipRangeOverride='auto') so the button does what its label says
      // even when multicast discovery already found devices.
      await api.scanSubnet();
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    } finally {
      scanEnabled = false;
      if (!signalRClient.connected) {
        appState.devices = await api.devices();
        appState.syncOrder();
      }
    }
  }

  async function refresh() {
    try {
      appState.devices = await api.devices();
      appState.syncOrder();
      appState.showToast('Refreshed');
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function registerAegon() {
    const lorex = prompt('Lorex password (blank if unknown)') ?? '';
    const wvc = prompt('WVC password (blank if unknown)') ?? '';
    try {
      await api.registerAegonLan(lorex, wvc);
      // DevicesChanged event will update the list
      if (!signalRClient.connected) {
        appState.devices = await api.devices();
        appState.syncOrder();
      }
      appState.showToast('LAN cameras registered');
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }

  async function addCam() {
    if (!quickIp.trim()) {
      appState.showToast('Enter IP', false);
      return;
    }
    try {
      await api.register({
        ipAddress: quickIp.trim(),
        port: 80,
        loginName: 'admin',
        password: quickPass,
        hardwareModel: '',
      });
      if (!signalRClient.connected) {
        appState.devices = await api.devices();
        appState.syncOrder();
      }
      appState.showToast('Camera added: ' + quickIp.trim());
      quickIp = '';
      quickPass = '';
      addOpen = false;
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }
</script>

<aside class="sidebar">
  <div class="brand">
    <div class="logo">BC</div>
    <div class="brand-text">
      <h1>BossCamSuite</h1>
      <p class="muted">Operator console</p>
    </div>
    <span class="conn-pill" class:live={connected} class:dead={!connected} data-tip={connected ? 'Live updates active' : 'HTTP-only mode — live updates paused'}>
      <span class="dot" class:ok={connected} class:bad={!connected}></span>
      {connected ? 'LIVE' : 'HTTP'}
    </span>
  </div>

  <div class="actions">
    <button class="btn btn-sm" onclick={discover} type="button" data-tip="Multicast discovery (D)">🔍 Discover</button>
    <button class="btn btn-sm" onclick={scanSubnet} type="button" disabled={scanEnabled} data-tip="Force a full subnet sweep">
      {scanEnabled ? 'Scanning…' : '🌐 Scan subnet'}
    </button>
    <button class="btn btn-sm btn-ghost" onclick={refresh} type="button" data-tip="Reload device list">↻</button>
    <button class="btn btn-sm btn-ghost" onclick={registerAegon} type="button" data-tip="Register Aegon/Lorex LAN cameras">Register LAN</button>
  </div>

  <!-- Discovery progress indicator -->
  {#if appState.discoveryStatus && !appState.discoveryStatus.complete}
    <div class="scan-progress">
      <div class="scan-bar"><div class="scan-fill"></div></div>
      <p class="faint small">
        Scanning {appState.discoveryStatus.provider}… {appState.discoveryStatus.devicesFound} found
        {#if appState.discoveryStatus.error}
          · error: {appState.discoveryStatus.error}
        {/if}
      </p>
    </div>
  {/if}

  <button class="add-toggle" type="button" onclick={() => addOpen = !addOpen} aria-expanded={addOpen}>
    <span>{addOpen ? '▾' : '▸'}</span>
    Quick add camera
    <span class="kbd">IP</span>
  </button>
  {#if addOpen}
    <div class="quick-add">
      <input class="input" bind:value={quickIp} placeholder="10.0.0.170" aria-label="Camera IP address" />
      <input class="input" bind:value={quickPass} placeholder="password" type="password" aria-label="Camera password" />
      <button class="btn btn-primary" onclick={addCam} type="button">Add</button>
    </div>
  {/if}

  <DeviceList devices={appState.devices} appState={appState} />

  <div class="sidebar-foot faint">
    <span class="dot" class:ok={connected} class:bad={!connected}></span>
    <span class="ellipsis">{appState.healthInfo}</span>
    {#if !appState.offlineMode && appState.internetConnectivity !== 'Unknown'}
      <span class="badge" class:ok={appState.internetConnectivity === 'Online'} class:warn={appState.internetConnectivity === 'Offline'}>WAN {appState.internetConnectivity.toLowerCase()}</span>
    {/if}
    {#if appState.offlineMode}
      <span class="badge warn" data-tip="LAN-only mode — cloud paths disabled; cameras keep working.">⚡ LAN-only</span>
    {/if}
    <span class="ctrl-info-hint" aria-hidden="true">Hold Ctrl + hover for info</span>
  </div>
</aside>

<style>
  .sidebar {
    border-right: 1px solid var(--border-soft);
    background: linear-gradient(180deg, var(--panel-solid), #0e0a0b);
    padding: 16px 14px;
    display: flex;
    flex-direction: column;
    gap: 12px;
    min-width: 0;
    overflow: hidden;
    max-height: 100vh;
  }
  .brand { display: flex; gap: 10px; align-items: center; }
  .brand-text { flex: 1; min-width: 0; }
  .brand h1 { margin: 0; font-size: 1.12rem; color: var(--text-strong); letter-spacing: 0.02em; line-height: 1.25; }
  .brand .muted { font-size: var(--fs-xs); }
  .logo {
    width: 42px; height: 42px; border-radius: 12px;
    display: grid; place-items: center;
    background: linear-gradient(145deg, var(--accent-strong), var(--accent-deep));
    font-weight: 800;
    font-size: 1.05rem;
    color: #fff8f2;
    border: 1px solid #ff9a4a66;
    box-shadow: 0 2px 12px var(--accent-glow);
    flex-shrink: 0;
  }
  .conn-pill {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    font-size: 0.62rem;
    font-weight: 800;
    letter-spacing: 0.06em;
    padding: 3px 7px;
    border-radius: 999px;
    border: 1px solid var(--border-cool);
    color: var(--faint);
    cursor: help;
    flex-shrink: 0;
  }
  .conn-pill.live { color: var(--ok-text); border-color: #3ecf8e44; background: #0f2e1a66; }
  .conn-pill.dead { color: var(--bad-text); border-color: #ff6b6b33; background: #2e161666; }

  .actions {
    display: grid;
    grid-template-columns: 1fr 1fr auto auto;
    gap: 6px;
  }
  .actions .btn { padding: 7px 8px; font-size: var(--fs-sm); }

  .scan-progress {
    background: var(--panel-2);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
    padding: 8px 10px;
  }
  .scan-bar {
    height: 4px;
    background: #2a150f;
    border-radius: 2px;
    overflow: hidden;
    margin-bottom: 5px;
  }
  .scan-fill {
    height: 100%;
    width: 100%;
    background: linear-gradient(90deg, var(--accent-strong), #ffb06a, var(--accent-strong));
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
    border-radius: 2px;
  }
  @keyframes shimmer {
    0% { background-position: -200% 0; }
    100% { background-position: 200% 0; }
  }

  .add-toggle {
    display: flex;
    align-items: center;
    gap: 6px;
    background: transparent;
    border: 1px dashed var(--border-soft);
    border-radius: var(--radius-sm);
    padding: 7px 10px;
    color: var(--muted);
    font-weight: 600;
    font-size: var(--fs-sm);
    cursor: pointer;
    text-align: left;
    transition: border-color 0.15s, background 0.15s, color 0.15s;
  }
  .add-toggle:hover { border-color: var(--accent-strong); color: var(--text); background: var(--panel-3); }
  .add-toggle .kbd { margin-left: auto; }

  .quick-add {
    display: grid;
    grid-template-columns: 1fr;
    gap: 6px;
    padding: 10px;
    background: var(--panel-2);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
    animation: tip-in 0.15s ease-out;
  }
  .quick-add .input { padding: 7px 10px; font-size: var(--fs-sm); }

  .sidebar-foot {
    margin-top: auto;
    padding-top: 10px;
    border-top: 1px solid var(--border-cool);
    display: flex;
    align-items: center;
    gap: 7px;
    font-size: var(--fs-xs);
    flex-wrap: wrap;
  }
  .sidebar-foot .ellipsis { max-width: 100%; }

  @media (max-width: 1000px) {
    .sidebar { max-height: 48vh; }
  }
</style>
