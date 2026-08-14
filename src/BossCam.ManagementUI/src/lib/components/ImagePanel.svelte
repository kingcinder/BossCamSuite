<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import SettingsEditor from './SettingsEditor.svelte';
  import MotionGridEditor from './MotionGridEditor.svelte';

  let { appState }: { appState: AppState } = $props();
  let isLoading = $state(true);
  let statusText = $state('Loading image settings…');

  $effect(() => {
    if (appState.activeTab === 'image' && appState.selectedDeviceId && !appState.imagePayload) {
      loadImageSettings();
    }
  });

  async function loadImageSettings() {
    if (!appState.selectedDeviceId) return;
    isLoading = true;
    statusText = 'Loading image settings…';
    try {
      const img = await api.settingGet(appState.selectedDeviceId, '/NetSDK/Video/input/channel/1');
      if (img && typeof img === 'object') {
        appState.imagePayload = img as Record<string, unknown>;
        statusText = 'Loaded from /NetSDK/Video/input/channel/1';
      } else {
        statusText = 'Unexpected image payload shape.';
      }
    } catch (e: unknown) {
      statusText = 'Image load failed: ' + String(e);
    }
    isLoading = false;
  }

  function buildFields(): import('../types').FieldDef[] {
    const p = appState.imagePayload;
    if (!p) return [];
    return [
      { key: 'brightnessLevel', label: 'Brightness', type: 'number', value: p.brightnessLevel ?? 50 },
      { key: 'contrastLevel', label: 'Contrast', type: 'number', value: p.contrastLevel ?? 50 },
      { key: 'saturationLevel', label: 'Saturation', type: 'number', value: p.saturationLevel ?? 50 },
      { key: 'sharpnessLevel', label: 'Sharpness', type: 'number', value: p.sharpnessLevel ?? 50 },
      { key: 'hueLevel', label: 'Hue', type: 'number', value: p.hueLevel ?? 50 },
      { key: 'flipEnabled', label: 'Flip', type: 'bool', value: !!p.flipEnabled },
      { key: 'mirrorEnabled', label: 'Mirror', type: 'bool', value: !!p.mirrorEnabled },
      { key: 'powerLineFrequencyMode', label: 'Power line Hz', type: 'number', value: p.powerLineFrequencyMode ?? 60, min: 50, max: 60 },
    ];
  }

  async function save() {
    if (!appState.selectedDeviceId || !appState.imagePayload) return;
    const keys = Object.keys(appState.dirtySettings).filter(k => k.startsWith('image.'));
    if (!keys.length) {
      appState.showToast('No image edits to save');
      return;
    }
    try {
      const p = { ...appState.imagePayload };
      for (const k of keys) {
        const field = k.slice('image.'.length);
        p[field] = appState.dirtySettings[k];
      }
      await api.settingPut(appState.selectedDeviceId, '/NetSDK/Video/input/channel/1', p);
      const remaining = { ...appState.dirtySettings };
      for (const k of keys) delete remaining[k];
      appState.dirtySettings = remaining;
      appState.showToast('Image settings saved');
      await loadImageSettings();
    } catch (e: unknown) {
      appState.showToast('Save failed: ' + String(e), false);
    }
  }
</script>

<div class="card">
  <h3>Image (video input channel 1)</h3>
  <p class="muted">Brightness, contrast, saturation, sharpness, hue, flip, mirror — loaded live from NetSDK.</p>

  {#if isLoading}
    <p class="muted">Loading…</p>
  {:else}
    <SettingsEditor fields={buildFields()} prefix="image" appState={appState} />
  {/if}

  <div class="row" style="margin-top: 12px;">
    <button onclick={save} type="button" class="btn btn-primary">Save Image Settings</button>
    <button onclick={loadImageSettings} type="button" class="btn">Reload</button>
  </div>
  <p class="muted small">{statusText}</p>
</div>

<MotionGridEditor {appState} />

<style>
  .card {
    background: var(--panel);
    border: 1px solid var(--border-soft);
    border-radius: var(--radius);
    padding: 18px 20px;
    margin-bottom: 16px;
    min-width: 0;
    overflow: hidden;
    box-shadow: var(--shadow-1);
  }
  .card h3 { margin: 0 0 8px; color: var(--text-strong); }
  .muted { color: var(--muted); font-size: var(--fs-md); margin: 0; }
  .small { font-size: var(--fs-sm); }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
</style>
