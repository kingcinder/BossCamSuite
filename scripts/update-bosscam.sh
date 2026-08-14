#!/usr/bin/env bash
# BossCamSuite — one-click updater (desktop icon: "BOSSCAMSUITE UPDATE").
#
# Rebuilds the installed BossCamService + desktop GUI from the repository
# (optionally pulling the latest pushed work first), restarts the service,
# relaunches the GUI, and keeps the previous build in a rollback slot so the
# "BOSSCAMSUITE ROLLBACK" icon can restore it.
#
# Rollback slots live in the user's own data dir (~/.local/share/BossCamSuite/
# rollback) because /opt is root-owned — cody-owned directories cannot be
# created there, and the updater must work without sudo.
#
# Environment overrides:
#   BOSSCAM_REPO             repo path              (auto-located by default)
#   BOSSCAM_SERVICE_PREFIX   service install dir    (/opt/bosscam)
#   BOSSCAM_GUI_PREFIX       GUI install dir        (/opt/bosscam-gui)
#   BOSSCAM_ROLLBACK_DIR     rollback slot dir      (~/.local/share/BossCamSuite/rollback)
#   BOSSCAM_API              local service base URL (http://127.0.0.1:5317)
#   BOSSCAM_UPDATE_PULL=0    skip `git pull --ff-only`
#   BOSSCAM_NO_RESTART=1     publish/install only — do not touch service or GUI
set -euo pipefail

say() { printf '%s\n' "$*"; }
die() {
  say "ERROR: $*"
  command -v notify-send >/dev/null 2>&1 && \
    notify-send -u critical "BossCamSuite update FAILED" "$*" >/dev/null 2>&1 || true
  [[ -t 0 ]] && read -r -p "Press Enter to close this window…" || true
  exit 1
}

# ── Single-instance guard (double-clicks must not race) ────────────
# The lock is a flock on this script's fd 9. Long-lived children (the nohup'd service
# and relaunched GUI) MUST NOT inherit fd 9: a leaked fd pins the flock forever, so
# every later update/rollback would claim "already running" while nothing is running.
# The nohup spawns below therefore close fd 9 with `9>&-`, and the EXIT trap releases
# the lock + removes the pid marker when this script finishes.
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

# ── Locate the repository ───────────────────────────────────────────
REPO_DIR="${BOSSCAM_REPO:-}"
if [[ -z "$REPO_DIR" ]]; then
  for c in "$HOME/Documents/BossCamSuite-main" "$HOME/BossCamSuite-main" "$HOME/Documents/BossCamSuite"; do
    if [[ -d "$c/.git" ]]; then REPO_DIR="$c"; break; fi
  done
fi
[[ -n "$REPO_DIR" && -f "$REPO_DIR/BossCamSuite.Linux.sln" ]] \
  || die "Repository not found. Set BOSSCAM_REPO to its path."

GUI_PREFIX="${BOSSCAM_GUI_PREFIX:-/opt/bosscam-gui}"
SERVICE_PREFIX="${BOSSCAM_SERVICE_PREFIX:-/opt/bosscam}"
ROLLBACK_DIR="${BOSSCAM_ROLLBACK_DIR:-$HOME/.local/share/BossCamSuite/rollback}"
PREV_GUI="$ROLLBACK_DIR/gui"
PREV_SVC="$ROLLBACK_DIR/svc"
API="${BOSSCAM_API:-http://127.0.0.1:5317}"

