<script lang="ts">
  import { AppState } from '../store';
  import { api } from '../api';
  import type { ControlPointInventoryReport, ControlPointInventoryItem } from '../types';

  let { appState }: { appState: AppState } = $props();

  let report = $state<ControlPointInventoryReport | null>(null);
  let isLoading = $state(false);
  let isProbing = $state(false);
  let statusText = $state('Select a camera and load features.');
  let showExpert = $state(false);

  // Refresh when device changes or after probe/write
  $effect(() => {
    if (appState.selectedDeviceId && appState.activeTab === 'features') {
      loadControlPoints();
    }
  });

  // Auto-refresh control points when probe completes via SignalR
  $effect(() => {
    const ps = appState.probeStatus;
    if (ps?.complete && !ps.error && ps.deviceId === appState.selectedDeviceId) {
      // Brief delay to let the backend finish persistence
      const timer = setTimeout(() => loadControlPoints(), 500);
      return () => clearTimeout(timer);
    }
  });

  async function loadControlPoints() {
    if (!appState.selectedDeviceId) {
      statusText = 'Select a camera first.';
      report = null;
      return;
    }
    isLoading = true;
    statusText = 'Loading control points…';
    try {
      report = await api.controlPoints(appState.selectedDeviceId);
      statusText = `Control points loaded · ${report.families.reduce((sum, f) => sum + f.controls.length, 0)} total · ${report.ambiguousControls.length} ambiguous`;
    } catch (e: unknown) {
      report = null;
      statusText = 'Failed: ' + String(e);
    }
    isLoading = false;
  }

  async function quickProbe() {
    if (!appState.selectedDeviceId) return;
    isProbing = true;
    statusText = 'Normalizing device settings…';
    try {
      // Step 1: Normalize device (reads all typed settings from camera)
      await api.normalizeDevice(appState.selectedDeviceId);
      statusText = `Normalize complete. Probing capabilities…`;

      // Step 2: Probe capabilities
      await api.probeDevice(appState.selectedDeviceId);
      statusText = `Probe complete. Loading features…`;

      // Step 3: Load control points (auto-refresh via $effect also fires)
      await loadControlPoints();
    } catch (e: unknown) {
      statusText = 'Quick probe failed: ' + String(e);
      appState.showToast(String(e), false);
    }
    isProbing = false;
  }

  // Controls eligible for normal UI: Toggle or Slider, eligible, no blocker
  function eligibleControls(familyControls: ControlPointInventoryItem[]): ControlPointInventoryItem[] {
    return familyControls.filter(c =>
      (c.recommendedWidget === 'Toggle' || c.recommendedWidget === 'Slider') &&
      c.normalUiEligible &&
      !c.exactBlocker &&
      !c.fieldKey.endsWith('Object') &&
      c.controlType !== 'HigherOrderComposite'
    );
  }

  // Controls that are expert-only or blocked
  function expertControls(familyControls: ControlPointInventoryItem[]): ControlPointInventoryItem[] {
    return familyControls.filter(c =>
      (c.recommendedWidget === 'Toggle' || c.recommendedWidget === 'Slider') &&
      (!c.normalUiEligible || !!c.exactBlocker || c.fieldKey.endsWith('Object'))
    );
  }

  function widgetIcon(item: ControlPointInventoryItem): string {
    return item.recommendedWidget === 'Toggle' ? '🔘' : '🎚️';
  }

  function stateLabel(item: ControlPointInventoryItem): string {
    if (item.exactBlocker) return `⛔ ${item.exactBlocker}`;
    if (!item.normalUiEligible) return '🔒 Expert only';
    if (item.readWriteState === 'Writable' || item.readWriteState === 'WritablePersistent') return '✅ Write-verified';
    if (item.readWriteState === 'ReadableOnly') return '👁️ Read-only';
    return '⚪ Unverified';
  }

  function stateClass(item: ControlPointInventoryItem): string {
    if (item.exactBlocker) return 'blocked';
    if (!item.normalUiEligible) return 'expert';
    if (item.readWriteState.startsWith('Writable')) return 'good';
    if (item.readWriteState.startsWith('Read')) return 'info';
    return 'unverified';
  }
