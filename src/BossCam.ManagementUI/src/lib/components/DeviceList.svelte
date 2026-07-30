<script lang="ts">
  import type { DeviceIdentity } from '../types';
  import { AppState } from '../store';

  let { devices, appState }: { devices: DeviceIdentity[]; appState: AppState } = $props();

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
</style>
