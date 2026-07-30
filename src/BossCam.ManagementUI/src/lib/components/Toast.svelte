<script lang="ts">
  import { AppState } from '../store';

  let { appState }: { appState: AppState } = $props();
</script>

{#if appState.toastVisible}
  <div class="toast" class:ok={appState.toastOk} class:bad={!appState.toastOk}>
    {appState.toastMessage}
  </div>
{/if}

<style>
  .toast {
    position: fixed;
    right: 18px;
    bottom: 18px;
    max-width: min(420px, calc(100vw - 32px));
    padding: 12px 14px;
    border-radius: 10px;
    background: #1d120f;
    border: 1px solid var(--border);
    box-shadow: 0 10px 40px #000a;
    z-index: 50;
    word-break: break-word;
    animation: toast-in 0.25s ease-out;
  }
  .toast.ok { border-color: #3ecf8e88; }
  .toast.bad { border-color: #ff6b6b88; }
  @keyframes toast-in {
    from { opacity: 0; transform: translateY(12px); }
    to { opacity: 1; transform: translateY(0); }
  }
</style>
