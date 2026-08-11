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
      type="text"
      bind:value={filePath}
      placeholder="/path/to/firmware.bin on server"
      disabled={uploading}
    />
    <button onclick={upload} type="button" class="accent" disabled={uploading || !filePath.trim()}>
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
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
    overflow: hidden;
  }
  .card h3 { margin: 0 0 10px; }
  .card h4 { margin: 0 0 6px; font-size: .95rem; color: var(--muted); }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
  .upload-row {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    margin-top: 8px;
  }
  .upload-row input[type="text"] {
    flex: 1;
    min-width: 200px;
    background: #0b090bcc;
    border: 1px solid #ff5a1f55;
    border-radius: 8px;
    padding: 8px;
    color: var(--text);
    font: inherit;
  }
  button {
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
    white-space: nowrap;
  }
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
  .firmware-table {
    display: grid;
    gap: 6px;
    margin-top: 8px;
  }
  .firmware-row {
    border: 1px solid #ff5a1f33;
    border-radius: 8px;
    padding: 8px 10px;
    background: #0a0809;
    display: grid;
    gap: 4px;
  }
  .fw-main {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .sub { color: var(--muted); font-size: .82rem; }
  .chip {
    display: inline-block;
    background: #2a150f;
    border-radius: 4px;
    padding: 2px 6px;
    font-size: .78rem;
    margin: 2px 4px 2px 0;
  }
</style>
