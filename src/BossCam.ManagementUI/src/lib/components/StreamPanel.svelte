<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import SettingsEditor from './SettingsEditor.svelte';

  let { appState }: { appState: AppState } = $props();
  let isLoading = $state(true);
  let statusText = $state('Loading stream settings…');

  $effect(() => {
    if (appState.activeTab === 'stream' && appState.selectedDeviceId && !appState.streamPayload) {
      loadStreamSettings();
    }
  });

  async function loadStreamSettings() {
    if (!appState.selectedDeviceId) return;
    isLoading = true;
    statusText = 'Loading stream settings…';
    try {
      const st = await api.settingGet(appState.selectedDeviceId, '/NetSDK/Video/encode/channel/101');
      if (st && typeof st === 'object') {
        appState.streamPayload = st as Record<string, unknown>;
        statusText = 'Loaded from /NetSDK/Video/encode/channel/101 (main high-res)';
      } else {
        statusText = 'Unexpected stream payload.';
      }
    } catch (e: unknown) {
      statusText = 'Stream load failed: ' + String(e);
    }
    isLoading = false;
  }

  function buildFields(): import('../types').FieldDef[] {
    const p = appState.streamPayload;
    if (!p) return [];
    return [
      { key: 'codecType', label: 'Codec', type: 'string', value: p.codecType ?? '' },
      { key: 'resolution', label: 'Resolution', type: 'string', value: p.resolution ?? '' },
      { key: 'frameRate', label: 'Frame rate', type: 'number', value: p.frameRate ?? 15, min: 1, max: 30 },
      { key: 'constantBitRate', label: 'Bitrate (kbps)', type: 'number', value: p.constantBitRate ?? 1536, min: 64, max: 8192 },
      { key: 'bitRateControlType', label: 'Rate control', type: 'string', value: p.bitRateControlType ?? '' },
      { key: 'h264Profile', label: 'Profile', type: 'string', value: p.h264Profile ?? '' },
      { key: 'keyFrameInterval', label: 'Keyframe interval', type: 'number', value: p.keyFrameInterval ?? 30, min: 1, max: 200 },
      { key: 'channelName', label: 'Channel name', type: 'string', value: p.channelName ?? '' },
      { key: 'enabled', label: 'Enabled', type: 'bool', value: p.enabled !== false },
    ];
  }

  async function save() {
    if (!appState.selectedDeviceId || !appState.streamPayload) return;
    const keys = Object.keys(appState.dirtySettings).filter(k => k.startsWith('stream.'));
    if (!keys.length) {
      appState.showToast('No stream edits to save');
      return;
    }
    try {
      const p = { ...appState.streamPayload };
      for (const k of keys) {
        const field = k.slice('stream.'.length);
        p[field] = appState.dirtySettings[k];
      }
      await api.settingPut(appState.selectedDeviceId, '/NetSDK/Video/encode/channel/101', p);
      const remaining = { ...appState.dirtySettings };
      for (const k of keys) delete remaining[k];
      appState.dirtySettings = remaining;
      appState.showToast('Stream settings saved');
      await loadStreamSettings();
    } catch (e: unknown) {
      appState.showToast('Save failed: ' + String(e), false);
    }
  }
</script>

<div class="card">
  <h3>Encode (main channel 101)</h3>
  <p class="muted">High-res main stream metadata from the camera.</p>

  {#if isLoading}
    <p class="muted">Loading…</p>
  {:else}
    <SettingsEditor fields={buildFields()} prefix="stream" appState={appState} />
  {/if}

  <div class="row" style="margin-top: 12px;">
    <button onclick={save} type="button" class="accent">Save Stream Settings</button>
    <button onclick={loadStreamSettings} type="button">Reload</button>
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
