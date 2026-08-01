<script lang="ts">
  import { AppState } from '../store';
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
      <button onclick={loadUsers} type="button" disabled={userLoading || !appState.selectedDeviceId}>
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
        <input type="text" bind:value={changePwUsername} placeholder="username" />
        <input type="password" bind:value={changePwValue} placeholder="new password" />
        <button onclick={changePassword} type="button" class="accent" disabled={!changePwUsername.trim() || !changePwValue.trim()}>Set</button>
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

    <div class="row gap wrap" style="margin: 8px 0;">        <input
        type="text"
        bind:value={persistenceEndpoint}
        placeholder="Field key (e.g. brightness, motionSensitivity)"
      />
      <button onclick={runPersistenceCheck} type="button" class="accent" disabled={persistenceLoading || !appState.selectedDeviceId || !persistenceEndpoint.trim()}>
        {persistenceLoading ? 'Checking…' : 'Verify'}
      </button>
      <button onclick={loadPersistenceResults} type="button">History</button>
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
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
    min-width: 0;
    overflow: hidden;
  }
  .card h3 { margin: 0 0 10px; }
  .muted { color: var(--muted); font-size: .9rem; }
  .small { font-size: .82rem; }
  .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  .gap { gap: 10px; }
  .wrap { flex-wrap: wrap; }
  a { color: var(--accent); }
  .code {
    background: #0b0d10;
    border-radius: 8px;
    padding: 10px;
    overflow: auto;
    max-height: 400px;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    font-size: .82rem;
    border: 1px solid #ffffff12;
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
  button:hover:not(:disabled) { border-color: #ffa33e; background: #331713; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  button.accent {
    background: linear-gradient(180deg, #ff7a2f, #b83a12);
    border-color: #ffb06a; color: #fff8f2; font-weight: 600;
  }
  button.toggle {
    width: 100%;
    text-align: left;
    background: transparent;
    border: none;
    font-weight: 600;
    font-size: .95rem;
    padding: 4px 0;
  }
  button.toggle:hover { color: #ffb06a; background: transparent; }
  .user-table, .persist-table {
    display: grid;
    gap: 4px;
    margin-top: 8px;
  }
  .user-row, .persist-row {
    border: 1px solid #ff5a1f33;
    border-radius: 6px;
    padding: 6px 10px;
    background: #0a0809;
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .persist-row { flex-direction: column; align-items: stretch; gap: 2px; }
  .persist-main { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .sub { color: var(--muted); font-size: .82rem; }
  .chip {
    display: inline-block;
    background: #2a150f;
    border-radius: 4px;
    padding: 2px 6px;
    font-size: .78rem;
  }
  .chip.active { background: #1a3a1a; color: #8fdd8f; }
  .chip.pass { background: #1a3a1a; color: #8fdd8f; }
  .chip.fail { background: #3a1a1a; color: #dd8f8f; }
  input[type="text"], input[type="password"] {
    background: #0b090bcc;
    border: 1px solid #ff5a1f55;
    border-radius: 8px;
    padding: 8px;
    color: var(--text);
    font: inherit;
    min-width: 120px;
    flex: 1;
  }
  .lab { color: var(--muted); font-size: .85rem; display: block; margin-bottom: 4px; }
  .pw-change { border-top: 1px solid #ffffff12; padding-top: 10px; }
</style>
