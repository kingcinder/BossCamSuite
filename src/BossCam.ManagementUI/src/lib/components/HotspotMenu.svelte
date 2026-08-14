<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';

  let { appState }: { appState: AppState } = $props();

  let loading = $state(false);
  let scanning = $state(false);
  let statusText = $state('');
  let wirelessDoc = $state<Record<string, unknown> | null>(null);

  // Wireless mode: 'none' | 'accessPoint' | 'stationMode'
  let mode = $state('none');
  // Station mode (join an AP — the daisy-chain link back to the router/AP wifi)
  let stationEssid = $state('');
  let stationPsk = $state('');
  // AP mode (broadcast a hotspot other cameras can join)
  let apEssid = $state('');
  let apPsk = $state('');
  let apChannel = $state('Auto');
  let apWpaMode = $state('WPA2_PSK');
  // Scanned AP list
  let apList = $state<Array<{ essid?: string; bssid?: string; signal?: number | string; security?: string }>>([]);

  const WIRELESS_ENDPOINT = '/NetSDK/Network/interface/4/wireless';

  /** Defensively unwrap the adapter's envelope to find the first object doc. */
  function unwrap(v: unknown): Record<string, unknown> | null {
    if (!v || typeof v !== 'object') return null;
    const obj = v as Record<string, unknown>;
    for (const k of ['response', 'Response', 'body', 'Body', 'data']) {
      if (obj[k] && typeof obj[k] === 'object') {
        const inner = unwrap(obj[k]);
        if (inner) return inner;
      }
    }
    return obj;
  }

  function str(doc: Record<string, unknown>, ...keys: string[]): string {
    for (const k of keys) {
      const v = doc[k];
      if (typeof v === 'string' && v.trim() !== '') return v;
    }
    return '';
  }

  async function load() {
    const id = appState.selectedDeviceId;
    if (!id) {
      statusText = 'Select a camera first';
      return;
    }
    loading = true;
    statusText = 'Loading wireless configuration…';
    try {
      const raw = await api.settingGet(id, WIRELESS_ENDPOINT);
      const doc = unwrap(raw);
      wirelessDoc = doc;
      if (doc) {
        const m = str(doc, 'wirelessMode', 'WirelessMode');
        if (m) mode = m;
        const station = unwrap(doc.stationMode ?? doc.stationmode) ?? doc;
        if (station) {
          const s = str(station as Record<string, unknown>, 'wirelessApEssId', 'wirelessEssId', 'essid', 'ESSID');
          if (s) stationEssid = s;
        }
        const ap = unwrap(doc.accessPointMode ?? doc.accesspointmode) ?? doc;
        if (ap) {
          const e = str(ap as Record<string, unknown>, 'wirelessEssId', 'essid', 'ESSID');
          if (e) apEssid = e;
          const ch = str(ap as Record<string, unknown>, 'wirelessApMode80211nChannel', 'channel', 'Channel');
          if (ch) apChannel = ch;
          const w = str(ap as Record<string, unknown>, 'wirelessWpaMode', 'wpaMode', 'WpaMode');
          if (w) apWpaMode = w;
        }
      }
      statusText = doc
        ? `Wireless mode: ${mode || 'none'}. Configure below to daisy-chain cameras.`
        : 'Wireless config returned no document.';
    } catch (e: unknown) {
      statusText = 'Wireless read failed: ' + String(e);
    } finally {
      loading = false;
    }
  }

  async function scanAps() {
    const id = appState.selectedDeviceId;
    if (!id) return;
    scanning = true;
    statusText = 'Scanning for nearby WiFi networks…';
    try {
      const raw = await api.settingGet(id, '/NetSDK/Wireless/ScanApList');
      const doc = unwrap(raw);
      const list = Array.isArray(doc?.apList ?? doc?.ApList ?? doc?.list)
        ? (doc?.apList ?? doc?.ApList ?? doc?.list) as unknown[]
        : Array.isArray(raw) ? raw as unknown[] : [];
      apList = list.map((a) => {
        const o = unwrap(a);
        return {
          essid: o ? str(o, 'essid', 'ESSID', 'ssid', 'SSID') : '',
          bssid: o ? str(o, 'bssid', 'BSSID', 'mac', 'MAC') : '',
          signal: o ? (typeof o.signal === 'number' || typeof o.signal === 'string' ? o.signal : undefined) : undefined,
          security: o ? str(o, 'security', 'Security', 'encrypt', 'Encrypt') || undefined : undefined,
        };
      });
      statusText = apList.length > 0 ? `${apList.length} network(s) found. Click one to join it.` : 'No networks found.';
    } catch (e: unknown) {
      apList = [];
      statusText = 'AP scan failed (may be gated on this model): ' + String(e);
    } finally {
      scanning = false;
    }
  }

  /** Station mode = this camera joins an AP/router's wifi (the daisy-chain link home). */
  async function applyStation() {
    const id = appState.selectedDeviceId;
    if (!id) return;
    if (!stationEssid.trim()) {
      statusText = 'Enter the AP network name (ESSID) first.';
      return;
    }
    loading = true;
    statusText = 'Joining network ' + stationEssid + '… (camera may drop off briefly)';
    try {
      await api.settingPut(id, WIRELESS_ENDPOINT, {
        wirelessMode: 'stationMode',
        stationMode: {
          wirelessApEssId: stationEssid.trim(),
          wirelessApPsk: stationPsk,
        },
      });
      statusText = 'Station mode applied — camera will join "' + stationEssid + '".';
      appState.showToast('📶 Camera set to join ' + stationEssid);
    } catch (e: unknown) {
      statusText = 'Station mode apply failed: ' + String(e);
    } finally {
      loading = false;
    }
  }

  /** AP mode = this camera broadcasts its own hotspot so other cameras can daisy-chain to it. */
  async function applyApMode() {
    const id = appState.selectedDeviceId;
    if (!id) return;
    if (!apEssid.trim()) {
      statusText = 'Enter a hotspot network name (ESSID) first.';
      return;
    }
    loading = true;
    statusText = 'Starting hotspot ' + apEssid + '… (camera may drop off briefly)';
    try {
      await api.settingPut(id, WIRELESS_ENDPOINT, {
        wirelessMode: 'accessPoint',
        accessPointMode: {
          wirelessEssId: apEssid.trim(),
          wirelessPsk: apPsk,
          wirelessApMode80211nChannel: apChannel,
          wirelessWpaMode: apWpaMode,
        },
      });
      statusText = 'Hotspot enabled — other cameras can join "' + apEssid + '".';
      appState.showToast('📡 Hotspot ' + apEssid + ' broadcasting');
    } catch (e: unknown) {
      statusText = 'Hotspot apply failed: ' + String(e);
    } finally {
      loading = false;
    }
  }

  function pickAp(a: { essid?: string; bssid?: string; signal?: number | string; security?: string }) {
    if (a.essid) stationEssid = a.essid;
    if (a.security && /wpa/i.test(a.security)) apWpaMode = /wpa2/i.test(a.security) ? 'WPA2_PSK' : 'WPA_PSK';
    statusText = `Selected "${a.essid}" — set the password, then Join network.`;
  }
