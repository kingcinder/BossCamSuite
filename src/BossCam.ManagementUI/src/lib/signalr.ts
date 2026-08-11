import * as signalRCore from '@microsoft/signalr';
import type { DeviceIdentity, RecordingJob, HighlightBoardState } from './types';
import { AppState } from './store.svelte';

/** localStorage key matching api.ts */
const LS_LAN_TOKEN = 'bosscam.lanToken';

/**
 * Manages the SignalR WebSocket connection to the BossCamSuite hub.
 * Attaches typed event handlers that update the shared AppState.
 *
 * Call `connect(store)` once from App.svelte's onMount. The connection
 * auto-reconnects with exponential backoff via @microsoft/signalr's
 * automatic reconnect policy.
 */
export class BossCamSignalR {
  private _connection: signalRCore.HubConnection | null = null;
  private _store: AppState | null = null;
  private _connected = false;

  get connected(): boolean {
    return this._connected;
  }

  /**
   * Open the WebSocket connection and register all event handlers.
   * Idempotent — safe to call multiple times.
   */
  async connect(store: AppState): Promise<void> {
    if (this._connection) return; // already connected or connecting

    this._store = store;

    const connection = new signalRCore.HubConnectionBuilder()
      .withUrl('/hub/bosscam', {
        accessTokenFactory: () => localStorage.getItem(LS_LAN_TOKEN) || '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10_000, 30_000])
      .configureLogging(signalRCore.LogLevel.Warning)
      .build();

    // ── Event handlers (server → client) ────────────────────────

    connection.on('DevicesChanged', (devices: DeviceIdentity[]) => {
      if (!this._store) return;
      this._store.devices = devices;
      this._store.syncOrder();
    });

    connection.on('RecordingJobStarted', (job: RecordingJob) => {
      if (!this._store) return;
      this._store.showToast(`Recording started: ${job.deviceId.slice(0, 8)}`);
      notifyDesktop('Recording started', `Camera ${job.deviceId.slice(0, 8)} started recording.`);
      // Add to recording jobs array immediately for real-time status
      const updated = this._store.recordingJobs.filter(j => j.id !== job.id);
      updated.push(job);
      this._store.recordingJobs = updated;
    });

    connection.on('RecordingJobStopped', (job: RecordingJob) => {
      if (!this._store) return;
      this._store.showToast(`Recording stopped: ${job.deviceId.slice(0, 8)}`);
      notifyDesktop('Recording stopped', `Camera ${job.deviceId.slice(0, 8)} recording stopped.`);
      // Update the recording job in the array
      const updated = this._store.recordingJobs.map(j => j.id === job.id ? job : j);
      this._store.recordingJobs = updated;
    });

    connection.on('HighlightStateChanged', (state: HighlightBoardState) => {
      if (!this._store) return;
      // The HighlightsPanel will re-fetch on next view via its $effect
    });

    connection.on('SnapshotSaved', (deviceId: string, path: string, bytes: number) => {
      if (!this._store) return;
      const kb = (bytes / 1024).toFixed(1);
      this._store.showToast(`Snapshot saved: ${deviceId.slice(0, 8)} (${kb} KB)`);
      notifyDesktop('Snapshot saved', `Camera ${deviceId.slice(0, 8)} — ${kb} KB.`);
    });

    connection.on('DiscoveryProgress', (devicesFound: number, provider: string, complete: boolean, error: string | null) => {
      if (!this._store) return;
      this._store.discoveryStatus = {
        devicesFound,
        provider,
        complete,
        error,
      };
      if (complete) {
        this._store.showToast(error ? `Discovery: ${provider} error — ${error}` : `Discovery: ${provider} found ${devicesFound} devices`);
      }
    });

    connection.on('ProbeProgress', (deviceId: string, stage: string, endpointsVerified: number, complete: boolean, error: string | null) => {
      if (!this._store) return;
      this._store.probeStatus = {
        deviceId,
        stage,
        endpointsVerified,
        complete,
        error,
      };
      if (complete && !error) {
        this._store.showToast(`Probe complete: ${deviceId.slice(0, 8)} — ${endpointsVerified} endpoints verified`);
      }
    });

    connection.on('ConnectivityChanged', (deviceId: string, status: string, transportResults: Record<string, boolean> | null, lastDiagnosticSummary: string | null) => {
      if (!this._store) return;
      this._store.connectivitySnapshots[deviceId] = {
        status,
        transportResults: transportResults || undefined,
        lastCheckedAt: new Date().toISOString(),
      };
      // Trigger reactivity by reassigning
      this._store.connectivitySnapshots = { ...this._store.connectivitySnapshots };

      if (status === 'Offline') {
        this._store.showToast(`Camera ${deviceId.slice(0, 8)} is offline`, false);
      } else if (status === 'Degraded') {
        this._store.showToast(`Camera ${deviceId.slice(0, 8)} has degraded connectivity`, false);
      }
    });

    // ── Lifecycle handlers ──────────────────────────────────────

    connection.onreconnecting(() => {
      this._connected = false;
    });

    connection.onreconnected(() => {
      this._connected = true;
    });

    connection.onclose(() => {
      this._connected = false;
    });

    // ── Start ────────────────────────────────────────────────────

    try {
      await connection.start();
      this._connection = connection;
      this._connected = true;
    } catch (err) {
      // Connection failed — the SPA still works via HTTP;
      // auto-reconnect will retry silently.
      this._connection = null;
      // eslint-disable-next-line no-console
      console.warn('SignalR connection failed — SPA will fall back to HTTP-only mode.', err);
    }
  }

  /** Gracefully stop the connection. */
  async disconnect(): Promise<void> {
    if (!this._connection) return;
    try {
      await this._connection.stop();
    } catch { /* ignore */ }
    this._connection = null;
    this._connected = false;
    this._store = null;
  }
}

/** Singleton instance shared across the SPA. */
export const signalR = new BossCamSignalR();

/**
 * Fire a desktop notification via the Web Notification API.
 * Equivalent to the WPF OS-level toast notifications.
 * Gracefully degrades if notifications are not supported or not granted.
 */
function notifyDesktop(title: string, body: string): void {
  if (typeof Notification === 'undefined') return;
  if (Notification.permission !== 'granted') return;
  try {
    new Notification(title, { body, tag: 'bosscam' });
  } catch {
    // Some browsers (Firefox) may throw in private browsing
  }
}
