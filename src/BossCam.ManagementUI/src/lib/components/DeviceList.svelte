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
      >
        <div class="name">{labelOf(d)}</div>
        <div class="sub">
          {d.ipAddress || '—'} · {d.hardwareModel || d.deviceType || 'camera'}
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
  .device-item .name { font-weight: 600; word-break: break-word; }
  .device-item .sub { color: var(--muted); font-size: .82rem; word-break: break-all; }
  .muted { color: var(--muted); font-size: .9rem; }
</style>
