<script lang="ts">
  import type { FieldDef } from '../types';
  import { AppState } from '../store.svelte';

  let { fields = [], prefix, appState }: {
    fields: FieldDef[];
    prefix: string;
    appState: AppState;
  } = $props();

  function setDirty(key: string, value: unknown) {
    appState.dirtySettings = { ...appState.dirtySettings, [`${prefix}.${key}`]: value };
  }

  function getValue(key: string): unknown {
    const fullKey = `${prefix}.${key}`;
    return fullKey in appState.dirtySettings ? appState.dirtySettings[fullKey] : fields.find(f => f.key === key)?.value;
  }
</script>

{#if fields.length === 0}
  <p class="muted">No fields available. Check camera connectivity and Reload Settings.</p>
{:else}
  <div class="form-grid">
    {#each fields as f (f.key)}
      <div class="form-item">
        {#if f.type === 'bool'}
          <label>
            <input
              type="checkbox"
              checked={!!getValue(f.key)}
              onchange={(e) => setDirty(f.key, (e.target as HTMLInputElement).checked)}
            />
            {f.label}
          </label>
        {:else if f.type === 'number'}
          {@const val = Number(getValue(f.key) ?? 0)}
          <label for="range-{prefix}-{f.key}">
            {f.label}
            <span class="val">{val}</span>
          </label>
          <input
            id="range-{prefix}-{f.key}"
            type="range"
            min={f.min ?? 0}
            max={f.max ?? 100}
            value={val}
            oninput={(e) => {
              const v = Number((e.target as HTMLInputElement).value);
              setDirty(f.key, v);
            }}
          />
        {:else}
          <label for="text-{prefix}-{f.key}">{f.label}</label>
          <input
            id="text-{prefix}-{f.key}"
            type="text"
            value={String(getValue(f.key) ?? '')}
            oninput={(e) => setDirty(f.key, (e.target as HTMLInputElement).value)}
          />
        {/if}
      </div>
    {/each}
  </div>
{/if}

<style>
  .form-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    gap: 14px;
  }
  .form-item { display: grid; gap: 6px; min-width: 0; }
  .form-item label { color: var(--faint); font-size: var(--fs-sm); word-break: break-word; display: flex; justify-content: space-between; align-items: center; gap: 8px; }
  .form-item input[type="range"] { width: 100%; height: 6px; accent-color: var(--accent-strong); cursor: pointer; }
  .form-item .val { font-variant-numeric: tabular-nums; color: var(--accent-strong); font-weight: 700; font-size: var(--fs-md); }
  .form-item input[type="text"] {
    width: 100%;
    background: #0b090bcc;
    border: 1px solid var(--border-soft);
    border-radius: var(--radius-sm);
    padding: 8px 11px;
    color: var(--text);
    font: inherit;
    transition: border-color 0.15s ease, box-shadow 0.15s ease;
  }
  .form-item input[type="text"]:focus {
    outline: none;
    border-color: var(--accent-strong);
    box-shadow: 0 0 0 3px var(--accent-glow);
  }
  .muted { color: var(--muted); font-size: var(--fs-md); }
</style>
