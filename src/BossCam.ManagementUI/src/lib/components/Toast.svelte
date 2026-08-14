<script lang="ts">
  import { AppState } from '../store.svelte';

  let { appState }: { appState: AppState } = $props();
</script>

{#if appState.toastVisible}
  <div class="toast" class:ok={appState.toastOk} class:bad={!appState.toastOk} role="status" aria-live="polite">
    <span class="toast-icon">{appState.toastOk ? '✓' : '⚠'}</span>
    <span class="toast-msg">{appState.toastMessage}</span>
    <button class="toast-close" onclick={() => appState.toastVisible = false} aria-label="Dismiss">✕</button>
    <span class="toast-line"></span>
  </div>
{/if}

<style>
  .toast {
    position: fixed;
    right: 18px;
    bottom: 18px;
    max-width: min(440px, calc(100vw - 32px));
    padding: 12px 14px 14px;
    border-radius: var(--radius);
    background: linear-gradient(180deg, #241512, #170d0b);
    border: 1px solid var(--border);
    box-shadow: var(--shadow-3);
    z-index: 100;
    display: flex;
    align-items: flex-start;
    gap: 10px;
    word-break: break-word;
    animation: toast-in 0.25s cubic-bezier(0.2, 0.8, 0.3, 1.1);
    overflow: hidden;
  }
  .toast.ok { border-color: #3ecf8e77; }
  .toast.bad { border-color: #ff6b6b88; }
  .toast-icon {
    font-weight: 800;
    font-size: 1rem;
    flex-shrink: 0;
    line-height: 1.4;
  }
  .toast.ok .toast-icon { color: var(--ok); }
  .toast.bad .toast-icon { color: var(--bad); }
  .toast-msg { flex: 1; min-width: 0; font-size: var(--fs-md); line-height: 1.45; }
  .toast-close {
    background: transparent;
    border: none;
    color: var(--faint);
    cursor: pointer;
    font-size: 0.8rem;
    padding: 2px 4px;
    flex-shrink: 0;
    border-radius: 4px;
    transition: color 0.15s, background 0.15s;
  }
  .toast-close:hover { color: var(--text); background: #ffffff12; }
  .toast-line {
    position: absolute;
    left: 0;
    bottom: 0;
    height: 3px;
    width: 100%;
    transform-origin: left;
    animation: toast-timer 4.5s linear forwards;
  }
  .toast.ok .toast-line { background: linear-gradient(90deg, var(--ok), transparent); }
  .toast.bad .toast-line { background: linear-gradient(90deg, var(--bad), transparent); }
  @keyframes toast-in {
    from { opacity: 0; transform: translateY(14px) scale(0.97); }
    to { opacity: 1; transform: translateY(0) scale(1); }
  }
  @keyframes toast-timer {
    from { transform: scaleX(1); }
    to { transform: scaleX(0); }
  }
</style>
