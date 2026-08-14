#!/usr/bin/env bash
# BossCamSuite — one-click rollback (desktop icon: "BOSSCAMSUITE ROLLBACK").
#
# Swaps the current installed build into the rollback slot and restores the
# previously-saved build (created by the last UPDATE). Symmetric with the
# updater: after a rollback the newest build is kept as the rollback slot,
# so you can flip back and forth between the two most recent builds.
#
# Environment overrides:
#   BOSSCAM_SERVICE_PREFIX   service install dir    (/opt/bosscam)
#   BOSSCAM_GUI_PREFIX       GUI install dir        (/opt/bosscam-gui)
#   BOSSCAM_ROLLBACK_DIR     rollback slot dir      (~/.local/share/BossCamSuite/rollback)
#   BOSSCAM_API              local service base URL (http://127.0.0.1:5317)
#   BOSSCAM_NO_RESTART=1     swap files only — do not touch service or GUI
set -euo pipefail

say() { printf '%s\n' "$*"; }
die() {
  say "ERROR: $*"
  command -v notify-send >/dev/null 2>&1 && \
    notify-send -u critical "BossCamSuite rollback FAILED" "$*" >/dev/null 2>&1 || true
  [[ -t 0 ]] && read -r -p "Press Enter to close this window…" || true
  exit 1
}

# ── Single-instance guard ───────────────────────────────────────────
# flock on this script's fd 9. Long-lived children (the nohup'd service and relaunched
# GUI) MUST NOT inherit fd 9: a leaked fd pins the flock forever, making every later
# update/rollback claim "already running" while nothing is running. The nohup spawns
# below close fd 9 with `9>&-`; the EXIT trap releases the lock + pid marker.
LOCK_FILE="${BOSSCAM_LOCK_FILE:-/tmp/bosscam.lock}"
LOCK_PID_FILE="${LOCK_FILE}.pid"
# True when pid is live. /proc works across owners: kill -0 on another user's process
# returns EPERM, which would misread a live updater as a stale lock.
pid_alive() { [[ -d "/proc/$1" ]] 2>/dev/null; }
if command -v flock >/dev/null 2>&1; then
  exec 9>"$LOCK_FILE"
  if ! flock -n 9 2>/dev/null; then
    # Contended. Wait up to 2s for a genuine concurrent updater: it writes its pid
    # marker within microseconds of taking the flock, and if it dies the flock is
    # released and we simply acquire it. Only when the wait still leaves us locked
    # out with no live holder do we treat the lock as stale (a crashed run, or a
    # lock pinned by a long-lived child spawned before the fd-leak fix). This wait
    # also closes the race where a competitor reclaims during the winner's
    # flock->pid-write gap and both runs proceed concurrently.
    if ! flock -w 2 9 2>/dev/null; then
      holder="$(cat "$LOCK_PID_FILE" 2>/dev/null || true)"
      if [[ -n "$holder" ]] && pid_alive "$holder"; then
        die "Another BossCamSuite update/rollback is already running (pid $holder) — wait for it to finish."
      fi
      say "· Stale update lock detected ($LOCK_FILE) — reclaiming it."
      rm -f "$LOCK_FILE" "$LOCK_PID_FILE"
      exec 9>"$LOCK_FILE"
      flock -n 9 2>/dev/null \
        || die "Could not acquire the update lock. Remove $LOCK_FILE and retry."
    fi
  fi
  printf '%s\n' "$$" > "$LOCK_PID_FILE"
  trap 'rm -f "$LOCK_PID_FILE"; exec 9>&- 2>/dev/null || true' EXIT
fi

GUI_PREFIX="${BOSSCAM_GUI_PREFIX:-/opt/bosscam-gui}"
SERVICE_PREFIX="${BOSSCAM_SERVICE_PREFIX:-/opt/bosscam}"
ROLLBACK_DIR="${BOSSCAM_ROLLBACK_DIR:-$HOME/.local/share/BossCamSuite/rollback}"
PREV_GUI="$ROLLBACK_DIR/gui"
PREV_SVC="$ROLLBACK_DIR/svc"
API="${BOSSCAM_API:-http://127.0.0.1:5317}"

say "=== BossCamSuite rollback ==="
if [[ ! -d "$PREV_GUI" && ! -d "$PREV_SVC" ]]; then
  die "No previous build found. Run the BOSSCAMSUITE UPDATE shortcut first."
fi