</script>

<div class="menu-body">
  <div class="menu-section">
    <h4>📶 Daisy-chain WiFi <span class="faint small">(NetSDK interface/4 wireless)</span></h4>
    <p class="faint small">
      Far-away cameras link back to your AP's wifi by joining it in <em>station mode</em>, or broadcast
      their own hotspot in <em>AP mode</em> so other cameras can chain through them.
    </p>
    <div class="row">
      <button type="button" class="btn btn-sm" onclick={load} disabled={loading}>↻ Load wireless config</button>
      <button type="button" class="btn btn-sm" onclick={scanAps} disabled={scanning}>
        {scanning ? 'Scanning…' : '📡 Scan networks'}
      </button>
    </div>
    <p class="muted small">{statusText}</p>
    <p class="mono small faint">Mode: {mode || 'unknown'}</p>
  </div>

  {#if apList.length > 0}
    <div class="menu-section">
      <h4>Nearby networks</h4>
      <ul class="ap-list">
        {#each apList as a, i (i)}
          <li>
            <button type="button" class="ap-row" onclick={() => pickAp(a)} data-tip="Click to join this network">
              <span class="ap-name">{a.essid || '(hidden)'}</span>
              {#if a.signal !== undefined}<span class="ap-signal">{a.signal}</span>{/if}
              {#if a.security}<span class="chip">{a.security}</span>{/if}
            </button>
          </li>
        {/each}
      </ul>
    </div>
  {/if}

  <div class="menu-section">
    <h4>🔗 Join a network <span class="faint small">(station mode — link back to AP wifi)</span></h4>
    <div class="field-row">
      <label>Network name</label>
      <input class="input" bind:value={stationEssid} placeholder="MyRouter_5G" />
    </div>
    <div class="field-row">
      <label>Password</label>
      <input class="input" type="password" bind:value={stationPsk} placeholder="wifi password" />
    </div>
    <button type="button" class="btn btn-sm btn-primary" onclick={applyStation} disabled={loading}>🔗 Join network</button>
  </div>

  <div class="menu-section">
    <h4>📡 Broadcast hotspot <span class="faint small">(AP mode — daisy-chain hub)</span></h4>
    <div class="field-row">
      <label>Hotspot name</label>
      <input class="input" bind:value={apEssid} placeholder="BossCam-Relay" />
    </div>
    <div class="field-row">
      <label>Password</label>
      <input class="input" type="password" bind:value={apPsk} placeholder="min 8 chars" />
    </div>
    <div class="field-row">
      <label>Channel</label>
      <select class="select" bind:value={apChannel}>
        {#each ['Auto', '1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13'] as ch (ch)}
          <option value={ch}>{ch}</option>
        {/each}
      </select>
      <label>Security</label>
      <select class="select" bind:value={apWpaMode}>
        <option value="WPA2_PSK">WPA2</option>
        <option value="WPA_PSK">WPA</option>
      </select>
    </div>
    <button type="button" class="btn btn-sm btn-primary" onclick={applyApMode} disabled={loading}>📡 Start hotspot</button>
  </div>
</div>

<style>
  .menu-body { display: grid; gap: 14px; }
  .menu-section { display: grid; gap: 8px; }
  .menu-section h4 { margin: 0; display: flex; align-items: center; gap: 8px; }
  .row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .field-row { display: flex; align-items: center; gap: 10px; }
  .field-row label { min-width: 110px; color: var(--muted); font-size: var(--fs-sm); }
  .field-row .input, .field-row .select { flex: 1; min-width: 0; }
  .ap-list { list-style: none; margin: 0; padding: 0; display: grid; gap: 4px; max-height: 150px; overflow-y: auto; }
  .ap-row {
    width: 100%;
    display: flex;
    align-items: center;
    gap: 10px;
    background: #171012;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-xs);
    padding: 7px 10px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
    font-size: var(--fs-sm);
    transition: border-color 0.15s, background 0.15s;
  }
  .ap-row:hover { border-color: var(--accent-strong); background: #24181a; }
  .ap-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .ap-signal { color: var(--ok-text); font-family: var(--font-mono); font-size: var(--fs-xs); }
</style>
