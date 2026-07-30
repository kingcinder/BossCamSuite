<script lang="ts">
  import { AppState } from '../store';

  let { state }: { state: AppState } = $props();

  function currentSettingsJson(): string {
    return JSON.stringify(
      {
        image: state.imagePayload,
        stream: state.streamPayload,
        network: state.netPayload,
        dirty: state.dirtySettings,
      },
      null,
      2
    );
  }
</script>

<div class="card">
  <h3>Raw NetSDK / typed payload</h3>
  <pre class="code">{currentSettingsJson()}</pre>
</div>

<div class="card">
  <h3>API</h3>
  <p class="muted">
    Swagger: <a href="/swagger" target="_blank" rel="noopener">/swagger</a>
  </p>
  <pre class="code">Operator UI: multi-camera live board
API: {location.origin}
UA: {navigator.userAgent}</pre>
</div>

<style>
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px;
    margin-bottom: 14px;
  }
  .card h3 { margin: 0 0 10px; }
  .muted { color: var(--muted); font-size: .9rem; }
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
</style>
