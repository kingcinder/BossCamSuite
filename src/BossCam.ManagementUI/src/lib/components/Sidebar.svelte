<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';
  import { signalR as signalRClient } from '../signalr';
  import DeviceList from './DeviceList.svelte';

  let { appState }: { appState: AppState } = $props();
  let quickIp = $state('');
  let quickPass = $state('');
  let connected = $state(false);
  let scanEnabled = $state(false);

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
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    }
  }
</script>

<aside class="sidebar">
  <div class="brand">
    <div class="logo">BC</div>
    <div>
      <h1>BossCamSuite</h1>
      <p class="muted">Multi-camera operator console</p>
    </div>
  </div>

  <div class="toolbar">
    <button onclick={discover} type="button" disabled={!connected && scanEnabled}>Discover</button>
    <button onclick={scanSubnet} type="button" disabled={scanEnabled}>
      {scanEnabled ? 'Scanning…' : 'Scan subnet'}
    </button>
    <button onclick={refresh} type="button">Refresh</button>
    <button onclick={registerAegon} type="button" class="accent">Register LAN</button>
  </div>

  <!-- Discovery progress indicator -->
  {#if appState.discoveryStatus && !appState.discoveryStatus.complete}
    <div class="scan-progress">
      <div class="scan-bar">
        <div class="scan-fill"></div>
      </div>
      <p class="muted small">
        Scanning {appState.discoveryStatus.provider}… {appState.discoveryStatus.devicesFound} found
        {#if appState.discoveryStatus.error}
          · error: {appState.discoveryStatus.error}
        {/if}
      </p>
    </div>
  {/if}

  <label class="field">
    <span>Quick add IP</span>
    <div class="row">
      <input bind:value={quickIp} placeholder="10.0.0.170" />
      <input bind:value={quickPass} placeholder="password" type="password" />
      <button onclick={addCam} type="button">Add</button>
    </div>
  </label>

  <DeviceList devices={appState.devices} appState={appState} />

  <div class="sidebar-foot muted">
    <span class="signal-dot" class:live={connected} class:dead={!connected}></span>
    {connected ? 'Live' : 'HTTP-only'}
    · {appState.healthInfo}
  </div>
</aside>

<style>
  .sidebar {
    border-right: 1px solid var(--border);
    background: var(--panel);
    padding: 16px;
    display: flex;
    flex-direction: column;
    gap: 12px;
    min-width: 0;
    overflow: auto;
    max-height: 100vh;
  }
  .brand { display: flex; gap: 12px; align-items: center; }
  .brand h1 { margin: 0; font-size: 1.15rem; color: #ffe8dd; }
  .logo {
    width: 44px; height: 44px; border-radius: 12px;
    display: grid; place-items: center;
    background: linear-gradient(145deg, #ff6a1f, #5a1408);
    font-weight: 800;
    border: 1px solid #ff9a4a66;
  }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .toolbar, .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .field {
    display: grid; gap: 6px; font-size: .85rem; color: var(--muted);
  }
  .field input {
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
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
  .sidebar-foot {
    margin-top: auto;
    padding-top: 8px;
    border-top: 1px solid #ffffff14;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .signal-dot {
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    flex-shrink: 0;
  }
  .signal-dot.live { background: #3ecf8e; box-shadow: 0 0 6px #3ecf8e88; }
  .signal-dot.dead { background: #ff6b6b; }

  .scan-progress {
    background: #0e0a0b;
    border: 1px solid #ff5a1f33;
    border-radius: 8px;
    padding: 8px 10px;
  }
  .scan-bar {
    height: 4px;
    background: #2a150f;
    border-radius: 2px;
    overflow: hidden;
    margin-bottom: 4px;
  }
  .scan-fill {
    height: 100%;
    width: 100%;
    background: linear-gradient(90deg, #ff7a2f, #ffb06a, #ff7a2f);
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
    border-radius: 2px;
  }
  @keyframes shimmer {
    0% { background-position: -200% 0; }
    100% { background-position: 200% 0; }
  }
</style>