# ── .NET toolchain (mirrors the installer's resolution) ────────────
if [[ -z "${DOTNET_ROOT:-}" && -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
elif [[ -z "${DOTNET_ROOT:-}" && -x "$HOME/.local/share/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.local/share/dotnet"
fi
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}$PATH"
export MSBUILDDISABLENODEREUSE=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
: "${DOTNET_SYSTEM_NET_DISABLEIPV6:=1}"
export DOTNET_SYSTEM_NET_DISABLEIPV6
command -v dotnet >/dev/null 2>&1 || die ".NET SDK not found — install the .NET 8 SDK (see README)."
command -v rsync >/dev/null 2>&1 || die "rsync is required (sudo apt-get install -y rsync)."

say "=== BossCamSuite update ==="
say "  Repo    : $REPO_DIR"
say "  Service : $SERVICE_PREFIX"
say "  GUI     : $GUI_PREFIX"
say "  Rollback: $ROLLBACK_DIR"

# ── Pull latest pushed work (fast-forward only) ────────────────────
if [[ "${BOSSCAM_UPDATE_PULL:-1}" != "0" ]]; then
  if git -C "$REPO_DIR" pull --ff-only --quiet 2>/dev/null; then
    say "  ✓ Pulled latest work from origin."
  else
    say "  · git pull skipped/failed — building the local checkout as-is."
  fi
fi

# ── Snapshot the current install into the rollback slot ────────────
# Copy to a staging path first, then atomically swap into the slot: if the
# copy fails the previous rollback slot is left intact instead of destroyed.
mkdir -p "$ROLLBACK_DIR"
if [[ -d "$GUI_PREFIX" ]]; then
  rm -rf "$ROLLBACK_DIR/.gui.new"
  cp -a "$GUI_PREFIX" "$ROLLBACK_DIR/.gui.new" || die "Could not snapshot current GUI into $ROLLBACK_DIR."
  rm -rf "$PREV_GUI"
  mv "$ROLLBACK_DIR/.gui.new" "$PREV_GUI"
  say "  ✓ Previous GUI saved -> $PREV_GUI"
fi
if [[ -d "$SERVICE_PREFIX" ]]; then
  rm -rf "$ROLLBACK_DIR/.svc.new"
  cp -a "$SERVICE_PREFIX" "$ROLLBACK_DIR/.svc.new" || die "Could not snapshot current service into $ROLLBACK_DIR."
  rm -rf "$PREV_SVC"
  mv "$ROLLBACK_DIR/.svc.new" "$PREV_SVC"
  say "  ✓ Previous service saved -> $PREV_SVC"
fi

# ── Publish service + GUI ──────────────────────────────────────────
say "  Publishing BossCamService…"
rm -rf /tmp/bosscam-service-publish
dotnet publish "$REPO_DIR/src/BossCam.Service/BossCam.Service.csproj" \
  -c Release -o /tmp/bosscam-service-publish --no-restore --nologo -v q \
  || dotnet publish "$REPO_DIR/src/BossCam.Service/BossCam.Service.csproj" \
     -c Release -o /tmp/bosscam-service-publish --nologo -v q
[[ -f /tmp/bosscam-service-publish/BossCam.Service.dll ]] || die "Service publish produced no output."

say "  Publishing desktop GUI…"
rm -rf /tmp/bosscam-gui-publish
dotnet publish "$REPO_DIR/src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj" \
  -c Release -o /tmp/bosscam-gui-publish --no-restore --nologo -v q \
  || dotnet publish "$REPO_DIR/src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj" \
     -c Release -o /tmp/bosscam-gui-publish --nologo -v q
[[ -x /tmp/bosscam-gui-publish/BossCam.Desktop.Avalonia ]] || die "GUI publish produced no output."

# ── Install ─────────────────────────────────────────────────────────
# NOTE: this updater refreshes an existing install (the /opt dirs are created
# by scripts/install-bosscam-gui.sh, which runs with sudo). It cannot
# bootstrap a fresh install by itself.
mkdir -p "$GUI_PREFIX" "$SERVICE_PREFIX"
rsync -a --delete --exclude=launch-bosscam.sh /tmp/bosscam-gui-publish/ "$GUI_PREFIX/"
chmod +x "$GUI_PREFIX/BossCam.Desktop.Avalonia"
rsync -a --delete /tmp/bosscam-service-publish/ "$SERVICE_PREFIX/"
chmod +x "$SERVICE_PREFIX/BossCam.Service" 2>/dev/null || true

# Launcher: keep the previous one if present, otherwise write the standard one.
if [[ -f "$PREV_GUI/launch-bosscam.sh" ]]; then
  cp "$PREV_GUI/launch-bosscam.sh" "$GUI_PREFIX/launch-bosscam.sh" && chmod +x "$GUI_PREFIX/launch-bosscam.sh"
elif [[ ! -f "$GUI_PREFIX/launch-bosscam.sh" ]]; then
  cat > "$GUI_PREFIX/launch-bosscam.sh" <<EOF
#!/usr/bin/env bash
# BossCamSuite launcher: wait for the local service, then start the GUI.
if ! systemctl is-active --quiet bosscam.service 2>/dev/null; then
  systemctl start bosscam.service 2>/dev/null || true
fi
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if curl --silent --fail --max-time 1 http://127.0.0.1:5317/api/health >/dev/null 2>&1; then
    exec "$GUI_PREFIX/BossCam.Desktop.Avalonia" "\$@"
  fi
  sleep 1
done
notify-send --urgency=critical "BossCamSuite" "Local service is not responding. Check: systemctl status bosscam" 2>/dev/null || true
exit 1
EOF
  chmod +x "$GUI_PREFIX/launch-bosscam.sh"
fi

say "  ✓ Installed new build (GUI + service)."

# ── Restart service + relaunch GUI (skippable for testing) ─────────
if [[ "${BOSSCAM_NO_RESTART:-0}" == "1" ]]; then
  say "  BOSSCAM_NO_RESTART=1 — service/GUI left untouched."
  exit 0
fi

say "  Restarting BossCamService…"
if systemctl list-unit-files 2>/dev/null | grep -q '^bosscam.service'; then
  if ! timeout 30 systemctl restart bosscam.service 2>/dev/null; then
    svc_pid="$(pgrep -f 'BossCam.Service.dll' | head -1 || true)"
    [[ -n "$svc_pid" ]] && kill -9 "$svc_pid" 2>/dev/null || true
  fi
else
  pkill -f 'BossCam.Service.dll' 2>/dev/null || true
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
  pkill -f "$GUI_PREFIX/BossCam.Desktop.Avalonia" 2>/dev/null || true
  sleep 1
  nohup "$GUI_PREFIX/launch-bosscam.sh" >/tmp/bosscam-gui-launch.log 2>&1 9>&- &
  say "  ✓ GUI relaunched."
else
  say "  · No display session detected — start the GUI from its desktop icon."
fi

say "=== Update complete. Previous build is one ROLLBACK click away. ==="
command -v notify-send >/dev/null 2>&1 && \
  notify-send -u normal "BossCamSuite updated" "Service + GUI rebuilt from $REPO_DIR. Rollback slot ready." >/dev/null 2>&1 || true
[[ -t 0 ]] && read -r -p "Press Enter to close this window…" || true
