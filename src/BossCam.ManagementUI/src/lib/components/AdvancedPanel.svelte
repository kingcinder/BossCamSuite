<script lang="ts">
  import { AppState } from '../store.svelte';
  import { api } from '../api';
  import FirmwarePanel from './FirmwarePanel.svelte';
  import type { UserAccount, PersistenceVerificationResult } from '../types';

  let { appState }: { appState: AppState } = $props();

  // ── User management ───────────────────────────────────────────
  let users = $state<UserAccount[]>([]);
  let userStatus = $state('');
  let userLoading = $state(false);
  let changePwUsername = $state('');
  let changePwValue = $state('');
  let changePwStatus = $state('');

  async function loadUsers() {
    if (!appState.selectedDeviceId) {
      userStatus = 'Select a device first';
      return;
    }
    userLoading = true;
    userStatus = 'Loading users…';
    try {
      const res = await api.userList(appState.selectedDeviceId);
      if (res) {
        // The backend returns a MaintenanceResult wrapping the camera's response.
        // Try to extract user list from common response shapes.
        const body = res.body || res.response || res;
        userStatus = JSON.stringify(res).slice(0, 400);
        if (typeof res.message === 'string') {
          userStatus = res.message;
        }
        // Try to parse the response as a user list XML or JSON
        if (Array.isArray(body)) {
          users = body as unknown as UserAccount[];
        } else if (Array.isArray(res)) {
          users = res as unknown as UserAccount[];
        } else {
          users = [];
        }
      } else {
        users = [];
        userStatus = 'No user data returned';
      }
    } catch (e: unknown) {
      users = [];
      userStatus = 'Failed: ' + String(e);
    }
    userLoading = false;
  }

  async function changePassword() {
    if (!appState.selectedDeviceId || !changePwUsername.trim() || !changePwValue.trim()) {
      changePwStatus = 'Fill username and new password';
      return;
    }
    changePwStatus = 'Changing…';
    try {
      const res = await api.userChangePassword(appState.selectedDeviceId, changePwUsername.trim(), changePwValue.trim());
      changePwStatus = res.message || res.Message || 'Password changed';
      appState.showToast('Password changed for ' + changePwUsername.trim());
      changePwUsername = '';
      changePwValue = '';
      await loadUsers();
    } catch (e: unknown) {
      changePwStatus = String(e);
      appState.showToast(changePwStatus, false);
    }
  }

  // ── Persistence verification ──────────────────────────────────
  let persistenceResults = $state<PersistenceVerificationResult[]>([]);
  let persistenceStatus = $state('');
  let persistenceEndpoint = $state('');
  let persistenceLoading = $state(false);

  async function loadPersistenceResults() {
    if (!appState.selectedDeviceId) {
      persistenceStatus = 'Select a device first';
      return;
    }
    persistenceLoading = true;
    persistenceStatus = 'Loading persistence results…';
    try {
      persistenceResults = await api.persistenceResults(appState.selectedDeviceId);
      persistenceStatus = `${persistenceResults.length} result(s)`;
    } catch (e: unknown) {
      persistenceResults = [];
      persistenceStatus = 'Failed: ' + String(e);
    }
    persistenceLoading = false;
  }

  async function runPersistenceCheck() {
    if (!appState.selectedDeviceId || !persistenceEndpoint.trim()) {
      persistenceStatus = 'Select a device and enter an endpoint';
      return;
    }
    persistenceLoading = true;
    persistenceStatus = 'Running persistence check…';
    try {
      const result = await api.persistenceVerify(appState.selectedDeviceId, persistenceEndpoint.trim());
      if (result) {
        persistenceResults = [result, ...persistenceResults];
        const status = result.immediateStatus || result.persistenceStatus || result.notes || 'done';
        persistenceStatus = `Check complete: ${status}`;
        appState.showToast(`Persistence: ${status}`);
      } else {
        persistenceStatus = 'No result returned';
      }
    } catch (e: unknown) {
      persistenceStatus = String(e);
      appState.showToast(persistenceStatus, false);
    }
    persistenceLoading = false;
  }

  // ── Diagnostics ───────────────────────────────────────────────
  function currentSettingsJson(): string {
    return JSON.stringify(
      {
        image: appState.imagePayload,
        stream: appState.streamPayload,
        network: appState.netPayload,
        dirty: appState.dirtySettings,
      },
      null,
      2
    );
  }

  let diagExpanded = $state(false);
  let fwExpanded = $state(false);
  let usersExpanded = $state(false);
  let persistExpanded = $state(false);
</script>

<div class="card">
  <h3>API & Diagnostics</h3>
  <p class="muted">
    Swagger: <a href="/swagger" target="_blank" rel="noopener">/swagger</a>
  </p>
  <pre class="code">Operator UI: multi-camera live board
