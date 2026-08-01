import type { DeviceIdentity, MediaStoragePaths, FieldDef, FirmwareArtifact, RecordingJob } from './types';

/* ── Global reactive state via Svelte 5 runes ───────────────── */
// Since stores need to be shared across components, we use a simple
// reactive class pattern that components instantiate via `new AppState()`.

export class AppState {
  devices = $state<DeviceIdentity[]>([]);
  selectedDeviceId = $state<string | null>(null);
  viewOrder = $state<string[]>([]);
  layout = $state<number>(Number(localStorage.getItem('bosscam.viewLayout') || 4));
  liveRefreshEnabled = $state<boolean>(true);
  streamQuality = $state<'sub' | 'main' | 'rtsp'>('sub');
  liveInterval = $state<number>(2000);
  activeTab = $state<string>('viewall');

  // Dirty settings tracker: key → value
  dirtySettings = $state<Record<string, unknown>>({});

  // Raw payloads for each section
  imagePayload = $state<Record<string, unknown> | null>(null);
  streamPayload = $state<Record<string, unknown> | null>(null);
  netPayload = $state<Record<string, unknown> | null>(null);

  // Storage paths
  storagePaths = $state<MediaStoragePaths>({
    continuousRecordings: '',
    highlights: '',
    snapshots: '',
  });

  // Recording
  recordingJobs = $state<RecordingJob[]>([]);
  recordingIndex = $state<string>('[]');

  // Firmware
  firmwareList = $state<FirmwareArtifact[]>([]);

  // Fullscreen
  fullscreenEnabled = $state<boolean>(false);

  // Picture-in-Picture: deviceId currently in PiP window, or null
  pipDeviceId = $state<string | null>(null);

  // Desktop notifications enabled flag
  notificationsEnabled = $state<boolean>(false);

  // Discovery progress (from SignalR DiscoveryProgress events)
  discoveryStatus = $state<{ devicesFound: number; provider: string; complete: boolean; error: string | null } | null>(null);

  // Probe progress (from SignalR ProbeProgress events)
  probeStatus = $state<{ deviceId: string; stage: string; endpointsVerified: number; complete: boolean; error: string | null } | null>(null);

  // Per-device connectivity snapshots (keyed by device ID)
  connectivitySnapshots = $state<Record<string, { status: string; transportResults?: Record<string, boolean>; lastCheckedAt?: string }>>({});

  // Health
  healthInfo = $state<string>('Connecting…');

  // Toast
  toastMessage = $state<string>('');
  toastOk = $state<boolean>(true);
  toastVisible = $state<boolean>(false);
  private _toastTimer?: ReturnType<typeof setTimeout>;

  // Derived
  get selectedDevice(): DeviceIdentity | null {
    return this.devices.find(d => d.id === this.selectedDeviceId) ?? null;
  }

  get orderedDevices(): DeviceIdentity[] {
    const map = new Map(this.devices.map(d => [d.id, d]));
    const ordered: DeviceIdentity[] = [];
    const added = new Set<string>();

    for (const id of this.viewOrder) {
      const d = map.get(id);
      if (d && !added.has(id)) {
        ordered.push(d);
        added.add(id);
      }
    }
    for (const d of this.devices) {
      if (!added.has(d.id)) {
        ordered.push(d);
        added.add(d.id);
      }
    }
    return ordered;
  }

  // Actions
  showToast(msg: string, ok = true) {
    this.toastMessage = msg;
    this.toastOk = ok;
    this.toastVisible = true;
    clearTimeout(this._toastTimer);
    this._toastTimer = setTimeout(() => {
      this.toastVisible = false;
    }, 4500);
  }

  setLayout(n: number) {
    this.layout = n;
    localStorage.setItem('bosscam.viewLayout', String(n));
  }

  syncOrder() {
    const saved: string[] = [];
    try {
      const raw = localStorage.getItem('bosscam.viewOrder');
      if (raw) saved.push(...JSON.parse(raw));
    } catch { /* ignore */ }

    const ids = this.devices.map(d => d.id);
    const next: string[] = [];
    for (const id of saved) {
      if (ids.includes(id) && !next.includes(id)) next.push(id);
    }
    for (const id of ids) {
      if (!next.includes(id)) next.push(id);
    }
    this.viewOrder = next;
    localStorage.setItem('bosscam.viewOrder', JSON.stringify(next));
  }

  persistOrder() {
    localStorage.setItem('bosscam.viewOrder', JSON.stringify(this.viewOrder));
  }

  resetOrder() {
    localStorage.removeItem('bosscam.viewOrder');
    this.syncOrder();
  }
}
