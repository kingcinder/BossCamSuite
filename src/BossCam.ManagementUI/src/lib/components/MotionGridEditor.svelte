<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';

  let { appState }: { appState: AppState } = $props();

  // Motion detection data loaded from the camera
  let gridData = $state<{ gridWidth: number; gridHeight: number; gridCells: number[] } | null>(null);
  let isDirty = $state(false);
  let isLoading = $state(false);
  let statusText = $state('');
  let brushMode = $state<'paint' | 'erase'>('paint');

  const GRID_COLS = 32;
  const GRID_ROWS = 24;

  // Toggle cell state
  function toggleCell(row: number, col: number) {
    if (!gridData) return;
    const idx = row * GRID_COLS + col;
    if (idx < 0 || idx >= gridData.gridCells.length) return;
    const newVal = brushMode === 'paint' ? 1 : 0;
    if (gridData.gridCells[idx] === newVal) return;
    // Create new array reference so $state reactivity fires in Svelte 5
    const newCells = [...gridData.gridCells];
    newCells[idx] = newVal;
    gridData.gridCells = newCells;
    isDirty = true;
  }

  // Mouse drag painting
  let isPainting = $state(false);
  function onCellMouseDown(row: number, col: number) {
    isPainting = true;
    toggleCell(row, col);
  }
  function onCellMouseEnter(row: number, col: number) {
    if (isPainting) toggleCell(row, col);
  }
  function onCellMouseUp() {
    isPainting = false;
  }

  // Batch operations
  function fillAll() {
    if (!gridData) return;
    gridData.gridCells = gridData.gridCells.map(() => 1);
    isDirty = true;
  }
  function clearAll() {
    if (!gridData) return;
    gridData.gridCells = gridData.gridCells.map(() => 0);
    isDirty = true;
  }
  function invertAll() {
    if (!gridData) return;
    gridData.gridCells = gridData.gridCells.map(v => v === 0 ? 1 : 0);
    isDirty = true;
  }

  // Load from camera
  async function loadGrid() {
    if (!appState.selectedDeviceId) {
      statusText = 'Select a device first';
      return;
    }
    isLoading = true;
    statusText = 'Loading motion grid…';
    isDirty = false;
    try {
      const raw = await api.motionGridGet(appState.selectedDeviceId);
      if (!raw || typeof raw !== 'object') {
        statusText = 'Motion grid not available on this device/firmware.';
        gridData = null;
        return;
      }
      const obj = raw as Record<string, unknown>;
      // Try to find grid cells from various possible response shapes
      const dg = (obj.detectionGrid ?? obj.grid ?? obj.DetectionGrid ?? obj) as Record<string, unknown>;
      const cells = dg.gridCells as number[] ?? dg.cells as number[] ?? dg.points as number[] ?? obj.gridCells as number[] ?? [];
      const w = (dg.gridWidth as number) ?? (dg.width as number) ?? 32;
      const h = (dg.gridHeight as number) ?? (dg.height as number) ?? 24;
      if (cells.length > 0) {
        // Pad or truncate to full grid size
        const expected = GRID_COLS * GRID_ROWS;
        const padded = new Array(expected).fill(0);
        for (let i = 0; i < Math.min(cells.length, expected); i++) {
          padded[i] = cells[i] ? 1 : 0;
        }
        gridData = { gridWidth: GRID_COLS, gridHeight: GRID_ROWS, gridCells: padded };
        statusText = `Loaded motion grid (${w}x${h}, ${cells.length} cells)`;
      } else {
        // No grid found — create empty one
        gridData = { gridWidth: GRID_COLS, gridHeight: GRID_ROWS, gridCells: new Array(GRID_COLS * GRID_ROWS).fill(0) };
        statusText = 'Empty grid — draw motion detection regions';
      }
    } catch (e: unknown) {
      statusText = 'Load failed: ' + String(e);
      gridData = null;
    }
    isLoading = false;
  }

  // Save to camera
  async function saveGrid() {
    if (!appState.selectedDeviceId || !gridData) return;
    isLoading = true;
    statusText = 'Saving motion grid…';
    try {
      // Build the detectionGrid payload matching the camera's expected shape
      // Clone gridCells to avoid race if user continues editing during save
      const payload = {
        detectionGrid: {
          gridWidth: gridData.gridWidth,
          gridHeight: gridData.gridHeight,
          gridCells: [...gridData.gridCells],
        }
      };
      await api.motionGridPut(appState.selectedDeviceId, payload);
      isDirty = false;
      statusText = 'Motion grid saved';
      appState.showToast('Motion grid saved');
    } catch (e: unknown) {
      statusText = 'Save failed: ' + String(e);
      appState.showToast(String(e), false);
    }
    isLoading = false;
  }

  // Summary text
  let summary = $derived.by(() => {
    if (!gridData) return '';
    const active = gridData.gridCells.filter(v => v === 1).length;
    const total = gridData.gridCells.length;
    const pct = ((active / total) * 100).toFixed(1);
    return `${active} / ${total} cells active (${pct}%)`;
  });

  // Load on setup
  $effect(() => {
    if (!gridData && !isLoading && appState.selectedDeviceId) {
      loadGrid();
    }
  });
