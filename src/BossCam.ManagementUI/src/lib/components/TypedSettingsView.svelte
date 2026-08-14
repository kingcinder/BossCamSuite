<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import type { TypedSettingGroupSnapshot } from '../types';

  let { appState }: { appState: AppState } = $props();

  let groups = $state<TypedSettingGroupSnapshot[]>([]);
  let loading = $state(false);
  let applying = $state(false);
  let statusText = $state('');
  let edits = $state<Record<string, unknown>>({});

  async function load() {
    const id = appState.selectedDeviceId;
    if (!id) {
      statusText = 'Select a camera first';
      return;
    }
    loading = true;
    statusText = 'Loading all camera parameters…';
    try {
      groups = await api.typedSettings(id);
      edits = {};
      statusText = groups.length > 0
        ? `${groups.reduce((n, g) => n + g.fields.length, 0)} editable parameters across ${groups.length} groups.`
        : 'No typed parameters returned.';
    } catch (e: unknown) {
      statusText = 'Load failed: ' + String(e);
    } finally {
      loading = false;
    }
  }

  function typeOf(v: unknown): 'bool' | 'number' | 'string' {
    if (typeof v === 'boolean') return 'bool';
    if (typeof v === 'number') return 'number';
    return 'string';
  }

  function asText(v: unknown): string {
    return v === null || v === undefined ? '' : String(v);
  }

  function current(group: TypedSettingGroupSnapshot, key: string): unknown {
    return key in edits ? edits[key] : group.fields.find(f => f.fieldKey === key)?.typedValue;
  }

  function setEdit(key: string, value: unknown) {
    edits = { ...edits, [key]: value };
  }

  async function applyAll() {
    const id = appState.selectedDeviceId;
    const keys = Object.keys(edits);
    if (!id || keys.length === 0) {
      statusText = 'Nothing changed yet — edit a value first.';
      return;
    }
    applying = true;
    statusText = `Applying ${keys.length} change(s)…`;
    try {
      // Backend contract: applyTypedBatch returns one WriteResult per change in the
      // same order as the request, so results[i] maps to keys[i].
      const results = await api.applyTypedBatch(id, keys.map(k => ({ fieldKey: k, value: edits[k] })));
      const ok = results.filter(r => r.success).length;
      statusText = `Applied ${ok}/${results.length}.`;
      appState.showToast(`${ok}/${results.length} parameter(s) saved to camera`);
      if (ok === results.length) {
        edits = {};
        await load(); // full success — refresh live values
      } else {
        // Partial failure: keep only the rejected edits so the operator can retry,
        // and name the fields that the device refused.
        const failedKeys = keys.filter((_, i) => !results[i].success);
        edits = Object.fromEntries(failedKeys.map(k => [k, edits[k]]));
        const reasons = results.map((r, i) => (r.success ? null : `${keys[i]}: ${r.message || r.semanticStatus}`)).filter(Boolean);
        statusText = `Rejected: ${reasons.join(' · ')}`;
      }
    } catch (e: unknown) {
      statusText = 'Apply failed: ' + String(e);
    } finally {
      applying = false;
    }
  }

  function resetAll() {
    edits = {};
    statusText = 'Changes discarded.';
  }
</script>

<div class="typed-view">
  <div class="row">
    <button type="button" class="btn btn-sm" onclick={load} disabled={loading}>
      {loading ? 'Loading…' : '↻ Reload parameters'}
    </button>
    <button type="button" class="btn btn-sm btn-primary" onclick={applyAll} disabled={applying || Object.keys(edits).length === 0}>
      {applying ? 'Applying…' : `💾 Apply ${Object.keys(edits).length} change(s)`}
    </button>
    <button type="button" class="btn btn-sm btn-ghost" onclick={resetAll} disabled={Object.keys(edits).length === 0}>Discard</button>
  </div>
  <p class="muted small">{statusText}</p>

  {#if groups.length === 0}
    <p class="muted">No parameters loaded yet.</p>
  {:else}
    {#each groups as group (group.groupKind)}
      <div class="group-block">
        <h4>{group.groupName || group.groupKind}
          {#if group.fields.length > 0}
            <span class="faint small">({group.fields.length})</span>
          {/if}
        </h4>
        <div class="form-grid">
          {#each group.fields as f (f.fieldKey)}
            {@const t = typeOf(f.typedValue)}
            {@const v = current(group, f.fieldKey)}
            <div class="form-item">
              {#if t === 'bool'}
                <label>
                  <input
                    type="checkbox"
                    checked={!!v}
                    onchange={(e) => setEdit(f.fieldKey, (e.target as HTMLInputElement).checked)}
                  />
                  {f.displayName || f.fieldKey}
                </label>
              {:else if t === 'number'}
                <label>
                  {f.displayName || f.fieldKey}
                  <span class="val">{String(v ?? '')}</span>
                </label>
                <input
                  type="number"
                  value={Number(v ?? 0)}
                  oninput={(e) => setEdit(f.fieldKey, Number((e.target as HTMLInputElement).value))}
                />
              {:else}
                <label for="ts-{group.groupKind}-{f.fieldKey}">{f.displayName || f.fieldKey}</label>
                <input
                  id="ts-{group.groupKind}-{f.fieldKey}"
                  type="text"
                  value={asText(v)}
                  oninput={(e) => setEdit(f.fieldKey, (e.target as HTMLInputElement).value)}
                />
              {/if}
            </div>
          {/each}
        </div>
      </div>
    {/each}
  {/if}
</div>

<style>
  .typed-view { display: grid; gap: 14px; }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .group-block { display: grid; gap: 8px; }
  .group-block h4 { margin: 0; display: flex; align-items: center; gap: 8px; }
  .form-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
    gap: 12px;
    padding: 12px;
    background: #171012;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
  }
  .form-item { display: grid; gap: 5px; min-width: 0; }
  .form-item label { color: var(--faint); font-size: var(--fs-sm); word-break: break-word; display: flex; justify-content: space-between; align-items: center; gap: 8px; }
  .form-item .val { color: var(--accent-strong); font-weight: 700; font-variant-numeric: tabular-nums; }
  .form-item input[type="number"], .form-item input[type="text"] {
    width: 100%;
    background: #0b090bcc;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
    padding: 7px 10px;
    color: var(--text);
    font: inherit;
  }
  .form-item input:focus { outline: none; border-color: var(--accent-strong); box-shadow: 0 0 0 3px var(--accent-glow); }
</style>
