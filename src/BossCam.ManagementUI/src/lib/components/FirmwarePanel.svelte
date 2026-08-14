<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import type { FirmwareArtifact } from '../types';

  let { appState }: { appState: AppState } = $props();

  let filePath = $state('');
  let uploadStatus = $state('');
  let uploading = $state(false);
  let firmwareList = $state<FirmwareArtifact[]>([]);
  let listLoaded = $state(false);

  async function loadList() {
    try {
      firmwareList = await api.firmwareList();
      listLoaded = true;
    } catch (e: unknown) {
      firmwareList = [];
      listLoaded = true;
    }
  }

  async function upload() {
    if (!filePath.trim()) {
      appState.showToast('Enter a firmware file path on the server', false);
      return;
    }
    uploading = true;
    uploadStatus = 'Uploading…';
    try {
      const res = await api.firmwareRegister(filePath.trim());
      if (res.success !== false) {
        appState.showToast('Firmware registered');
        uploadStatus = 'Registered successfully';
        filePath = '';
        await loadList();
      } else {
        uploadStatus = res.message || 'Upload failed';
        appState.showToast(uploadStatus, false);
      }
    } catch (e: unknown) {
      uploadStatus = String(e);
      appState.showToast(uploadStatus, false);
    }
    uploading = false;
  }

  $effect(() => {
    if (!listLoaded) loadList();
  });
</script>

<div class="card">
  <h3>Firmware</h3>
  <p class="muted">
    Register a firmware file from the server filesystem for analysis.
    Equivalent to the WPF Desktop firmware browser.
  </p>

  <div class="upload-row">
    <input
      class="input"
      type="text"
      bind:value={filePath}
      placeholder="/path/to/firmware.bin on server"
      disabled={uploading}
    />
    <button onclick={upload} type="button" class="btn btn-primary" disabled={uploading || !filePath.trim()}>
      {uploading ? 'Uploading…' : 'Register firmware'}
    </button>
  </div>
  {#if uploadStatus}
    <p class="muted small">{uploadStatus}</p>
  {/if}

  <h4 style="margin-top: 16px;">Registered firmware ({firmwareList.length})</h4>
  {#if firmwareList.length === 0}
    <p class="muted">No firmware artifacts registered yet.</p>
  {:else}
    <div class="firmware-table">
      {#each firmwareList as art (art.id)}
        <div class="firmware-row">
          <div class="fw-main">
            <strong>{art.fileName || art.filePath.split('/').pop() || art.filePath}</strong>
            <span class="sub">{art.filePath}</span>
          </div>
          <div class="sub">
            {new Date(art.analyzedAt).toLocaleString()}
            {#if art.metadata}
              {#each Object.entries(art.metadata) as [k, v]}
                <span class="chip">{k}: {v}</span>
              {/each}
            {/if}
          </div>
        </div>
      {/each}
    </div>
  {/if}
</div>

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
  .card h4 { margin: 0 0 6px; font-size: var(--fs-lg); color: var(--muted); }
  .muted { color: var(--muted); font-size: var(--fs-md); margin: 0; }
  .small { font-size: var(--fs-sm); }
  .upload-row {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    margin-top: 8px;
  }
  .upload-row .input { flex: 1; min-width: 200px; }
  .firmware-table {
    display: grid;
    gap: 6px;
    margin-top: 8px;
  }
  .firmware-row {
    border: 1px solid var(--border-faint);
    border-radius: var(--radius-sm);
    padding: 8px 10px;
    background: var(--panel-2);
    display: grid;
    gap: 4px;
    transition: border-color 0.15s;
  }
  .firmware-row:hover { border-color: var(--border); }
  .fw-main {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .fw-main strong { color: var(--text); word-break: break-word; }
  .sub { color: var(--faint); font-size: var(--fs-xs); }
  .chip {
    display: inline-block;
    background: var(--panel-3);
    border: 1px solid var(--border-faint);
    border-radius: 999px;
    padding: 1px 8px;
    font-size: var(--fs-xs);
    margin: 2px 4px 2px 0;
    color: var(--muted);
  }
</style>
