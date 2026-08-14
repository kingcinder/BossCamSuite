<script lang="ts">
  import { onMount } from 'svelte';
  import type { DeviceIdentity } from '../types';
  import { AppState } from '../store.svelte';
  import LiveStreamMSE from './LiveStreamMSE.svelte';
  import AudioMenu from './AudioMenu.svelte';
  import HotspotMenu from './HotspotMenu.svelte';
  import ImagePanel from './ImagePanel.svelte';
  import StreamPanel from './StreamPanel.svelte';
  import NetworkPanel from './NetworkPanel.svelte';
  import FeaturesPanel from './FeaturesPanel.svelte';
  import TypedSettingsView from './TypedSettingsView.svelte';
  import RecordPanel from './RecordPanel.svelte';
  import AdvancedPanel from './AdvancedPanel.svelte';
  import FirmwarePanel from './FirmwarePanel.svelte';
  import RecoveryPanel from './RecoveryPanel.svelte';

  let { device, appState }: { device: DeviceIdentity; appState: AppState } = $props();

  let muted = $state(true);
  let volume = $state(1);
  let bannerVisible = $state(false);
  let openMenu = $state<string | null>(null);
  let clickTimer: ReturnType<typeof setTimeout> | undefined;
  let audioFlash = $state(false);

  function labelOf(d: DeviceIdentity): string {
    return d.displayName || d.ipAddress || d.id;
  }

  // ── Fullscreen lifecycle ─────────────────────────────────────
  // The feed maximizes into this fixed overlay. Double-click anywhere on the
  // video returns it to its previous size.
  function exitFullscreen() {
    appState.fullscreenDeviceId = null;
  }

  // A single click toggles the option-menu banner; a double-click exits fullscreen.
  // Defer the single-click action ~240 ms so a fast second click is seen as a
  // double-click (exit) instead of a banner flip. A second *slow* click (banner
  // already visible) dismisses the banner again.
  function onStageClick() {
    if (clickTimer) clearTimeout(clickTimer);
    clickTimer = setTimeout(() => {
      clickTimer = undefined;
      if (openMenu) {
        // A slow click while a menu sheet is open dismisses the sheet AND the
        // banner (second slow click = hide the option menus entirely).
        openMenu = null;
        bannerVisible = false;
        return;
      }
      bannerVisible = !bannerVisible;
    }, 240);
  }

  function onStageDblClick() {
    if (clickTimer) {
      clearTimeout(clickTimer);
      clickTimer = undefined;
    }
    exitFullscreen();
  }

  // ── Keyboard: spacebar = audio, Escape/Backspace = dismiss banner ──
  function onKeydown(e: KeyboardEvent) {
    // Never hijack keys while the user is typing in a menu form (e.g. Hotspot
    // password field) — same guard the app root uses for its shortcuts.
    const tag = (e.target as HTMLElement | null)?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
    if (e.key === ' ' || e.code === 'Space') {
      e.preventDefault();
      e.stopPropagation();
      toggleAudio();
      return;
    }
    if (e.key === 'Escape' || e.key === 'Backspace') {
      e.preventDefault();
      e.stopPropagation();
      openMenu = null;
      bannerVisible = false;
    }
  }

  function toggleAudio() {
    muted = !muted;
    audioFlash = true;
    appState.showToast(muted ? '🔇 Audio muted' : '🔊 Audio on');
    setTimeout(() => { audioFlash = false; }, 900);
  }

  function toggleStar() {
    appState.toggleStar(device.id);
    appState.showToast(appState.isStarred(device.id) ? '⭐ Pinned to landing page' : '☆ Unpinned');
  }

  // ── Option-menu tiles (ALL camera options from the bottom banner) ──
  const tiles = [
    { id: 'display', icon: '🎬', label: 'Display', tip: 'Video image & stream options' },
    { id: 'audio', icon: '🔊', label: 'Audio', tip: 'Audio output options' },
    { id: 'network', icon: '🌐', label: 'Network', tip: 'Networking & associated AP' },
    { id: 'hotspot', icon: '📶', label: 'Hotspot', tip: 'Daisy-chain WiFi (join AP / broadcast hotspot)' },
    { id: 'features', icon: '🎛️', label: 'Features', tip: 'All camera control points' },
    { id: 'settings', icon: '⚙️', label: 'Settings', tip: 'Every editable parameter' },
    { id: 'record', icon: '⏺️', label: 'Record', tip: 'Continuous recording & storage' },
    { id: 'advanced', icon: '🛠️', label: 'Advanced', tip: 'Users, persistence, maintenance' },
    { id: 'firmware', icon: '📟', label: 'Firmware', tip: 'Firmware artifacts & upload' },
    { id: 'recovery', icon: '🔧', label: 'Recovery', tip: 'Camera AP recovery & enrollment' },
  ];

  function onTileClick(id: string) {
    openMenu = openMenu === id ? null : id;
  }

  onMount(() => {
    appState.selectedDeviceId = device.id;
    document.addEventListener('keydown', onKeydown, true);
    return () => {
      document.removeEventListener('keydown', onKeydown, true);
      if (clickTimer) clearTimeout(clickTimer);
    };
  });
