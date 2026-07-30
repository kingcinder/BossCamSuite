<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';
  import SettingsEditor from './SettingsEditor.svelte';

  let { appState }: { appState: AppState } = $props();
  let isLoading = $state(true);
  let statusText = $state('Loading network settings…');

  $effect(() => {
    if (appState.activeTab === 'network' && appState.selectedDeviceId && appState.netPayload === null) {
      loadNetworkSettings();
    }
  });

  async function loadNetworkSettings() {
    if (!appState.selectedDeviceId) return;
    isLoading = true;
    statusText = 'Loading network settings…';
    try {
      let net: unknown = await api.settingGet(appState.selectedDeviceId, '/NetSDK/Network/interface/1');
      if (Array.isArray(net)) net = (net as unknown[])[0] || net;
      if (net && typeof net === 'object') {
        appState.netPayload = net as Record<string, unknown>;
        statusText = 'Loaded from /NetSDK/Network/interface/1';
      } else {
        // Fallback: list endpoint
        try {
          const list = await api.settingGet(appState.selectedDeviceId, '/NetSDK/Network/interface');
          if (Array.isArray(list) && list.length > 0) {
            appState.netPayload = list[0] as Record<string, unknown>;
            statusText = 'Loaded from /NetSDK/Network/interface list';
          } else if (typeof list === 'object' && list !== null) {
            appState.netPayload = list as Record<string, unknown>;
            statusText = 'Loaded from /NetSDK/Network/interface';
          } else {
            statusText = 'No network interface payload.';
          }
        } catch {
          statusText = 'Network load failed from both endpoints.';
        }
      }
    } catch (e: unknown) {
      statusText = 'Network load failed: ' + String(e);
    }
    isLoading = false;
  }

  function buildFields(): import('../types').FieldDef[] {
    const p = appState.netPayload;
    if (!p) return [];
    const lan = (p.lan as Record<string, unknown>) || (p.Lan as Record<string, unknown>) || {};
    return [
      { key: 'interfaceName', label: 'Interface', type: 'string', value: p.interfaceName ?? 'eth0' },
      { key: 'staticIP', label: 'IP address', type: 'string', value: lan.staticIP ?? '' },
      { key: 'staticNetmask', label: 'Netmask', type: 'string', value: lan.staticNetmask ?? '' },
      { key: 'staticGateway', label: 'Gateway', type: 'string', value: lan.staticGateway ?? '' },
      { key: 'dhcp', label: 'DHCP', type: 'bool', value: !!lan.dhcp },
      { key: 'mtu', label: 'MTU', type: 'number', value: lan.mtu ?? 1500, min: 576, max: 9000 },
      { key: 'addressingType', label: 'Addressing', type: 'string', value: lan.addressingType ?? (lan.dhcp ? 'dynamic' : 'static') },
    ];
  }

  async function save() {
    if (!appState.selectedDeviceId || !appState.netPayload) return;
    const keys = Object.keys(appState.dirtySettings).filter(k => k.startsWith('network.'));
    if (!keys.length) {
      appState.showToast('No network edits to save');
      return;
    }
    try {
      const p = JSON.parse(JSON.stringify(appState.netPayload)) as Record<string, unknown>;
      p.lan = (p.lan as Record<string, unknown>) || {};
      const lan = p.lan as Record<string, unknown>;
      for (const k of keys) {
        const field = k.slice('network.'.length);
        if (field === 'interfaceName') {
          p.interfaceName = appState.dirtySettings[k];
        } else if (field === 'mtu') {
          lan.mtu = appState.dirtySettings[k];
        } else {
          lan[field] = appState.dirtySettings[k];
        }
      }
      if (lan.dhcp === true) lan.addressingType = 'dynamic';
      if (lan.dhcp === false && !lan.addressingType) lan.addressingType = 'static';
      await api.settingPut(appState.selectedDeviceId, '/NetSDK/Network/interface/1', p);
      const remaining = { ...appState.dirtySettings };
      for (const k of keys) delete remaining[k];
      appState.dirtySettings = remaining;
      appState.showToast('Network settings saved');
      await loadNetworkSettings();
    } catch (e: unknown) {
      appState.showToast('Save failed: ' + String(e), false);
    }
  }
</script>

<div class="card">
  <h3>Network (interface 1 / eth0)</h3>
  <p class="muted">LAN addressing, DHCP/static, gateway, netmask — loaded live from NetSDK.</p>

  {#if isLoading}
    <p class="muted">Loading…</p>
  {:else}
    <SettingsEditor fields={buildFields()} prefix="network" appState={appState} />
  {/if}

  <div class="row" style="margin-top: 12px;">
    <button onclick={save} type="button" class="accent">Save Network Settings</button>
    <button onclick={loadNetworkSettings} type="button">Reload</button>
  </div>
  <p class="muted small">{statusText}</p>
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
</style>
