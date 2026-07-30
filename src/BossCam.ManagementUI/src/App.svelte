<script lang="ts">
  import { onMount } from 'svelte';
  import { AppState } from './lib/store';
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
  import RecordPanel from './lib/components/RecordPanel.svelte';
  import HighlightsPanel from './lib/components/HighlightsPanel.svelte';
  import AdvancedPanel from './lib/components/AdvancedPanel.svelte';
  import FirmwarePanel from './lib/components/FirmwarePanel.svelte';

  let appState = new AppState();

  // Overview tab state (local runes, no naming conflict)
  let snapSrc = $state('');
  let snapHint = $state('Select a camera or open View All.');
  let sourcesHtml = $state('');
  let identityHtml = $state('');
  let fullscreenSupported = $state(typeof document !== 'undefined' && !!document.documentElement.requestFullscreen);

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
      case 'a': case 'A': // A = Advanced
        appState.activeTab = 'advanced';
        break;
      case 'f': case 'F': // F = Firmware (only from Advanced tab)
        if (appState.activeTab === 'advanced') {
          // firmware section is inside advanced
        }
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

  onMount(async () => {
    try {
      const h = await api.health();
      appState.healthInfo = `API ok · ${h.platform || ''} · ${h.timestamp || ''}`;
    } catch {
      appState.healthInfo = 'API unreachable';
    }

    try {
      appState.devices = await api.devices();
      appState.syncOrder();
    } catch (e: unknown) {
      appState.healthInfo = 'Devices load failed: ' + String(e);
    }

    // Connect to SignalR for real-time push events.
    // If connection fails, the SPA degrades gracefully to HTTP-only mode.
    signalR.connect(appState);

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

    // Listen for keyboard shortcuts globally
    document.addEventListener('keydown', handleKeyboard);

    // Track fullscreen changes
    document.addEventListener('fullscreenchange', () => {
      appState.fullscreenEnabled = !!document.fullscreenElement;
    });

    return () => {
      signalR.disconnect();
      document.removeEventListener('keydown', handleKeyboard);
    };
  });
</script>

<div id="app">
  <Sidebar appState={appState} />

  <main class="main">
    <TopBar appState={appState} />
    <Tabs appState={appState} />

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
  </main>
</div>

<Toast appState={appState} />

<style>
  #app {
    display: grid;
    grid-template-columns: minmax(240px, 300px) 1fr;
    min-height: 100vh;
    background:
      radial-gradient(1000px 600px at 100% 0%, #3a1208aa, transparent 60%),
      linear-gradient(160deg, #0a0708 0%, #050506 55%, #120a0a 100%);
  }
  .main {
    min-width: 0;
    display: flex;
    flex-direction: column;
    overflow: auto;
    padding: 16px 18px 28px;
    max-height: 100vh;
  }
  .panel { display: block; }
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
    overflow: hidden;
  }
  .card h3 { margin: 0 0 10px; }
  .grid-2 {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
    gap: 14px;
  }
  .snap-wrap {
    min-height: 220px;
    background: #0a0a0a;
    border-radius: 10px;
    overflow: hidden;
    display: grid;
    place-items: center;
  }
  .snap-wrap img {
    max-width: 100%;
    max-height: 420px;
    object-fit: contain;
  }
  .kv { display: grid; grid-template-columns: 140px 1fr; gap: 6px 10px; margin: 0 0 14px; }
  :global(.kv dt) { color: var(--muted); }
  :global(.kv dd) { margin: 0; word-break: break-word; }
  .plain { margin: 0; padding-left: 18px; }
  .plain li { margin: 4px 0; word-break: break-all; }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }

  @media (max-width: 1000px) {
    #app { grid-template-columns: 1fr; }
    .main { max-height: 64vh; }
  }
</style>