# .NET resolution (only needed when restarting a manually-spawned service)
if [[ -z "${DOTNET_ROOT:-}" && -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
elif [[ -z "${DOTNET_ROOT:-}" && -x "$HOME/.local/share/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.local/share/dotnet"
fi

# Stop the running service + GUI before swapping files (skipped in test mode).
if [[ "${BOSSCAM_NO_RESTART:-0}" != "1" ]]; then
  if systemctl list-unit-files 2>/dev/null | grep -q '^bosscam.service'; then
    timeout 20 systemctl stop bosscam.service 2>/dev/null || true
  fi
  pkill -f 'BossCam.Service.dll' 2>/dev/null || true
  pkill -f "$GUI_PREFIX/BossCam.Desktop.Avalonia" 2>/dev/null || true
  sleep 1
fi

# Keep the current build as the new rollback slot (symmetric flip).
# The saved build is moved to /tmp FIRST so it can never be clobbered.
TMP_GUI="/tmp/bosscam-rollback-gui.$$"
TMP_SVC="/tmp/bosscam-rollback-svc.$$"
if [[ -d "$PREV_GUI" ]]; then
  mv "$PREV_GUI" "$TMP_GUI" || die "Could not stage the previous GUI build for rollback."
fi
if [[ -d "$PREV_SVC" ]]; then
  mv "$PREV_SVC" "$TMP_SVC" || die "Could not stage the previous service build for rollback."
fi

mkdir -p "$ROLLBACK_DIR"
[[ -d "$GUI_PREFIX" ]] && { rm -rf "$PREV_GUI"; cp -a "$GUI_PREFIX" "$PREV_GUI" || die "Could not snapshot current GUI into $PREV_GUI."; }
[[ -d "$SERVICE_PREFIX" ]] && { rm -rf "$PREV_SVC"; cp -a "$SERVICE_PREFIX" "$PREV_SVC" || die "Could not snapshot current service into $PREV_SVC."; }

# Restore: rsync INTO the existing install dir (which is user-owned) rather
# than building a sibling staging dir — /opt itself is root-owned, so a sibling
# like $GUI_PREFIX.new cannot be created without sudo. rsync --delete makes the
# target exactly match the saved build; the install dir is never left missing
# and a failed copy cannot destroy it.
# Sanity-check BOTH saved builds BEFORE touching either live install, so a
# corrupt saved build always aborts with both live installs untouched.
if [[ -d "$TMP_GUI" ]]; then
  [[ -x "$TMP_GUI/BossCam.Desktop.Avalonia" ]] \
    || die "Saved GUI build is missing its executable — live installs left untouched. Re-run UPDATE to refresh the rollback slot."
fi
if [[ -d "$TMP_SVC" ]]; then
  [[ -f "$TMP_SVC/BossCam.Service.dll" ]] \
    || die "Saved service build is missing BossCam.Service.dll — live installs left untouched. Re-run UPDATE to refresh the rollback slot."
fi

if [[ -d "$TMP_GUI" ]]; then
  rsync -a --delete "$TMP_GUI/" "$GUI_PREFIX/" || die "Could not restore previous GUI into $GUI_PREFIX."
  chmod +x "$GUI_PREFIX/BossCam.Desktop.Avalonia" 2>/dev/null || true
  # Ensure the launcher exists after restore (a saved build may predate it).
  if [[ ! -x "$GUI_PREFIX/launch-bosscam.sh" ]]; then
    printf '%s\n' '#!/usr/bin/env bash' 'exec "'"$GUI_PREFIX"'/BossCam.Desktop.Avalonia" "$@"' \
      > "$GUI_PREFIX/launch-bosscam.sh"
    chmod +x "$GUI_PREFIX/launch-bosscam.sh"
  fi
  rm -rf "$TMP_GUI"
  say "  ✓ Restored previous GUI."
fi
if [[ -d "$TMP_SVC" ]]; then
  rsync -a --delete "$TMP_SVC/" "$SERVICE_PREFIX/" || die "Could not restore previous service into $SERVICE_PREFIX."
  chmod +x "$SERVICE_PREFIX/BossCam.Service" 2>/dev/null || true
  rm -rf "$TMP_SVC"
  say "  ✓ Restored previous service."
fi

if [[ "${BOSSCAM_NO_RESTART:-0}" == "1" ]]; then
  say "  BOSSCAM_NO_RESTART=1 — service/GUI left stopped."
  exit 0
fi

# Start the service again and wait for health.
if systemctl list-unit-files 2>/dev/null | grep -q '^bosscam.service'; then
  timeout 30 systemctl start bosscam.service 2>/dev/null || true
else
  nohup "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" "$SERVICE_PREFIX/BossCam.Service.dll" \
    >>"$HOME/.local/share/BossCamSuite/service.log" 2>&1 9>&- &
fi
say "  Waiting for health on $API …"
healthy=0
for _ in $(seq 1 60); do
  if curl -sf -m 2 "$API/api/health" >/dev/null 2>&1; then healthy=1; break; fi
  sleep 1
done
[[ "$healthy" == "1" ]] || die "Service did not become healthy at $API."

if [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]]; then
  nohup "$GUI_PREFIX/launch-bosscam.sh" >/tmp/bosscam-gui-launch.log 2>&1 9>&- &
  say "  ✓ GUI relaunched."
fi

say "=== Rollback complete. Run UPDATE again to go back to the newest build. ==="
command -v notify-send >/dev/null 2>&1 && \
  notify-send -u normal "BossCamSuite rolled back" "Previous build restored. Run UPDATE to return to the newest build." >/dev/null 2>&1 || true
[[ -t 0 ]] && read -r -p "Press Enter to close this window…" || true