</script>

<div class="fs-overlay" role="dialog" aria-modal="true" aria-label={`${labelOf(device)} fullscreen`}>
  <!-- Top chrome -->
  <div class="fs-topbar">
    <div class="fs-title">
      <span class="dot ok"></span>
      <strong>{labelOf(device)}</strong>
      <span class="badge neutral">{device.ipAddress || ''}</span>
      <span class="badge info">{appState.streamQuality === 'main' ? 'HD main' : appState.streamQuality === 'rtsp' ? 'RTSP' : 'Sub'}</span>
    </div>
    <div class="fs-actions">
      <button
        type="button"
        class="fs-star"
        class:starred={appState.isStarred(device.id)}
        onclick={toggleStar}
        data-tip={appState.isStarred(device.id) ? 'Pinned — auto-loads on landing. Click to unpin.' : 'Pin to landing page'}
        aria-label="Toggle pin"
      >{appState.isStarred(device.id) ? '★' : '☆'}</button>
      <span class="fs-audio-hint" class:flash={audioFlash}>
        {muted ? '🔇' : '🔊'}
      </span>
      <button type="button" class="btn btn-sm btn-ghost" onclick={exitFullscreen} data-tip="Exit fullscreen (double-click video or Esc)">
        ✕ Exit fullscreen
      </button>
    </div>
  </div>

  <!-- Video stage: single click = banner, double click = exit -->
  <div class="fs-stage" onclick={onStageClick} ondblclick={onStageDblClick}>
    <LiveStreamMSE {device} {appState} bind:muted={muted} bind:volume={volume} />

    {#if bannerVisible}
      <div class="fs-banner" onclick={(e) => e.stopPropagation()}>
        {#each tiles as t (t.id)}
          <button
            type="button"
            class="fs-tile"
            class:active={openMenu === t.id}
            onclick={() => onTileClick(t.id)}
            data-tip-pos="below"
            data-tip={t.tip}
          >
            <span class="fs-tile-icon">{t.icon}</span>
            <span class="fs-tile-label">{t.label}</span>
          </button>
        {/each}
      </div>
    {/if}

    {#if openMenu}
      <div class="fs-sheet" onclick={(e) => e.stopPropagation()}>
        <div class="fs-sheet-head">
          <strong>{tiles.find(t => t.id === openMenu)?.icon} {tiles.find(t => t.id === openMenu)?.label}</strong>
          <span class="faint small">{tiles.find(t => t.id === openMenu)?.tip}</span>
          <button type="button" class="btn btn-sm btn-ghost" onclick={() => openMenu = null}>✕</button>
        </div>
        <div class="fs-sheet-body">
          {#if openMenu === 'display'}
            <ImagePanel {appState} />
            <StreamPanel {appState} />
          {:else if openMenu === 'audio'}
            <AudioMenu {appState} bind:muted={muted} bind:volume={volume} />
          {:else if openMenu === 'network'}
            <NetworkPanel {appState} />
          {:else if openMenu === 'hotspot'}
            <HotspotMenu {appState} />
          {:else if openMenu === 'features'}
            <FeaturesPanel {appState} />
          {:else if openMenu === 'settings'}
            <TypedSettingsView {appState} />
          {:else if openMenu === 'record'}
            <RecordPanel {appState} />
          {:else if openMenu === 'advanced'}
            <AdvancedPanel {appState} />
          {:else if openMenu === 'firmware'}
            <FirmwarePanel {appState} />
          {:else if openMenu === 'recovery'}
            <RecoveryPanel {appState} />
          {/if}
        </div>
      </div>
    {/if}
  </div>

  <!-- Bottom hint bar -->
  <div class="fs-hint" onclick={(e) => e.stopPropagation()}>
    <span><span class="kbd">Double-click</span> fullscreen / restore</span>
    <span><span class="kbd">Space</span> audio {muted ? 'off' : 'on'}</span>
    <span><span class="kbd">Click</span> {bannerVisible ? 'hide' : 'show'} options</span>
    <span><span class="kbd">Esc</span>/<span class="kbd">⌫</span> dismiss menu</span>
  </div>
</div>

<style>
  .fs-overlay {
    position: fixed;
    inset: 0;
    z-index: 500;
    background: #000;
    display: flex;
    flex-direction: column;
    color: var(--text);
  }
  .fs-topbar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    background: linear-gradient(180deg, #171012ee, #0d090aee);
    border-bottom: 1px solid var(--border-soft);
    z-index: 2;
    flex-wrap: wrap;
  }
  .fs-title { display: flex; align-items: center; gap: 10px; min-width: 0; }
  .fs-title strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .fs-actions { display: flex; align-items: center; gap: 8px; }
  .fs-star {
    width: 28px; height: 28px;
    display: inline-flex; align-items: center; justify-content: center;
    border-radius: 50%;
    background: transparent;
    border: 1px solid var(--border-soft);
    color: #7a6a62;
    font-size: 1.1rem;
    cursor: pointer;
    transition: color 0.15s, background 0.15s, border-color 0.15s, transform 0.1s;
  }
  .fs-star:hover { border-color: #ffd25a99; color: #ffd25a; }
  .fs-star:active { transform: scale(0.9); }
  .fs-star.starred {
    color: #ffd25a;
    border-color: #ffd25a88;
    background: linear-gradient(180deg, #4a3c12, #2a2410);
    text-shadow: 0 0 8px rgba(255, 210, 90, 0.65);
  }
  .fs-audio-hint { font-size: 1.05rem; line-height: 1; opacity: 0.85; }
  .fs-audio-hint.flash { animation: audio-pulse 0.9s ease; }
  @keyframes audio-pulse {
    0% { transform: scale(1); opacity: 1; }
    50% { transform: scale(1.35); opacity: 0.6; }
    100% { transform: scale(1); opacity: 0.85; }
  }

  .fs-stage {
    position: relative;
    flex: 1;
    min-height: 0;
    background: #000;
    cursor: default;
    overflow: hidden;
  }
  .fs-stage :global(.mse-wrapper) {
    position: absolute;
    inset: 0;
    min-height: 0;
    border-radius: 0;
  }
  .fs-stage :global(.mse-video) {
    width: 100%;
    height: 100%;
    min-height: 0;
    object-fit: contain;
  }

  /* ── Bottom option-menu banner ─────────────────────────── */
  .fs-banner {
    position: absolute;
    left: 50%;
    transform: translateX(-50%);
    bottom: 14px;
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
    justify-content: center;
    max-width: 96%;
    padding: 8px 10px;
    background: rgba(13, 9, 10, 0.82);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-lg);
    backdrop-filter: blur(10px);
    box-shadow: var(--shadow-3);
    animation: banner-in 0.18s ease-out;
    z-index: 3;
  }
  @keyframes banner-in {
    from { opacity: 0; transform: translateX(-50%) translateY(10px); }
    to { opacity: 1; transform: translateX(-50%) translateY(0); }
  }
  .fs-tile {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 3px;
    min-width: 66px;
    padding: 8px 10px;
    background: #1d1416;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
    cursor: pointer;
    color: var(--text);
    font: inherit;
    transition: background 0.15s, border-color 0.15s, transform 0.1s, box-shadow 0.15s;
  }
  .fs-tile:hover { background: #33201a; border-color: var(--accent-strong); transform: translateY(-2px); }
  .fs-tile:active { transform: translateY(0); }
  .fs-tile.active {
    background: linear-gradient(180deg, #4a2a18, #2a1710);
    border-color: var(--accent-strong);
    box-shadow: 0 0 0 1px var(--accent-glow) inset, 0 4px 14px var(--accent-glow);
  }
  .fs-tile-icon { font-size: 1.25rem; line-height: 1; }
  .fs-tile-label { font-size: 0.66rem; font-weight: 700; letter-spacing: 0.02em; }

  /* ── Menu sheet (opens above the banner) ───────────────── */
  .fs-sheet {
    position: absolute;
    left: 50%;
    transform: translateX(-50%);
    bottom: 84px;
    width: min(640px, 94vw);
    max-height: 46vh;
    display: flex;
    flex-direction: column;
    background: rgba(13, 9, 10, 0.94);
    border: 1px solid var(--border);
    border-radius: var(--radius-lg);
    backdrop-filter: blur(12px);
    box-shadow: var(--shadow-3);
    overflow: hidden;
    z-index: 4;
    animation: sheet-in 0.18s ease-out;
  }
  @keyframes sheet-in {
    from { opacity: 0; transform: translateX(-50%) translateY(12px); }
    to { opacity: 1; transform: translateX(-50%) translateY(0); }
  }
  .fs-sheet-head {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 14px;
    border-bottom: 1px solid var(--border-faint);
    background: #171012;
  }
  .fs-sheet-head .faint { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .fs-sheet-body {
    overflow-y: auto;
    padding: 14px;
    display: grid;
    gap: 12px;
  }
  .fs-sheet-body :global(.card) { margin-bottom: 0; padding: 14px; }

  .fs-hint {
    display: flex;
    gap: 16px;
    flex-wrap: wrap;
    align-items: center;
    padding: 7px 16px;
    background: #0d090aee;
    border-top: 1px solid var(--border-faint);
    font-size: var(--fs-xs);
    color: var(--faint);
    z-index: 2;
  }
</style>
