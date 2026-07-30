<script lang="ts">
  import { AppState } from '../store';
  import LiveTile from './LiveTile.svelte';

  let { appState }: { appState: AppState } = $props();

  const layouts = [1, 2, 4, 5, 6, 7, 8];

  const layoutClasses: Record<number, string> = {
    1: 'layout-1', 2: 'layout-2', 4: 'layout-4', 5: 'layout-5',
    6: 'layout-6', 7: 'layout-7', 8: 'layout-8',
  };

  function resetOrder() {
    appState.resetOrder();
    appState.showToast('View order reset');
  }
</script>

<div class="card view-toolbar">
  <div class="row gap wrap">
    <span class="muted">Layout</span>
    <div class="layout-btns">
      {#each layouts as n}
        <button
          type="button"
          class:active={appState.layout === n}
          onclick={() => appState.setLayout(n)}
        >{n}</button>
      {/each}
    </div>

    <label class="inline-check">
      Stream mode
      <select bind:value={appState.streamQuality}>
        <option value="sub">Multi-view (recommended)</option>
        <option value="rtsp">Full motion RTSP (1–2 cams)</option>
        <option value="main">HD RTSP main (heavy)</option>
      </select>
    </label>

    <label class="inline-check">
      <input type="checkbox" bind:checked={appState.liveRefreshEnabled} /> Live refresh
    </label>

    <select bind:value={appState.liveInterval}>
      <option value={1000}>1s</option>
      <option value={2000}>2s</option>
      <option value={5000}>5s</option>
      <option value={10000}>10s</option>
    </select>

    <button onclick={resetOrder} type="button">Reset order</button>
  </div>
  <p class="muted small">
    Continuous live streams (RTSP→MJPEG via ffmpeg). Drag title bar to rearrange. Click a tile to select for settings/record.
  </p>
</div>

<div class="view-grid {layoutClasses[appState.layout] || 'layout-4'}">
  {#each appState.orderedDevices.slice(0, appState.layout) as d, i (d.id)}
    <LiveTile device={d} index={i} appState={appState} />
  {:else}
    <div class="empty">Add or register a camera to start viewing.</div>
  {/each}
</div>

<style>
  .view-toolbar { margin-bottom: 12px; }
  .row { display: flex; gap: 10px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 8px; }
  .wrap { flex-wrap: wrap; }
  .muted { color: var(--muted); font-size: .9rem; }
  .small { font-size: .82rem; }
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
    overflow: hidden;
  }
  .layout-btns { display: flex; gap: 4px; flex-wrap: wrap; }
  .layout-btns button {
    min-width: 36px;
    font-weight: 700;
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
  }
  .layout-btns button:hover { border-color: #ffa33e; background: #331713; }
  .layout-btns button.active {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a;
    color: #fff;
  }
  .inline-check {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    color: var(--muted);
    font-size: .9rem;
  }
  .inline-check select, .inline-check input {
    background: #0b090bcc;
    border: 1px solid #ff5a1f55;
    border-radius: 8px;
    padding: 6px 8px;
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
  }
  button:hover { border-color: #ffa33e; background: #331713; }

  .view-grid {
    display: grid;
    gap: 10px;
    min-height: 50vh;
    align-content: start;
  }
  .view-grid.layout-1 { grid-template-columns: 1fr; }
  .view-grid.layout-2 { grid-template-columns: 1fr 1fr; }
  .view-grid.layout-4 { grid-template-columns: 1fr 1fr; }
  .view-grid.layout-5 {
    grid-template-columns: 2fr 1fr 1fr;
    grid-template-rows: 1fr 1fr 1fr;
  }
  .view-grid.layout-5 :global(.view-tile:first-child) { grid-row: span 3; }
  .view-grid.layout-6 { grid-template-columns: 1fr 1fr 1fr; }
  .view-grid.layout-7 {
    grid-template-columns: 2fr 1fr 1fr;
    grid-template-rows: 1.2fr 1fr 1fr;
  }
  .view-grid.layout-7 :global(.view-tile:first-child) { grid-row: span 2; }
  .view-grid.layout-8 { grid-template-columns: 1fr 1fr 1fr 1fr; }

  .empty {
    grid-column: 1 / -1;
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 200px;
    color: var(--muted);
    font-size: 1.1rem;
    border: 2px dashed #ff5a1f44;
    border-radius: 12px;
  }

  @media (max-width: 1000px) {
    .view-grid.layout-4,
    .view-grid.layout-6,
    .view-grid.layout-8 { grid-template-columns: 1fr 1fr; }
    .view-grid.layout-5,
    .view-grid.layout-7 { grid-template-columns: 1fr 1fr; }
    .view-grid.layout-5 :global(.view-tile:first-child),
    .view-grid.layout-7 :global(.view-tile:first-child) { grid-row: span 1; }
  }
</style>