API: {location.origin}
UA: {navigator.userAgent}</pre>

  <button
    onclick={() => diagExpanded = !diagExpanded}
    type="button"
    class="toggle"
  >
    {diagExpanded ? '▾' : '▸'} Raw payload JSON
  </button>
  {#if diagExpanded}
    <pre class="code">{currentSettingsJson()}</pre>
  {/if}
</div>

<!-- Firmware (replaces WPF firmware browser) -->
<div class="card">
  <button
    onclick={() => fwExpanded = !fwExpanded}
    type="button"
    class="toggle"
  >
    {fwExpanded ? '▾' : '▸'} Firmware upload (WPF equivalent)
  </button>
  {#if fwExpanded}
    <FirmwarePanel {appState} />
  {/if}
</div>

<!-- User Account Management (replaces WPF user accounts panel) -->
<div class="card">
  <button
    onclick={() => usersExpanded = !usersExpanded}
    type="button"
    class="toggle"
  >
    {usersExpanded ? '▾' : '▸'} User accounts (WPF equivalent)
  </button>
  {#if usersExpanded}
    <p class="muted">Manage user accounts on the selected camera device.</p>

    <div class="row gap wrap" style="margin: 8px 0;">
      <button onclick={loadUsers} type="button" class="btn" disabled={userLoading || !appState.selectedDeviceId}>
        {userLoading ? 'Loading…' : 'Refresh users'}
      </button>
    </div>
    {#if userStatus}
      <p class="muted small">{userStatus}</p>
    {/if}

    {#if users.length > 0}
      <div class="user-table">
        {#each users as u}
          <div class="user-row">
            <strong>{u.username}</strong>
            <span class="chip">{u.role}</span>
            <span class="chip" class:active={u.enabled}>{u.enabled ? 'enabled' : 'disabled'}</span>
          </div>
        {/each}
      </div>
    {/if}

    <div class="pw-change" style="margin-top: 12px;">
      <span class="lab">Change password</span>
      <div class="row">
        <input class="input" type="text" bind:value={changePwUsername} placeholder="username" />
        <input class="input" type="password" bind:value={changePwValue} placeholder="new password" />
        <button onclick={changePassword} type="button" class="btn btn-primary" disabled={!changePwUsername.trim() || !changePwValue.trim()}>Set</button>
      </div>
      {#if changePwStatus}
        <p class="muted small">{changePwStatus}</p>
      {/if}
    </div>
  {/if}
</div>

<!-- Persistence Verification (replaces WPF persistence tests) -->
<div class="card">
  <button
    onclick={() => persistExpanded = !persistExpanded}
    type="button"
    class="toggle"
  >
    {persistExpanded ? '▾' : '▸'} Persistence verification (WPF equivalent)
  </button>
  {#if persistExpanded}
    <p class="muted">Verify that a settings change persists across a camera reboot.</p>

    <div class="row gap wrap" style="margin: 8px 0;">
      <input
        class="input"
        type="text"
        bind:value={persistenceEndpoint}
        placeholder="Field key (e.g. brightness, motionSensitivity)"
      />
      <button onclick={runPersistenceCheck} type="button" class="btn btn-primary" disabled={persistenceLoading || !appState.selectedDeviceId || !persistenceEndpoint.trim()}>
        {persistenceLoading ? 'Checking…' : 'Verify'}
      </button>
      <button onclick={loadPersistenceResults} type="button" class="btn">History</button>
    </div>
    {#if persistenceStatus}
      <p class="muted small">{persistenceStatus}</p>
    {/if}

    {#if persistenceResults.length > 0}
      <div class="persist-table">
        {#each persistenceResults as r (r.id)}
          <div class="persist-row">
            <div class="persist-main">
              <strong>{r.endpoint || r.notes}</strong>
              {#if r.persistenceStatus}
                <span class="chip" class:pass={r.persistenceStatus === 'Persists'} class:fail={r.persistenceStatus === 'Fails' || r.persistenceStatus === 'Unverified'}>
                  {r.persistenceStatus}
                </span>
              {/if}
            </div>
            <div class="sub">{new Date(r.timestamp).toLocaleString()}</div>
          </div>
        {/each}
      </div>
    {:else}
      <p class="muted">No persistence results yet. Enter a field key and click Verify.</p>
    {/if}
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
  .muted { color: var(--muted); font-size: var(--fs-md); }
  .small { font-size: var(--fs-sm); }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }
  a { color: var(--accent-strong); }
  .code {
    background: var(--bg-deep);
    border-radius: var(--radius-sm);
    padding: 10px 12px;
    overflow: auto;
    max-height: 400px;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: var(--font-mono);
    font-size: var(--fs-sm);
    border: 1px solid var(--border-cool);
    color: var(--muted);
  }
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
  button.toggle {
    width: 100%;
    text-align: left;
    justify-content: flex-start;
    background: transparent;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    font-weight: 600;
    font-size: var(--fs-lg);
    padding: 8px 12px;
    color: var(--text);
  }
  button.toggle:hover { color: var(--accent-strong); background: var(--panel-3); border-color: var(--border-faint); }
  .user-table, .persist-table {
    display: grid;
    gap: 4px;
    margin-top: 8px;
  }
  .user-row, .persist-row {
    border: 1px solid var(--border-faint);
    border-radius: var(--radius-xs);
    padding: 6px 10px;
    background: var(--panel-2);
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .persist-row { flex-direction: column; align-items: stretch; gap: 2px; }
  .persist-main { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .sub { color: var(--faint); font-size: var(--fs-xs); }
  .chip {
    display: inline-block;
    background: var(--panel-3);
    border: 1px solid var(--border-faint);
    border-radius: 999px;
    padding: 1px 8px;
    font-size: var(--fs-xs);
    color: var(--muted);
  }
  .chip.active { background: var(--ok-dim); color: var(--ok-text); }
  .chip.pass { background: var(--ok-dim); color: var(--ok-text); }
  .chip.fail { background: var(--bad-dim); color: var(--bad-text); }
  .input { min-width: 120px; flex: 1; }
  .lab { color: var(--faint); font-size: var(--fs-sm); display: block; margin-bottom: 4px; }
  .pw-change { border-top: 1px solid var(--border-cool); padding-top: 10px; }
</style>
