<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import type { ControlPointInventoryReport, ControlPointInventoryItem, WriteResult } from '../types';

  let { appState }: { appState: AppState } = $props();

  let report = $state<ControlPointInventoryReport | null>(null);
  let isLoading = $state(false);
  let isProbing = $state(false);
  let statusText = $state('Select a camera and load features.');

  // Track apply in-flight per fieldKey+endpoint
  let applying = $state<Set<string>>(new Set());
  // Track toggle values separate from "current" to avoid flickering
  let toggleValues = $state<Record<string, boolean>>({});
  let sliderValues = $state<Record<string, number>>({});
  let enumValues = $state<Record<string, string>>({});

  // Expert override: per-item and global
  let showExpert = $state(false);
  let expertOverrides = $state<Record<string, boolean>>({});
  // Global force-apply: unlocks every control and applies each write with
  // expert override so settings are ACTUALLY editable even before the
  // write-verification pass has proven them. Writes are still verified by
  // the backend's semantic read-back (PersistedAfterDelay etc.).
  let forceApply = $state(false);

  // Refresh when device changes or when features tab selected
  $effect(() => {
    if (appState.selectedDeviceId && appState.activeTab === 'features') {
      loadControlPoints();
    }
  });

  // Auto-refresh control points when probe completes via SignalR
  $effect(() => {
    const ps = appState.probeStatus;
    if (ps?.complete && !ps.error && ps.deviceId === appState.selectedDeviceId) {
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
      const total = report.families.reduce((sum, f) => sum + f.controls.length, 0);
      statusText = `Control points loaded · ${total} total · ${report.ambiguousControls.length} ambiguous`;

      // Seed toggle/slider/enum values from typed settings (live camera values)
      // This avoids the "optimistic default" problem — values reflect what the camera reports.
      let liveFieldValues: Record<string, unknown> | null = null;
      try {
        const typed = await api.typedSettings(appState.selectedDeviceId);
        liveFieldValues = {};
        for (const group of typed) {
          for (const field of group.fields) {
            liveFieldValues[field.fieldKey] = field.typedValue;
          }
        }
      } catch {
        // Typed settings not available yet (no probe/read has been done) — fall back to defaults
      }

      for (const family of report.families) {
        for (const ctrl of family.controls) {
          const key = ctrlKey(ctrl);
          // Prefer live value from camera, fall back to control-point metadata
          const liveVal = liveFieldValues?.[ctrl.fieldKey];
          if (liveVal !== undefined && liveVal !== null) {
            if (typeof liveVal === 'boolean') {
              toggleValues[key] = liveVal;
            } else if (typeof liveVal === 'number') {
              sliderValues[key] = liveVal;
            } else if (typeof liveVal === 'string') {
              enumValues[key] = liveVal;
            }
          } else if (!(key in toggleValues)) {
            // No live value available — use sensible defaults
            toggleValues[key] = false;
            if (!(key in sliderValues)) sliderValues[key] = ctrl.min ?? 50;
            if (!(key in enumValues)) enumValues[key] = ctrl.allowedValues[0] || '';
          }
        }
      }
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
      await api.normalizeDevice(appState.selectedDeviceId);
      statusText = 'Normalize complete. Probing capabilities…';
      await api.probeDevice(appState.selectedDeviceId);
      statusText = 'Probe complete. Loading features…';
      await loadControlPoints();
    } catch (e: unknown) {
      statusText = 'Quick probe failed: ' + String(e);
      appState.showToast(String(e), false);
    }
    isProbing = false;
  }

  function ctrlKey(item: ControlPointInventoryItem): string {
    return `${item.fieldKey}:${item.endpoint}`;
  }

  function isApplying(item: ControlPointInventoryItem): boolean {
    return applying.has(ctrlKey(item));
  }

  function needsExpertOverride(item: ControlPointInventoryItem): boolean {
    return !item.normalUiEligible || !!item.exactBlocker;
  }

  // ── Apply helpers ─────────────────────────────────────────────

  async function applyToggle(item: ControlPointInventoryItem, checked: boolean) {
    if (!appState.selectedDeviceId) return;
    const key = ctrlKey(item);
    applying = new Set(applying).add(key);
    toggleValues[key] = checked;
    try {
      const override = forceApply || (expertOverrides[key] ?? false);
      const result = await api.applyTypedField(appState.selectedDeviceId, item.fieldKey, checked, override);
      if (result.success) {
        appState.showToast(`${item.displayName || item.fieldKey} → ${checked ? 'ON' : 'OFF'} ✓`);
      } else {
        appState.showToast(`${item.displayName || item.fieldKey}: ${result.message || 'Apply failed'}`, false);
      }
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    } finally {
      const next = new Set(applying);
      next.delete(key);
      applying = next;
      // Reload control points to reflect updated state
      loadControlPoints();
    }
  }

  async function applySlider(item: ControlPointInventoryItem, value: number) {
    if (!appState.selectedDeviceId) return;
    const key = ctrlKey(item);
    applying = new Set(applying).add(key);
    sliderValues[key] = value;
    try {
      const override = forceApply || (expertOverrides[key] ?? false);
      const result = await api.applyTypedField(appState.selectedDeviceId, item.fieldKey, value, override);
      if (result.success) {
        appState.showToast(`${item.displayName || item.fieldKey} → ${value} ✓`);
      } else {
        appState.showToast(`${item.displayName || item.fieldKey}: ${result.message || 'Apply failed'}`, false);
      }
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    } finally {
      const next = new Set(applying);
      next.delete(key);
      applying = next;
      loadControlPoints();
    }
  }

  async function applyEnum(item: ControlPointInventoryItem, value: string) {
    if (!appState.selectedDeviceId) return;
    const key = ctrlKey(item);
    applying = new Set(applying).add(key);
    enumValues[key] = value;
    try {
      const override = forceApply || (expertOverrides[key] ?? false);
      const result = await api.applyTypedField(appState.selectedDeviceId, item.fieldKey, value, override);
      if (result.success) {
        appState.showToast(`${item.displayName || item.fieldKey} → ${value} ✓`);
      } else {
        appState.showToast(`${item.displayName || item.fieldKey}: ${result.message || 'Apply failed'}`, false);
      }
    } catch (e: unknown) {
      appState.showToast(String(e), false);
    } finally {
      const next = new Set(applying);
      next.delete(key);
      applying = next;
      loadControlPoints();
    }
  }

  // ── Filtering ─────────────────────────────────────────────────

  function eligibleControls(familyControls: ControlPointInventoryItem[]): ControlPointInventoryItem[] {
    return familyControls.filter(c =>
      (c.recommendedWidget === 'Toggle' || c.recommendedWidget === 'Slider' || c.recommendedWidget === 'Dropdown') &&
      c.normalUiEligible &&
      !c.exactBlocker &&
      !c.fieldKey.endsWith('Object') &&
      c.controlType !== 'HigherOrderComposite'
    );
  }

  function expertControls(familyControls: ControlPointInventoryItem[]): ControlPointInventoryItem[] {
    return familyControls.filter(c =>
      (c.recommendedWidget === 'Toggle' || c.recommendedWidget === 'Slider' || c.recommendedWidget === 'Dropdown') &&
      (!c.normalUiEligible || !!c.exactBlocker || c.fieldKey.endsWith('Object'))
    );
  }

  function widgetIcon(item: ControlPointInventoryItem): string {
    return item.recommendedWidget === 'Toggle' ? '🔘' : item.recommendedWidget === 'Dropdown' ? '🔽' : '🎚️';
  }

  function stateLabel(item: ControlPointInventoryItem): string {
    if (item.exactBlocker) return `⛔ ${item.exactBlocker}`;
    if (!item.normalUiEligible) return '🔒 Expert only';
    if (item.readWriteState === 'Writable' || item.readWriteState === 'WritablePersistent') return '✅ Write-verified';
    if (item.readWriteState === 'ReadableOnly') return '👁️ Read-only';
    return '⚪ Unverified';
  }

  /** Returns true if the control is eligible for normal UI but not yet write-verified */
  function needsProbe(item: ControlPointInventoryItem): boolean {
    // Force-apply mode unlocks every control immediately — no probe required.
    if (forceApply) return false;
    return item.normalUiEligible
      && !item.exactBlocker
      && !item.readWriteState.startsWith('Writable')
      && !item.readWriteState.startsWith('Read');
  }

  /// True when the control is currently interactive (not disabled).
  function isControlEnabled(item: ControlPointInventoryItem): boolean {
    if (isApplying(item)) return false;
    // Force-apply unlocks every control; otherwise only write-verified ones.
    return forceApply || item.readWriteState.startsWith('Writable');
  }

  function stateClass(item: ControlPointInventoryItem): string {
    if (item.exactBlocker) return 'blocked';
    if (!item.normalUiEligible) return 'expert';
    if (item.readWriteState.startsWith('Writable')) return 'good';
    if (item.readWriteState.startsWith('Read')) return 'info';
    return 'unverified';
  }

  function isToggle(item: ControlPointInventoryItem): boolean {
    return item.recommendedWidget === 'Toggle';
  }

  function isSlider(item: ControlPointInventoryItem): boolean {
    return item.recommendedWidget === 'Slider' && item.min != null && item.max != null;
  }

  function isEnum(item: ControlPointInventoryItem): boolean {
    return item.allowedValues.length > 0;
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
    <button class="btn btn-primary" onclick={quickProbe} type="button" disabled={isProbing || isLoading || !appState.selectedDeviceId}>
      {isProbing ? '⏳ Probing…' : '🔍 Quick Probe'}
    </button>
    <button class="btn" onclick={loadControlPoints} type="button" disabled={isLoading || !appState.selectedDeviceId}>
      {isLoading ? '⏳ Loading…' : '🔄 Reload features'}
    </button>
    <label class="inline-check">
      <input type="checkbox" bind:checked={showExpert} />
      Show expert / blocked
    </label>
    <label class="inline-check force-apply" data-tip-pos="below" data-tip="Unlock every control and apply each change with expert override (bypasses write-verification gating). Changes are still checked by the camera's own read-back.">
      <input type="checkbox" bind:checked={forceApply} onchange={(e) => { if ((e.target as HTMLInputElement).checked) showExpert = true; }} />
      ⚡ Force apply (edit everything)
    </label>
  </div>

  {#if forceApply}
    <div class="force-apply-banner">
      ⚡ Force apply is ON — every control is editable and writes bypass
      verification gating. Writes are still read-back verified by the camera.
      Expert / blocked controls are also shown and unlocked.
    </div>
  {/if}

  <!-- Probe progress indicator -->
  {#if appState.probeStatus && !appState.probeStatus.complete}
    <div class="probe-progress">
      <div class="probe-bar"><div class="probe-fill"></div></div>
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
            {#each eligible as item (ctrlKey(item))}
              <div class="feature-item {stateClass(item)}" class:applying={isApplying(item)}>
                <div class="feature-header">
                  <span class="feature-icon">{widgetIcon(item)}</span>
                  <span class="feature-name">{item.displayName || item.fieldKey}</span>
                  <span class="feature-state {stateClass(item)}">{stateLabel(item)}</span>
                </div>
                <div class="feature-meta">
                  <span class="meta-chip">{item.writeShape}</span>
                  {#if item.groupedWriteRequired}
                    <span class="meta-chip warn" data-tip="Requires grouped write with related fields">Grouped write</span>
                  {/if}
                  {#if item.interFieldDependent}
                    <span class="meta-chip warn" data-tip="Value depends on other fields in this family">Dependent</span>
                  {/if}
                  {#if item.min != null && item.max != null}
                    <span class="meta-chip">{item.min}–{item.max}</span>
                  {/if}
                </div>

                <!-- Interactive control -->
                <div class="feature-control">
                  {#if needsProbe(item)}
                    <span class="probe-hint" data-tip="Run Quick Probe above to unlock this control">🔍 Probe to unlock</span>
                  {:else if isToggle(item)}
                    <label class="toggle-switch" data-tip={isApplying(item) ? 'Applying…' : `Toggle ${item.displayName || item.fieldKey}`}>
                      <input
                        type="checkbox"
                        aria-label={`Toggle ${item.displayName || item.fieldKey}`}
                        checked={toggleValues[ctrlKey(item)] ?? false}
                        disabled={!isControlEnabled(item)}
                        onchange={(e) => applyToggle(item, (e.target as HTMLInputElement).checked)}
                      />
                      <span class="toggle-slider"></span>
                    </label>
                  {:else if isSlider(item)}
                    <div class="slider-control">
                      <input
                        type="range"
                        aria-label={`${item.displayName || item.fieldKey} (${item.min}–${item.max})`}
                        min={item.min ?? 0}
                        max={item.max ?? 100}
                        value={sliderValues[ctrlKey(item)] ?? item.min ?? 50}
                        disabled={!isControlEnabled(item)}
                        onchange={(e) => applySlider(item, Number((e.target as HTMLInputElement).value))}
                        oninput={(e) => { sliderValues[ctrlKey(item)] = Number((e.target as HTMLInputElement).value); }}
                      />
                      <span class="slider-value">{sliderValues[ctrlKey(item)] ?? item.min ?? 50}</span>
                    </div>
                  {:else if isEnum(item)}
                    <div class="enum-control">
                      <select
                        aria-label={item.displayName || item.fieldKey}
                        value={enumValues[ctrlKey(item)] ?? item.allowedValues[0]}
                        disabled={!isControlEnabled(item)}
                        onchange={(e) => applyEnum(item, (e.target as HTMLSelectElement).value)}
                      >
                        {#each item.allowedValues as val}
                          <option value={val}>{val}</option>
                        {/each}
                      </select>
                    </div>
                  {:else}
                    <span class="muted small">Widget: {item.recommendedWidget} (no interactive control)</span>
                  {/if}

                  {#if isApplying(item)}
                    <span class="applying-spinner">⏳</span>
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
              {#each expert as item (ctrlKey(item))}
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

                  <!-- Expert override checkbox + apply -->
                  <div class="expert-control">
                    <label class="inline-check">
                      <input
                        type="checkbox"
                        bind:checked={expertOverrides[ctrlKey(item)]}
                      />
                      Expert override
                    </label>

                    {#if isToggle(item)}
                      <label class="toggle-switch" data-tip="Apply with expert override">
                        <input
                          type="checkbox"
                          aria-label={`Apply ${item.displayName || item.fieldKey} with expert override`}
                          checked={toggleValues[ctrlKey(item)] ?? false}
                          disabled={isApplying(item) || (!forceApply && !expertOverrides[ctrlKey(item)])}
                          onchange={(e) => applyToggle(item, (e.target as HTMLInputElement).checked)}
                        />
                        <span class="toggle-slider"></span>
                      </label>
                    {:else if isSlider(item)}
                      <div class="slider-control">
                        <input
                          type="range"
                          aria-label={`${item.displayName || item.fieldKey} (${item.min}–${item.max}) expert`}
                          min={item.min ?? 0}
                          max={item.max ?? 100}
                          value={sliderValues[ctrlKey(item)] ?? item.min ?? 50}
                          disabled={isApplying(item) || (!forceApply && !expertOverrides[ctrlKey(item)])}
                          onchange={(e) => applySlider(item, Number((e.target as HTMLInputElement).value))}
                          oninput={(e) => { sliderValues[ctrlKey(item)] = Number((e.target as HTMLInputElement).value); }}
                        />
                        <span class="slider-value">{sliderValues[ctrlKey(item)] ?? item.min ?? 50}</span>
                      </div>
                    {:else if isEnum(item)}
                      <select
                        aria-label={`${item.displayName || item.fieldKey} expert`}
                        value={enumValues[ctrlKey(item)] ?? item.allowedValues[0]}
                        disabled={isApplying(item) || (!forceApply && !expertOverrides[ctrlKey(item)])}
                        onchange={(e) => applyEnum(item, (e.target as HTMLSelectElement).value)}
                      >
                        {#each item.allowedValues as val}
                          <option value={val}>{val}</option>
                        {/each}
                      </select>
                    {:else}
                      <span class="muted small">Widget: {item.recommendedWidget}</span>
                    {/if}

                    {#if isApplying(item)}
                      <span class="applying-spinner">⏳</span>
                    {/if}
                  </div>

                  <div class="feature-detail">
                    <code>{item.endpoint}</code>
                    {#if item.exactBlocker}
                      <span class="sub"> · blocker: {item.exactBlocker}</span>
                    {/if}
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
    border: 1px solid var(--border-soft);
    border-radius: var(--radius);
    padding: 18px 20px;
    margin-bottom: 16px;
    min-width: 0;
    overflow: hidden;
    box-shadow: var(--shadow-1);
  }
  .card h3 { margin: 0 0 8px; color: var(--text-strong); }
  .muted { color: var(--muted); font-size: var(--fs-md); margin: 0; }
  .small { font-size: var(--fs-sm); }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }

  button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    background: #221618;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    padding: 8px 14px;
    cursor: pointer;
    color: var(--text);
    font: inherit;
    font-size: var(--fs-md);
    font-weight: 600;
    white-space: nowrap;
    transition: background 0.15s ease, border-color 0.15s ease, color 0.15s ease, box-shadow 0.15s ease;
  }
  button:hover:not(:disabled) { background: #33201a; border-color: var(--accent-strong); color: var(--text-strong); }
  button:disabled { opacity: 0.45; cursor: not-allowed; }
  button.btn-primary {
    background: linear-gradient(180deg, var(--accent-strong), var(--accent-deep));
    border-color: #ffb06a99;
    color: #fff8f2;
    box-shadow: 0 2px 10px var(--accent-glow);
  }
  button.btn-primary:hover:not(:disabled) {
    background: linear-gradient(180deg, #ff9a4f, #cc4416);
    border-color: #ffc68f;
    box-shadow: 0 3px 16px var(--accent-glow);
  }

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
    background: var(--panel-2);
    border: 1px solid var(--border-soft);
    border-radius: 10px;
    padding: 12px;
    margin-bottom: 12px;
  }
  .family-title {
    margin: 0 0 10px;
    font-size: var(--fs-lg);
    color: var(--text-strong);
    border-bottom: 1px solid var(--border-faint);
    padding-bottom: 6px;
  }
  .feature-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
    gap: 8px;
  }
  .feature-item {
    background: var(--bg-deep);
    border: 1px solid var(--border-faint);
    border-radius: 8px;
    padding: 10px;
    transition: border-color 0.15s, opacity 0.15s, box-shadow 0.15s;
  }
  .feature-item:hover { border-color: #ffa33e88; }
  .feature-item.applying { opacity: 0.6; pointer-events: none; }
  .feature-item.good { border-left: 3px solid #3ecf8e; }
  .feature-item.info { border-left: 3px solid #3e8ecf; }
  .feature-item.unverified { border-left: 3px solid #cf9e3e; }
  .feature-item.expert-blocked { border-left: 3px solid #cf3e3e; opacity: 0.85; }

  .feature-header {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
  }
  .feature-icon { font-size: 1rem; flex-shrink: 0; }
  .feature-name { font-weight: 600; font-size: .85rem; word-break: break-word; flex: 1; min-width: 0; }  .feature-state { font-size: .72rem; padding: 2px 6px; border-radius: 999px; white-space: nowrap; }
  .feature-state.good { background: var(--ok-dim); color: var(--ok-text); }
  .feature-state.info { background: var(--info-dim); color: var(--info-text); }
  .feature-state.unverified { background: var(--warn-dim); color: var(--warn-text); }
  .feature-state.blocked { background: var(--bad-dim); color: var(--bad-text); }
  .feature-state.expert { background: #2a1a3a; color: #cf8fff; }

  .feature-meta {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
    margin-top: 4px;
  }
  .meta-chip {
    background: #201819;
    border: 1px solid var(--border-faint);
    border-radius: 4px;
    padding: 1px 6px;
    font-size: .72rem;
    color: var(--faint);
  }
  .meta-chip.warn { color: #ddb86a; }

  /* Interactive control area */
  .feature-control {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 6px;
  }

  .expert-control {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 6px;
    flex-wrap: wrap;
  }

  /* ── Toggle switch ────────────────────────────────────── */
  .toggle-switch {
    position: relative;
    display: inline-flex;
    align-items: center;
    width: 40px;
    height: 22px;
    cursor: pointer;
  }
  .toggle-switch input {
    opacity: 0;
    width: 0;
    height: 0;
    position: absolute;
  }
  .toggle-slider {
    position: absolute;
    cursor: pointer;
    top: 0; left: 0; right: 0; bottom: 0;
    background: #3a2a2a;
    border-radius: 22px;
    transition: background 0.25s, box-shadow 0.25s;
    border: 1px solid #ff5a1f44;
  }
  .toggle-slider::before {
    content: '';
    position: absolute;
    height: 16px;
    width: 16px;
    left: 2px;
    bottom: 2px;
    background: #ffe8dd;
    border-radius: 50%;
    transition: transform 0.25s;
  }
  .toggle-switch input:checked + .toggle-slider {
    background: #3ecf8e;
    border-color: #3ecf8e;
    box-shadow: 0 0 8px #3ecf8e66;
  }
  .toggle-switch input:checked + .toggle-slider::before {
    transform: translateX(18px);
  }
  .toggle-switch input:disabled + .toggle-slider {
    opacity: 0.35;
    cursor: not-allowed;
  }

  /* ── Slider ────────────────────────────────────────────── */
  .slider-control {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
  }
  .slider-control input[type='range'] {
    flex: 1;
    height: 6px;
    -webkit-appearance: none;
    appearance: none;
    background: #3a2a2a;
    border-radius: 3px;
    outline: none;
    accent-color: #ffa33e;
    cursor: pointer;
  }
  .slider-control input[type='range']::-webkit-slider-thumb {
    -webkit-appearance: none;
    appearance: none;
    width: 16px;
    height: 16px;
    border-radius: 50%;
    background: #ffa33e;
    cursor: pointer;
    border: 2px solid #ffa33e88;
    transition: transform 0.1s;
  }
  .slider-control input[type='range']::-webkit-slider-thumb:hover {
    transform: scale(1.2);
  }
  .slider-control input[type='range']:disabled {
    opacity: 0.35;
    cursor: not-allowed;
  }
  .slider-value {
    min-width: 32px;
    text-align: center;
    font-size: .85rem;
    font-weight: 600;
    color: var(--accent);
  }

  /* ── Select / dropdown ────────────────────────────────── */
  select {
    background: #1a1010;
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 4px 8px;
    color: var(--text);
    font: inherit;
    font-size: .85rem;
    cursor: pointer;
    min-width: 80px;
  }
  select:disabled {
    opacity: 0.35;
    cursor: not-allowed;
  }

  /* ── Probe-to-unlock hint ───────────────────────────── */
  .probe-hint {
    font-size: .78rem;
    color: #cf9e3e;
    cursor: help;
    padding: 4px 8px;
    border: 1px dashed #cf9e3e44;
    border-radius: 6px;
    transition: border-color 0.15s;
  }
  .probe-hint:hover {
    border-color: #cf9e3e;
  }

  /* ── Force apply mode ──────────────────────────────── */
  .force-apply input { accent-color: var(--accent); }
  .force-apply {
    font-weight: 700;
    color: #ffb06a;
  }
  .force-apply-banner {
    background: #3a1a0a;
    border: 1px solid #ff8f3e88;
    border-radius: 8px;
    padding: 8px 12px;
    margin-bottom: 12px;
    color: #ffd2c6;
    font-size: .85rem;
  }

  .applying-spinner {
    font-size: 1rem;
    animation: spin 1s linear infinite;
  }
  @keyframes spin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
  }

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
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 8px;
    min-height: 120px;
    border: 2px dashed var(--border-faint);
    border-radius: 10px;
    padding: 24px;
    text-align: center;
    color: var(--faint);
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
