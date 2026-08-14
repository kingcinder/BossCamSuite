<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';

  let { appState, muted = $bindable(false), volume = $bindable(1) }: {
    appState: AppState;
    muted?: boolean;
    volume?: number;
  } = $props();

  let loading = $state(false);
  let statusText = $state('');
  let inputDoc = $state<Record<string, unknown> | null>(null);
  let encodeDoc = $state<Record<string, unknown> | null>(null);
  let inputVolume = $state(80);
  let outputVolume = $state(80);
  let encodeEnabled = $state(true);
  let codecType = $state('');
  let saving = $state(false);

  /** Defensively unwrap the adapter's envelope to find the first object doc. */
  function unwrap(v: unknown): Record<string, unknown> | null {
    if (!v || typeof v !== 'object') return null;
    const obj = v as Record<string, unknown>;
    for (const k of ['response', 'Response', 'body', 'Body', 'data']) {
      if (obj[k] && typeof obj[k] === 'object') {
        const inner = unwrap(obj[k]);
        if (inner) return inner;
      }
    }
    return obj;
  }

  function num(doc: Record<string, unknown>, ...keys: string[]): number | null {
    for (const k of keys) {
      const v = doc[k];
      if (typeof v === 'number') return v;
      if (typeof v === 'string' && v.trim() !== '' && !Number.isNaN(Number(v))) return Number(v);
    }
    return null;
  }

  function bool(doc: Record<string, unknown>, ...keys: string[]): boolean | null {
    for (const k of keys) {
      const v = doc[k];
      if (typeof v === 'boolean') return v;
    }
    return null;
  }

  async function load() {
    const id = appState.selectedDeviceId;
    if (!id) {
      statusText = 'Select a camera first';
      return;
    }
    loading = true;
    statusText = 'Loading camera audio settings…';
    try {
      const [inp, enc] = await Promise.all([
        api.settingGet(id, '/NetSDK/Audio/input/channel/1'),
        api.settingGet(id, '/NetSDK/Audio/encode/channel/1'),
      ]);
      const inDoc = unwrap(inp);
      const encDoc = unwrap(enc);
      inputDoc = inDoc;
      encodeDoc = encDoc;
      if (inDoc) {
        const iv = num(inDoc, 'inputVolume', 'inputvolume', 'InputVolume');
        if (iv !== null) inputVolume = iv;
        const ov = num(inDoc, 'outputVolume', 'outputvolume', 'OutputVolume');
        if (ov !== null) outputVolume = ov;
      }
      if (encDoc) {
        const en = bool(encDoc, 'enabled', 'Enabled');
        if (en !== null) encodeEnabled = en;
        codecType = String(encDoc.codecType ?? encDoc.CodecType ?? '');
      }
      statusText = 'Camera audio settings loaded (input/output volume, encode).';
    } catch (e: unknown) {
      statusText = 'Camera audio read failed: ' + String(e);
    } finally {
      loading = false;
    }
  }

  async function applyInput() {
    const id = appState.selectedDeviceId;
    if (!id) return;
    saving = true;
    statusText = 'Applying input/output volume…';
    try {
      const doc = inputDoc ? { ...inputDoc, inputVolume, outputVolume } : { id: 1, inputVolume, outputVolume };
      await api.settingPut(id, '/NetSDK/Audio/input/channel/1', doc);
      statusText = 'Volume applied to camera.';
      appState.showToast('Camera audio volume saved');
    } catch (e: unknown) {
      statusText = 'Apply failed: ' + String(e);
    } finally {
      saving = false;
    }
  }

  async function applyEncode() {
    const id = appState.selectedDeviceId;
    if (!id) return;
    saving = true;
    statusText = 'Applying audio encode…';
    try {
      const doc = encodeDoc ? { ...encodeDoc, enabled: encodeEnabled } : { id: 1, enabled: encodeEnabled };
      await api.settingPut(id, '/NetSDK/Audio/encode/channel/1', doc);
      statusText = 'Audio encode applied.';
      appState.showToast('Camera audio encode saved');
    } catch (e: unknown) {
      statusText = 'Apply failed: ' + String(e);
    } finally {
      saving = false;
    }
  }

  function toggleMute() {
    muted = !muted;
    appState.showToast(muted ? '🔇 Audio muted (spacebar to unmute)' : '🔊 Audio on (spacebar to mute)');
  }
</script>

<div class="menu-body">
  <div class="menu-section">
    <h4>▶ Player output <span class="faint small">(this feed)</span></h4>
    <div class="row">
      <button type="button" class="btn btn-sm" class:active={!muted} onclick={toggleMute}>
        {muted ? '🔇 Muted' : '🔊 On'}
      </button>
      <input
        type="range" min="0" max="1" step="0.01"
        value={volume}
        oninput={(e) => volume = Number(e.currentTarget.value)}
        aria-label="Feed volume"
      />
      <span class="mono small">{Math.round(volume * 100)}%</span>
    </div>
    <p class="faint small">Spacebar toggles the audio output on the fullscreen feed.</p>
  </div>

  <div class="menu-section">
    <h4>🎙 Camera audio <span class="faint small">(NetSDK Audio/input + encode)</span></h4>
    <div class="row">
      <button type="button" class="btn btn-sm" onclick={load} disabled={loading}>
        {loading ? 'Loading…' : '↻ Load camera audio'}
      </button>
    </div>
    <p class="muted small">{statusText}</p>
    {#if inputDoc}
      <div class="field-row">
        <label>Input volume</label>
        <input type="range" min="0" max="100" bind:value={inputVolume} />
        <span class="mono small">{inputVolume}</span>
      </div>
      <div class="field-row">
        <label>Output volume</label>
        <input type="range" min="0" max="100" bind:value={outputVolume} />
        <span class="mono small">{outputVolume}</span>
      </div>
      <button type="button" class="btn btn-sm btn-primary" onclick={applyInput} disabled={saving}>
        Apply volume
      </button>
    {/if}
    {#if encodeDoc}
      <div class="field-row">
        <label class="chk">
          <input type="checkbox" bind:checked={encodeEnabled} /> Audio encode enabled
        </label>
        {#if codecType}<span class="chip">{codecType}</span>{/if}
      </div>
      <button type="button" class="btn btn-sm btn-primary" onclick={applyEncode} disabled={saving}>
        Apply encode
      </button>
    {/if}
  </div>
</div>

<style>
  .menu-body { display: grid; gap: 14px; }
  .menu-section { display: grid; gap: 8px; }
  .menu-section h4 { margin: 0; display: flex; align-items: center; gap: 8px; }
  .row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .row input[type="range"] { flex: 1; min-width: 140px; }
  .field-row { display: flex; align-items: center; gap: 10px; }
  .field-row label { min-width: 110px; color: var(--muted); font-size: var(--fs-sm); }
  .field-row input[type="range"] { flex: 1; }
  .chk { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: var(--fs-sm); }
</style>