</script>

<div class="card">
  <div class="row gap wrap" style="margin-bottom: 8px;">
    <h3 style="margin: 0;">Features & Toggles</h3>
    <span class="muted small">Detected camera control points</span>
  </div>
  <p class="muted small">
    Controls discovered from the camera SDK, classified into toggles and sliders. 
    Write-verified controls have been confirmed to apply successfully.
  </p>

  <div class="row gap wrap" style="margin-bottom: 12px;">
    <button onclick={quickProbe} type="button" disabled={isProbing || isLoading || !appState.selectedDeviceId}>
      {isProbing ? 'Probing…' : '🔍 Quick Probe'}
    </button>
    <button onclick={loadControlPoints} type="button" disabled={isLoading || !appState.selectedDeviceId}>
      {isLoading ? 'Loading…' : 'Reload features'}
    </button>
    <label class="inline-check">
      <input type="checkbox" bind:checked={showExpert} />
      Show expert / blocked
    </label>
  </div>

  <!-- Probe progress indicator -->
  {#if appState.probeStatus && !appState.probeStatus.complete}
    <div class="probe-progress">
      <div class="probe-bar">
        <div class="probe-fill"></div>
      </div>
      <p class="muted small">
        Probing {appState.probeStatus.stage}… {appState.probeStatus.endpointsVerified} endpoints
        {#if appState.probeStatus.error}
          · error: {appState.probeStatus.error}
        {/if}
      </p>
    </div>
  {/if}

  {#if statusText}
    <p class="muted small">{statusText}</p>
  {/if}

  {#if report}
    {#each report.families as family (family.family)}
      {@const eligible = eligibleControls(family.controls)}
      {@const expert = expertControls(family.controls)}
      {#if eligible.length > 0 || (showExpert && expert.length > 0)}
        <div class="family-card">
          <h4 class="family-title">{family.family}</h4>
          <div class="feature-grid">
            {#each eligible as item (item.fieldKey + item.endpoint)}
              <div class="feature-item" class:good={stateClass(item) === 'good'} class:info={stateClass(item) === 'info'} class:unverified={stateClass(item) === 'unverified'}>
                <div class="feature-header">
                  <span class="feature-icon">{widgetIcon(item)}</span>
                  <span class="feature-name">{item.displayName || item.fieldKey}</span>
                  <span class="feature-state {stateClass(item)}">{stateLabel(item)}</span>
                </div>
                <div class="feature-meta">
                  <span class="meta-chip">{item.writeShape}</span>
                  {#if item.groupedWriteRequired}
                    <span class="meta-chip warn">Grouped write</span>
                  {/if}
                  {#if item.interFieldDependent}
                    <span class="meta-chip warn">Dependent</span>
                  {/if}
                  {#if item.min != null && item.max != null}
                    <span class="meta-chip">{item.min}–{item.max}</span>
                  {/if}
                </div>
                <div class="feature-detail">
                  <code>{item.endpoint}</code>
                  {#if item.allowedValues.length > 0}
                    <span class="sub"> · {item.allowedValues.join(', ')}</span>
                  {/if}
                </div>
              </div>
            {/each}
            {#if showExpert}
              {#each expert as item (item.fieldKey + item.endpoint)}
                <div class="feature-item expert-blocked">
                  <div class="feature-header">
                    <span class="feature-icon">{widgetIcon(item)}</span>
                    <span class="feature-name">{item.displayName || item.fieldKey}</span>
                    <span class="feature-state blocked">{stateLabel(item)}</span>
                  </div>
                  <div class="feature-meta">
                    {#if item.min != null && item.max != null}
                      <span class="meta-chip">{item.min}–{item.max}</span>
                    {/if}
                    {#if item.allowedValues.length > 0}
                      <span class="meta-chip">{item.allowedValues.length} values</span>
                    {/if}
                  </div>
                  <div class="feature-detail">
                    <code>{item.endpoint}</code>
                  </div>
                </div>
              {/each}
            {/if}
          </div>
        </div>
      {/if}
    {/each}

    {#if report.ambiguousControls.length > 0}
      <div class="family-card">
        <h4 class="family-title">Ambiguous / Unclassified</h4>
        <p class="muted small">{report.ambiguousControls.length} controls without a clear type or blocker.</p>
      </div>
    {/if}
  {:else if !isLoading}
    <div class="empty-state">
      <p class="muted">No control point data. Select a camera and click Reload features.</p>
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
  .muted { color: var(--muted); font-size: .9rem; margin: 0; }
  .small { font-size: .82rem; }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }
  
  button {
    background: #1a1010cc;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
  }
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  
  .inline-check {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    color: var(--muted);
    font-size: .9rem;
    cursor: pointer;
  }
  .inline-check input { accent-color: var(--accent); }

  .family-card {
    background: #0e0a0b;
    border: 1px solid #ff5a1f33;
    border-radius: 10px;
    padding: 12px;
    margin-bottom: 12px;
  }
  .family-title {
    margin: 0 0 10px;
    font-size: .95rem;
    color: #ffe8dd;
    border-bottom: 1px solid #ff5a1f22;
    padding-bottom: 6px;
  }
  .feature-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
    gap: 8px;
  }
  .feature-item {
    background: #0a0809;
    border: 1px solid #ff5a1f33;
    border-radius: 8px;
    padding: 10px;
    transition: border-color 0.15s;
  }
  .feature-item:hover { border-color: #ffa33e88; }
  .feature-item.good { border-left: 3px solid #3ecf8e; }
  .feature-item.info { border-left: 3px solid #3e8ecf; }
  .feature-item.unverified { border-left: 3px solid #cf9e3e; }
  .feature-item.expert-blocked { border-left: 3px solid #cf3e3e; opacity: 0.75; }
  
  .feature-header {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
  }
  .feature-icon { font-size: 1rem; flex-shrink: 0; }
  .feature-name { font-weight: 600; font-size: .85rem; word-break: break-word; flex: 1; min-width: 0; }
  .feature-state {
    font-size: .72rem;
    padding: 2px 6px;
    border-radius: 4px;
    white-space: nowrap;
  }
  .feature-state.good { background: #1a3a1a; color: #8fdd8f; }
  .feature-state.info { background: #1a2a3a; color: #8fbfff; }
  .feature-state.unverified { background: #3a2a1a; color: #ddcf8f; }
  .feature-state.blocked { background: #3a1a1a; color: #ff8f8f; }

  .feature-meta {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
    margin-top: 4px;
  }
  .meta-chip {
    background: #1a1010;
    border-radius: 4px;
    padding: 1px 6px;
    font-size: .72rem;
    color: var(--muted);
  }
  .meta-chip.warn { color: #ddb86a; }

  .feature-detail {
    margin-top: 4px;
    font-size: .72rem;
    color: var(--muted);
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .feature-detail code { font-size: .72rem; word-break: break-all; }
  .feature-detail .sub { color: var(--muted); }

  .empty-state {
    display: grid;
    place-items: center;
    min-height: 120px;
    border: 2px dashed #ff5a1f44;
    border-radius: 10px;
    padding: 24px;
  }

  code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  }

  .probe-progress {
    background: #0e0a0b;
    border: 1px solid #3e8ecf55;
    border-radius: 8px;
    padding: 8px 10px;
    margin-bottom: 12px;
  }
  .probe-bar {
    height: 4px;
    background: #1a2a3a;
    border-radius: 2px;
    overflow: hidden;
    margin-bottom: 4px;
  }
  .probe-fill {
    height: 100%;
    width: 100%;
    background: linear-gradient(90deg, #3e8ecf, #8fbfff, #3e8ecf);
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
    border-radius: 2px;
  }
  @keyframes shimmer {
    0% { background-position: -200% 0; }
    100% { background-position: 200% 0; }
  }
</style>
