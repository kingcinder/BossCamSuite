<script lang="ts">
  import type { DeviceIdentity, EnrollDeviceResult } from '../types';
  import { api } from '../api';
  import { AppState } from '../store';

  let { devices, appState }: { devices: DeviceIdentity[]; appState: AppState } = $props();

  let enrollBusy = $state(false);
  let enrollAllBusy = $state(false);
  let enrollError = $state('');
  let enrollNotice = $state('');
  let newIp = $state('');
  let newLogin = $state('admin');
  let newPassword = $state('');
  let newStartRecord = $state(true);

  async function addAndRecord() {
    const ip = newIp.trim();
    if (!ip) {
      enrollError = 'IP address is required.';
      return;
    }
    enrollBusy = true;
    enrollError = '';
    enrollNotice = '';
    try {
      const result = await api.enroll({
        ipAddress: ip,
        loginName: newLogin || undefined,
        password: newPassword || undefined,
        startContinuousRecord: newStartRecord,
      });
      reportEnroll(result);
      appState.devices = await api.devices();
      if (result.deviceId) select(result.deviceId);
      newPassword = ''; // never linger with the credential in component state
    } catch (err) {
      enrollError = err instanceof Error ? err.message : String(err);
    } finally {
      enrollBusy = false;
    }
  }

  async function enrollAllDiscovered() {
    // Idempotent: re-enrolling an already-enrolled camera merges by MAC/IP and re-probes, so the
    // whole discovered list is safe to (re)enroll — cameras without a password or env profile fail
    // the credentials step honestly and show up in the per-result message.
    const discovered = devices.filter((d) => d.ipAddress);
    if (discovered.length === 0) {
      enrollError = 'No discovered devices to enroll.';
      return;
    }
    enrollAllBusy = true;
    enrollError = '';
    enrollNotice = '';
    try {
      const results = await api.enrollBatch(
        discovered.map((d) => ({ ipAddress: d.ipAddress!, startContinuousRecord: true }))
      );
      const ok = results.filter((r) => r.enrolled).length;
      const failed = results.length - ok;
      enrollNotice =
        `Enrolled ${ok}/${results.length} camera(s)` +
        (failed > 0
          ? `; ${failed} failed (passwords/environment credential profiles required).`
          : '.');
      appState.devices = await api.devices();
    } catch (err) {
      enrollError = err instanceof Error ? err.message : String(err);
    } finally {
      enrollAllBusy = false;
    }
  }

  function reportEnroll(result: EnrollDeviceResult) {
    const failed = result.steps.filter((s) => !s.success).map((s) => `${s.step}: ${s.message}`).join('; ');
    enrollNotice =
      `${result.enrolled ? 'Enrolled' : 'Enroll failed'} ${result.displayName || result.ipAddress}` +
      (failed ? ` — ${failed}` : '') +
      (result.degradedReason ? ` (degraded: ${result.degradedReason})` : '') +
      (result.continuousJobId ? ' · continuous recording started' : '');
    if (!result.enrolled) {
      enrollError = failed || 'Enroll failed.';
    }
  }

  function labelOf(d: DeviceIdentity): string {
    return d.displayName || d.ipAddress || d.id;
  }

  function select(id: string) {
    appState.selectedDeviceId = id;
    appState.dirtySettings = {};
    appState.imagePayload = null;
    appState.streamPayload = null;
    appState.netPayload = null;
  }

  function connectivityClass(deviceId: string): string {
    const cs = appState.connectivitySnapshots[deviceId];
    if (!cs) return 'unknown';
    switch (cs.status) {
      case 'Healthy': return 'healthy';
      case 'Degraded': return 'degraded';
      case 'Offline': return 'offline';
      default: return 'unknown';
    }
  }

  function connectivityTitle(deviceId: string): string {
    const cs = appState.connectivitySnapshots[deviceId];
    if (!cs) return 'Connectivity: Unknown';
    const transports = cs.transportResults
      ? Object.entries(cs.transportResults).map(([k, v]) => `${k}=${v ? 'ok' : 'fail'}`).join(', ')
      : '';
    return `Status: ${cs.status}${transports ? ' · ' + transports : ''}`;
  }
</script>

