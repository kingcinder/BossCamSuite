<script lang="ts">
  import { AppState } from '../store.svelte';

  let { appState }: { appState: AppState } = $props();

  const tabs = [
    { id: 'viewall', label: 'Live Wall', icon: '🎥', key: 'V' },
    { id: 'overview', label: 'Device', icon: '🖥️', key: 'O' },
    { id: 'features', label: 'Features', icon: '🎛️', key: 'F' },
    { id: 'image', label: 'Image', icon: '🖼️', key: 'I' },
    { id: 'stream', label: 'Stream', icon: '📡', key: 'S' },
    { id: 'network', label: 'Network', icon: '🌐', key: 'N' },
    { id: 'record', label: 'Record', icon: '⏺️', key: 'R' },
    { id: 'highlights', label: 'Highlights', icon: '⭐', key: 'H' },
    { id: 'recovery', label: 'Recovery', icon: '🔧', key: 'X' },
    { id: 'advanced', label: 'Advanced', icon: '⚙️', key: 'A' },
  ];

  function switchTab(id: string) {
    appState.activeTab = id;
  }
</script>

<nav class="tabs" aria-label="Sections">
  {#each tabs as tab (tab.id)}
    <button
      type="button"
      class="tab"
      class:active={appState.activeTab === tab.id}
      onclick={() => switchTab(tab.id)}
      data-tip={tab.key ? `Go to ${tab.label} (${tab.key})` : `Go to ${tab.label}`}
    >
      <span class="tab-icon">{tab.icon}</span>
      <span>{tab.label}</span>
      {#if tab.key}
        <span class="kbd">{tab.key}</span>
      {/if}
    </button>
  {/each}
</nav>

<style>
  .tabs {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
    margin-bottom: 14px;
    position: sticky;
    top: 0;
    z-index: 20;
    background: linear-gradient(180deg, var(--bg) 70%, transparent);
    padding: 4px 0 8px;
    backdrop-filter: blur(8px);
  }
  .tab {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    background: transparent;
    border: 1px solid transparent;
    border-radius: 999px;
    padding: 7px 12px;
    cursor: pointer;
    color: var(--faint);
    font: inherit;
    font-size: var(--fs-sm);
    font-weight: 600;
    transition: background 0.15s ease, color 0.15s ease, border-color 0.15s ease, transform 0.06s ease;
  }
  .tab:hover {
    color: var(--text);
    background: var(--panel-3);
    border-color: var(--border-soft);
  }
  .tab:active { transform: translateY(1px); }
  .tab.active {
    color: var(--text-strong);
    border-color: var(--border);
    background: linear-gradient(180deg, #2a1813, #1c0f0b);
    box-shadow: inset 0 0 0 1px rgba(255, 106, 31, 0.15);
  }
  .tab .tab-icon { font-size: 0.95rem; line-height: 1; }
  .tab .kbd { opacity: 0.65; margin-left: 2px; }
  .tab.active .kbd { opacity: 1; color: var(--accent-strong); }
</style>