</script>

<div class="motion-grid-card">
  <div class="row gap wrap" style="margin-bottom: 8px;">
    <h3 style="margin: 0;">Motion detection grid</h3>
    {#if summary}
      <span class="muted small">{summary}</span>
    {/if}
  </div>

  <p class="muted small">Interactive 32×24 motion detection grid (WPF equivalent). Click/toggle cells to define detection regions. Hold and drag to paint.</p>

  <div class="toolbar row gap wrap">
    <button onclick={loadGrid} type="button" disabled={isLoading}>Reload</button>
    <button onclick={fillAll} type="button" disabled={!gridData || isLoading}>Fill all</button>
    <button onclick={clearAll} type="button" disabled={!gridData || isLoading}>Clear all</button>
    <button onclick={invertAll} type="button" disabled={!gridData || isLoading}>Invert</button>

    <label class="brush-toggle">
      <input type="checkbox" checked={brushMode === 'paint'} onchange={() => brushMode = brushMode === 'paint' ? 'erase' : 'paint'} />
      <span class="chip">{brushMode === 'paint' ? '🖌️ Paint' : '🧹 Erase'}</span>
    </label>

    <button onclick={saveGrid} type="button" class="accent" disabled={!isDirty || isLoading}>
      {isLoading ? 'Saving…' : 'Save grid'}
    </button>
  </div>

  {#if statusText}
    <p class="muted small">{statusText}</p>
  {/if}

  {#if gridData}
    <!-- svelte-ignore a11y_no_noninteractive_element_interactions -- the wrapper captures drag-release outside cells. -->
    <div class="grid-wrap" role="application" aria-label="Motion detection grid" onmouseup={onCellMouseUp} onmouseleave={onCellMouseUp}>
      <div class="grid" style="grid-template-columns: repeat({GRID_COLS}, 1fr);">
        {#each gridData.gridCells as cell, i (i)}
          {@const row = Math.floor(i / GRID_COLS)}
          {@const col = i % GRID_COLS}
          <div
            class="cell"
            class:active={cell === 1}
            role="button"
            tabindex="0"
            onmousedown={() => onCellMouseDown(row, col)}
            onmouseenter={() => onCellMouseEnter(row, col)}
            onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') toggleCell(row, col); }}
          ></div>
        {/each}
      </div>
    </div>
  {:else if !isLoading}
    <p class="muted">No grid data. Select a device and click Reload.</p>
  {:else}
    <p class="muted">Loading grid…</p>
  {/if}
</div>

<style>
  .motion-grid-card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
    overflow: hidden;
  }
  .motion-grid-card h3 { margin: 0 0 10px; }
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }

  .toolbar {
    margin-bottom: 10px;
    background: #0b090b99;
    border-radius: 8px;
    padding: 8px;
  }
  button {
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 6px 10px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
    font-size: .85rem;
  }
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
  .brush-toggle {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    cursor: pointer;
    font-size: .85rem;
  }
  .chip {
    background: #2a150f;
    border-radius: 4px;
    padding: 2px 8px;
    font-size: .82rem;
  }

  .grid-wrap {
    overflow: auto;
    max-width: 100%;
    max-height: 520px;
    border: 1px solid #ff5a1f33;
    border-radius: 8px;
    background: #050506;
    user-select: none;
  }
  .grid {
    display: grid;
    gap: 1px;
    padding: 4px;
    min-width: max-content;
  }
  .cell {
    width: 20px;
    height: 14px;
    border-radius: 2px;
    background: #1a1010;
    border: 1px solid transparent;
    cursor: pointer;
    transition: background 50ms, border-color 150ms;
  }
  .cell:hover {
    border-color: #ffa33e88;
    background: #2a1a14;
  }
  .cell.active {
    background: #ff6a1f;
    border-color: #ffb06a66;
    box-shadow: 0 0 4px #ff6a1f44;
  }
  .cell.active:hover {
    background: #ff8938;
  }
</style>