<div class="enroll-bar">
  <div class="row">
    <button class="btn" onclick={addAndRecord} disabled={enrollBusy}>
      {enrollBusy ? 'Enrolling…' : 'Add & Record'}
    </button>
    <button class="btn secondary" onclick={enrollAllDiscovered} disabled={enrollAllBusy || devices.length === 0}>
      {enrollAllBusy ? 'Enrolling…' : 'Enroll All Discovered'}
    </button>
  </div>
  <div class="form-row">
    <input placeholder="IP address" bind:value={newIp} />
    <input placeholder="Login" bind:value={newLogin} />
    <input type="password" placeholder="Password (or env profile)" bind:value={newPassword} />
    <label class="chk"><input type="checkbox" bind:checked={newStartRecord} /> continuous</label>
  </div>
  {#if enrollError}<div class="error">{enrollError}</div>{/if}
  {#if enrollNotice}<div class="notice">{enrollNotice}</div>{/if}
</div>

<h2>Cameras <span class="muted">({devices.length})</span></h2>
<ul class="device-list">
  {#each devices as d (d.id)}
    <li>
      <div
        class="device-item"
        class:active={d.id === appState.selectedDeviceId}
        role="button"
        tabindex="0"
        onclick={() => select(d.id)}
        onkeydown={(e) => e.key === 'Enter' && select(d.id)}
        title={connectivityTitle(d.id)}
      >
        <div class="name-row">
          <span class="signal-dot {connectivityClass(d.id)}"></span>
          <div class="name">{labelOf(d)}</div>
        </div>
        <div class="sub">
          {d.ipAddress || '—'} · {d.hardwareModel || d.deviceType || 'camera'}
          {#if d.httpControlPort}<span class="ctrl-port">:{(d.httpControlPort)}</span>{/if}
          {#if d.continuousRecord}<span class="rec-badge">continuous</span>{/if}
          {#if d.linkHint === 'Wifi'}<span class="wifi-badge">Wi-Fi</span>{/if}
          {#if appState.connectivitySnapshots[d.id]}
            <span class="conn-badge {connectivityClass(d.id)}">{appState.connectivitySnapshots[d.id].status}</span>
          {/if}
        </div>
      </div>
    </li>
  {/each}
</ul>

<style>
  .device-list {
    list-style: none; margin: 0; padding: 0;
    display: grid; gap: 8px; overflow: auto; flex: 1;
  }
  .device-list li { padding: 0; margin: 0; list-style: none; }
  .device-item {
    border: 1px solid #ff5a1f44;
    border-radius: 10px;
    padding: 10px;
    cursor: pointer;
    background: #0e0a0b;
    outline: none;
  }
  .device-item:hover, .device-item.active {
    border-color: var(--accent);
    background: #2a130f;
  }
  .name-row {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .name-row .name { font-weight: 600; word-break: break-word; }
  .device-item .sub { color: var(--muted); font-size: .82rem; word-break: break-all; margin-top: 2px; }
  .muted { color: var(--muted); font-size: .9rem; }

  .signal-dot {
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    flex-shrink: 0;
    transition: background 0.3s;
  }
  .signal-dot.healthy { background: #3ecf8e; box-shadow: 0 0 6px #3ecf8e88; }
  .signal-dot.degraded { background: #cf9e3e; box-shadow: 0 0 6px #cf9e3e88; }
  .signal-dot.offline { background: #ff6b6b; box-shadow: 0 0 6px #ff6b6b88; }
  .signal-dot.unknown { background: #666; }

  .conn-badge {
    display: inline-block;
    font-size: .7rem;
    padding: 1px 5px;
    border-radius: 4px;
    margin-left: 4px;
    font-weight: 600;
  }
  .conn-badge.healthy { background: #1a3a1a; color: #8fdd8f; }
  .conn-badge.degraded { background: #3a2a1a; color: #ddcf8f; }
  .conn-badge.offline { background: #3a1a1a; color: #ff8f8f; }
  .conn-badge.unknown { background: #1a1a1a; color: #999; }

  .enroll-bar {
    display: grid;
    gap: 8px;
    margin-bottom: 10px;
    padding: 10px;
    border: 1px solid #ff5a1f44;
    border-radius: 10px;
    background: #0e0a0b;
  }
  .enroll-bar .row, .enroll-bar .form-row {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
    align-items: center;
  }
  .enroll-bar input {
    flex: 1;
    min-width: 110px;
    background: #161011;
    border: 1px solid #3a2420;
    border-radius: 6px;
    color: var(--text);
    padding: 5px 8px;
    font-size: .82rem;
  }
  .enroll-bar .chk {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    color: var(--muted);
    font-size: .78rem;
    white-space: nowrap;
  }
  .btn {
    background: var(--accent);
    color: #1a0a04;
    border: none;
    border-radius: 6px;
    padding: 6px 12px;
    font-weight: 700;
    font-size: .82rem;
    cursor: pointer;
    transition: filter .15s;
  }
  .btn.secondary {
    background: #2a130f;
    color: var(--accent);
    border: 1px solid #ff5a1f66;
  }
  .btn:hover { filter: brightness(1.15); }
  .btn:disabled { opacity: .5; cursor: default; filter: none; }
  .enroll-bar .error { color: #ff8f8f; font-size: .78rem; word-break: break-word; }
  .enroll-bar .notice { color: #8fdd8f; font-size: .78rem; word-break: break-word; }

  .rec-badge, .wifi-badge, .ctrl-port {
    display: inline-block;
    font-size: .7rem;
    padding: 1px 5px;
    border-radius: 4px;
    margin-left: 4px;
    font-weight: 600;
  }
  .rec-badge { background: #0f2e1a; color: #5fbf8f; }
  .wifi-badge { background: #2a2410; color: #cfc06f; }
  .ctrl-port { background: #161011; color: #b0a8a8; }
</style>
