<script lang="ts">
  import { AppState } from '../store';

  let { appState }: { appState: AppState } = $props();

  const tabs = [
    { id: 'viewall', label: 'View All' },
    { id: 'overview', label: 'Device' },
    { id: 'features', label: 'Features' },
    { id: 'image', label: 'Image' },
    { id: 'stream', label: 'Stream' },
    { id: 'network', label: 'Network' },
    { id: 'record', label: 'Record' },
    { id: 'highlights', label: 'Highlights' },
    { id: 'advanced', label: 'Advanced' },
  ];

  function switchTab(id: string) {
    appState.activeTab = id;
  }
</script>

<nav class="tabs">
  {#each tabs as tab (tab.id)}
    <button
      type="button"
      class:active={appState.activeTab === tab.id}
      onclick={() => switchTab(tab.id)}
    >
      {tab.label}
    </button>
  {/each}
</nav>

<style>
  .tabs {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
    margin-bottom: 12px;
    position: sticky;
    top: 0;
    z-index: 5;
    background: #050506ee;
    padding: 8px 0;
    backdrop-filter: blur(6px);
  }
  .tabs button {
    background: transparent;
    border: 1px solid transparent;
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--muted);
    font: inherit;
  }
  .tabs button.active {
    color: #fff;
    border-color: var(--border);
    background: #24110d;
  }
</style>
