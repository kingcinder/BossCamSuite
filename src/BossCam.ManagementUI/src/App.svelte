<script lang="ts">
  import { onMount } from 'svelte';
  import { AppState } from './lib/store.svelte';
  import { api } from './lib/api';
  import { signalR } from './lib/signalr';
  import Sidebar from './lib/components/Sidebar.svelte';
  import TopBar from './lib/components/TopBar.svelte';
  import Tabs from './lib/components/Tabs.svelte';
  import ViewGrid from './lib/components/ViewGrid.svelte';
  import Toast from './lib/components/Toast.svelte';
  import ImagePanel from './lib/components/ImagePanel.svelte';
  import StreamPanel from './lib/components/StreamPanel.svelte';
  import NetworkPanel from './lib/components/NetworkPanel.svelte';
  import FeaturesPanel from './lib/components/FeaturesPanel.svelte';
  import RecordPanel from './lib/components/RecordPanel.svelte';
  import HighlightsPanel from './lib/components/HighlightsPanel.svelte';
  import AdvancedPanel from './lib/components/AdvancedPanel.svelte';
  import FirmwarePanel from './lib/components/FirmwarePanel.svelte';
  import RecoveryPanel from './lib/components/RecoveryPanel.svelte';

  let appState = new AppState();

  // Overview tab state (local runes, no naming conflict)
  let snapSrc = $state('');
  let snapHint = $state('Select a camera or open View All.');
  let sourcesHtml = $state('');
  let identityHtml = $state('');
  let fullscreenSupported = $state(typeof document !== 'undefined' && !!document.documentElement.requestFullscreen);
  let healthPollTimer: ReturnType<typeof setInterval> | undefined;

  async function refreshHealth() {
    try {
      const h = await api.health();
      appState.offlineMode = !!h.offlineMode;
      appState.internetConnectivity = h.internetConnectivity || (h.offlineMode ? 'Disabled' : 'Unknown');
      appState.internetConnectivityChangedAt = h.internetConnectivityChangedAt || '';
      appState.healthInfo = `API ok · ${h.platform || ''} · ${h.timestamp || ''}`;
    } catch {
      appState.healthInfo = 'API unreachable';
    }
  }

  // ── Desktop notifications (Web Notification API) ──────────────
  // Equivalent to WPF OS-level toast notifications.
  function requestNotifyPermission() {
    if (!('Notification' in window)) return;
    if (Notification.permission !== 'granted') {
      Notification.requestPermission().then(perm => {
        appState.notificationsEnabled = perm === 'granted';
        if (perm === 'granted') appState.showToast('Desktop notifications enabled');
      });
    } else {
      appState.notificationsEnabled = true;
      appState.showToast('Desktop notifications active');
    }
  }

  // ── Fullscreen toggle (replaces WPF full-screen mode) ────────
  async function toggleFullscreen() {
    if (!document.fullscreenElement) {
      await document.documentElement.requestFullscreen();
      appState.fullscreenEnabled = true;
    } else {
      await document.exitFullscreen();
      appState.fullscreenEnabled = false;
    }
  }

  // ── Keyboard shortcuts (replaces WPF keyboard navigation) ────
  function handleKeyboard(e: KeyboardEvent) {
    // F11 / Escape: fullscreen toggle
    if (e.key === 'F11') {
      e.preventDefault();
      toggleFullscreen();
      return;
    }

    // Don't handle shortcuts while typing in inputs
    if ((e.target as HTMLElement)?.tagName === 'INPUT' || (e.target as HTMLElement)?.tagName === 'TEXTAREA') return;

    switch (e.key) {
      case 'd': case 'D': // D = Discover
        appState.activeTab = 'viewall';
        document.dispatchEvent(new CustomEvent('bosscam:discover'));
        break;
      case 'v': case 'V': // V = View All
        appState.activeTab = 'viewall';
        break;
      case 'f': case 'F': // F = Features
        appState.activeTab = 'features';
        break;
      case 'o': case 'O': // O = Overview
        appState.activeTab = 'overview';
        break;
      case 'i': case 'I': // I = Image
        appState.activeTab = 'image';
        break;
      case 's': case 'S': // S = Stream
        appState.activeTab = 'stream';
        break;
      case 'n': case 'N': // N = Network
        appState.activeTab = 'network';
        break;
      case 'r': case 'R': // R = Record
        appState.activeTab = 'record';
        break;
      case 'h': case 'H': // H = Highlights
        appState.activeTab = 'highlights';
        break;
      case 'x': case 'X': // X = Recovery
        appState.activeTab = 'recovery';
        break;
      case 'a': case 'A': // A = Advanced
        appState.activeTab = 'advanced';
        break;
    }
  }

  $effect(() => {
    if (appState.selectedDeviceId && appState.activeTab === 'overview') {
      loadDevicePreview();
      loadSourcesAndIdentity();
    }
  });

  function loadDevicePreview() {
    if (!appState.selectedDeviceId) {
      snapSrc = '';
      snapHint = 'Select a camera or open View All.';
      return;
    }
    snapSrc = api.liveMjpegUrl(appState.selectedDeviceId, appState.streamQuality);
    snapHint = '';
  }

  async function loadSourcesAndIdentity() {
    const d = appState.selectedDevice;
    if (!d) {
      sourcesHtml = '';
      identityHtml = '';
      return;
    }
    identityHtml = [
      ['Name', d.displayName ?? ''],
      ['IP', d.ipAddress ?? ''],
      ['Port', String(d.port)],
      ['Model', d.hardwareModel ?? ''],
      ['Firmware', d.firmwareVersion ?? ''],
      ['Type', d.deviceType ?? ''],
      ['ESEE', d.eseeId ?? ''],
      ['Serial', d.deviceId ?? ''],
      ['Login', d.loginName ?? ''],
    ].map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v || '—')}</dd>`).join('');

    try {
      const sources = await api.sources(d.id);
      sourcesHtml = (sources || []).slice(0, 16)
        .map(s => `<li><strong>${esc(s.displayName || s.kind)}</strong> r${s.rank}: ${esc(s.url)}</li>`)
        .join('');
    } catch (e: unknown) {
      sourcesHtml = `<li>${esc(String(e))}</li>`;
    }
  }

  function esc(s: unknown): string {
    return String(s ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;');
  }

  async function saveSnapshotAction() {
    if (!appState.selectedDeviceId) return;
    try {
      await api.saveSnapshot(appState.selectedDeviceId);
      appState.showToast('Snapshot saved');
    } catch {
      window.open(api.snapshotUrl(appState.selectedDeviceId), '_blank');
      appState.showToast('Server save failed — opened in new tab', false);
    }
  }

  async function saveChangesAction() {
    if (!appState.selectedDevice) return;
    document.dispatchEvent(new CustomEvent('bosscam:save-trigger', { detail: { tab: appState.activeTab } }));
  }

  // ── Auto-record on launch ──────────────────────────────────
  // Open on the Live Wall with every camera streaming, and make sure
  // the fleet is actually recording: start-all is idempotent
  // (RecordingService.StartAsync dedups by profile), so this never
  // double-starts an active job — it only ensures each camera has one.
  async function ensureFleetRecording() {
    // 1. Render current recording state instantly (tiles + status strip).
    try {
      appState.recordingJobs = await api.recordingJobs();
    } catch {
      // Non-fatal: tiles fall back to their per-tile Record button.
    }

    // 2. Ensure every camera is recording (idempotent; can take tens of
    //    seconds server-side while unreachable cameras are probed).
    let startAllOk = false;
    try {
      await api.recordingStartAll();
      startAllOk = true;
    } catch {
      // Non-fatal: the operator can start recording from the Record tab.
    }

    // 3. Refresh so tiles flip to REC once start-all settles.
    try {
      appState.recordingJobs = await api.recordingJobs();
    } catch {
      // Non-fatal.
    }

    const active = appState.recordingJobs.filter(j => j.isRunning).length;
    if (startAllOk && active > 0) {
      appState.showToast(
        `${active} camera${active === 1 ? '' : 's'} recording — fleet auto-start on`
      );
    }
  }

  onMount(() => {
    // Start async initialization without making onMount itself async
    (async () => {
      await refreshHealth();

      try {
        appState.devices = await api.devices();
        appState.syncOrder();
      } catch (e: unknown) {
        appState.healthInfo = 'Devices load failed: ' + String(e);
      }

      // Fetch initial connectivity snapshots for all devices
      try {
        const snapshots = await api.connectivityAll();
        const map: Record<string, { status: string; transportResults?: Record<string, boolean>; lastCheckedAt?: string }> = {};
        for (const snap of snapshots || []) {
          map[snap.deviceId] = {
            status: snap.status,
            transportResults: snap.transportResults || undefined,
            lastCheckedAt: snap.lastCheckedAt,
          };
        }
        if (Object.keys(map).length > 0) {
          appState.connectivitySnapshots = map;
        }
      } catch {
        // Connectivity snapshots are optional; degrade gracefully
      }

      // Connect to SignalR for real-time push events.
      // If connection fails, the SPA degrades gracefully to HTTP-only mode.
      signalR.connect(appState);

      // Open on the Live Wall (all cameras streaming at once) and
      // guarantee continuous recording across the fleet.
      appState.activeTab = 'viewall';
      void ensureFleetRecording();
    })();

    document.addEventListener('bosscam:discover', () => {
      api.discover().then(() => {
        if (!signalR.connected) {
          return api.devices().then(d => { appState.devices = d; appState.syncOrder(); });
        }
      }).catch(() => {});
    });

    document.addEventListener('bosscam:save', () => saveChangesAction());
    document.addEventListener('bosscam:refresh-settings', () => {
      appState.imagePayload = null;
      appState.streamPayload = null;
      appState.netPayload = null;
      appState.dirtySettings = {};
      appState.showToast('Settings reloaded');
    });
    document.addEventListener('bosscam:snapshot', () => saveSnapshotAction());

    // Keep automatic WAN transitions visible without user interaction. This is status-only:
    // the service owns transport gating, while LAN recording and streaming continue regardless.
    healthPollTimer = setInterval(() => { void refreshHealth(); }, 15_000);

    // Listen for keyboard shortcuts globally
    document.addEventListener('keydown', handleKeyboard);

    // Track fullscreen changes
    document.addEventListener('fullscreenchange', () => {
      appState.fullscreenEnabled = !!document.fullscreenElement;
    });

    return () => {
      signalR.disconnect();
      if (healthPollTimer) clearInterval(healthPollTimer);
      healthPollTimer = undefined;
      document.removeEventListener('keydown', handleKeyboard);
    };
  });
</script>

<div id="app">
  <Sidebar appState={appState} />

  <main class="main">
    <TopBar appState={appState} />
    <Tabs appState={appState} />
    <div class="tab-scroll">
    {#if appState.activeTab === 'viewall'}
      <ViewGrid appState={appState} />
    {/if}

    {#if appState.activeTab === 'overview'}
      <section class="panel active">
        <div class="grid-2">
          <div class="card">
            <h3>Live preview</h3>
            <div class="snap-wrap">
              {#if snapSrc}
                <img src={snapSrc} alt="Live preview" class="show" />
              {:else}
                <p class="muted">{snapHint}</p>
              {/if}
            </div>
          </div>
          <div class="card">
            <h3>Identity</h3>
            <dl class="kv">{@html identityHtml}</dl>
            <h3>Sources (high-res first)</h3>
            <ul class="plain">{@html sourcesHtml}</ul>
          </div>
        </div>
      </section>
    {/if}

    {#if appState.activeTab === 'features'}
      <section class="panel active">
        <FeaturesPanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'image'}
      <section class="panel active">
        <ImagePanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'stream'}
      <section class="panel active">
        <StreamPanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'network'}
      <section class="panel active">
        <NetworkPanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'record'}
      <section class="panel active">
        <RecordPanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'highlights'}
      <section class="panel active">
        <HighlightsPanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'recovery'}
      <section class="panel active">
        <RecoveryPanel appState={appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'advanced'}
      <section class="panel active">
        <AdvancedPanel {appState} />
      </section>
    {/if}

    {#if appState.activeTab === 'firmware'}
      <section class="panel active">
        <FirmwarePanel {appState} />
      </section>
    {/if}
    </div>
  </main>
</div>

<Toast appState={appState} />

<style>
  #app {
    display: grid;
    grid-template-columns: minmax(248px, 300px) 1fr;
    min-height: 100vh;
    background:
      radial-gradient(1100px 640px at 100% -10%, rgba(255, 106, 31, 0.10), transparent 60%),
      radial-gradient(900px 500px at -10% 110%, rgba(255, 106, 31, 0.06), transparent 55%),
      linear-gradient(160deg, #0d090a 0%, #050506 55%, #110a0b 100%);
    background-attachment: fixed;
  }
  .main {
    min-width: 0;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    padding: 18px 20px 28px;
    max-height: 100vh;
  }
  .tab-scroll {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    overflow-x: hidden;
    padding: 2px 4px 24px 2px;
    scroll-behavior: smooth;
  }
  .panel { display: block; }
  .grid-2 {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
    gap: 16px;
  }
  .snap-wrap {
    min-height: 240px;
    background: var(--bg-deep);
    border-radius: var(--radius);
    overflow: hidden;
    display: grid;
    place-items: center;
    border: 1px solid var(--border-faint);
  }
  .snap-wrap img {
    max-width: 100%;
    max-height: 440px;
    object-fit: contain;
  }
  .kv { display: grid; grid-template-columns: 150px 1fr; gap: 6px 12px; margin: 0 0 16px; }
  :global(.kv dt) { color: var(--faint); font-size: var(--fs-sm); }
  :global(.kv dd) { margin: 0; word-break: break-word; font-size: var(--fs-md); }
  .plain { margin: 0; padding-left: 18px; }
  :global(.plain li) { margin: 4px 0; word-break: break-all; color: var(--muted); font-size: var(--fs-sm); }
  .muted { color: var(--muted); font-size: var(--fs-md); margin: 0; }

  @media (max-width: 1000px) {
    #app { grid-template-columns: 1fr; }
    .main { max-height: 100vh; }
    .tab-scroll { max-height: none; }
  }
</style>
