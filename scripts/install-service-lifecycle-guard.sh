#!/usr/bin/env bash
# install-service-lifecycle-guard.sh — install the BossCamSuite service-lifecycle guard.
#
# Publishes scripts/service-lifecycle-guard.sh as a systemd oneshot service
# driven by a timer (default every 2 minutes, Persistent=true so missed ticks
# fire on boot). Each tick:
#
#   1. Hands any stale MANUAL BossCam.Service instance holding the API port
#      over to systemd (stop the manual process, start the bosscam unit),
#   2. Recovers the unit when it is crash-looping / failed with nothing
#      listening (rate-limited by RECOVER_COOLDOWN),
#   3. Reaps orphaned MJPEG recorder processes (PPID 1) and their ffmpeg/curl
#      children.
#
# After installing the timer the installer runs the guard ONCE immediately, so
# the currently-stale instance and any orphans are resolved right now, not on
# the first timer tick.
#
# Usage:
#   sudo ./scripts/install-service-lifecycle-guard.sh
#   sudo API=http://127.0.0.1:5317 RECOVER_COOLDOWN=60 ./scripts/install-service-lifecycle-guard.sh
#
# Env vars (all optional, baked into the unit):
#   TIMER_NAME        unit name suffix    (default bosscam-lifecycle-guard)
#   ON_CALENDAR       systemd OnCalendar  (default *:0/2 = every 2 minutes)
#   RANDOM_DELAY      RandomizedDelaySec  (default 10s, softens burst)
#   WATCHDOG_USER     service user        (default $SUDO_USER or $USER)
#   API               BossCam API base URL(default http://127.0.0.1:5317)
#   TERM_GRACE        graceful-stop grace before SIGKILL (default 15)
#   HEALTH_TIMEOUT    health wait after a handoff (default 45)
#   RECOVER_COOLDOWN  seconds between unit-recovery attempts (default 120)
#
# NOTE on privileges: the guard drives systemctl start/stop bosscam.service
# and kills processes, so it MUST run as root (or a user with a systemd polkit
# rule) — the recovery watchdog only curls the API and can stay unprivileged,
# but this guard cannot. Default is root; override with WATCHDOG_USER=... only
# if you have installed a systemd polkit rule for that user.
set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUARD_SCRIPT="${SCRIPTS_DIR}/service-lifecycle-guard.sh"
if [[ ! -x "$GUARD_SCRIPT" ]]; then
  echo "ERROR: service-lifecycle-guard.sh not found or not executable: $GUARD_SCRIPT" >&2
  exit 2
fi

if [[ $EUID -ne 0 ]]; then
  echo "ERROR: This script must be run as root (use sudo)." >&2
  exit 3
fi

TIMER_NAME="${TIMER_NAME:-bosscam-lifecycle-guard}"
ON_CALENDAR="${ON_CALENDAR:-*:0/2}"
RANDOM_DELAY="${RANDOM_DELAY:-10}"
WATCHDOG_USER="${WATCHDOG_USER:-root}"
API="${API:-http://127.0.0.1:5317}"
TERM_GRACE="${TERM_GRACE:-15}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-45}"
RECOVER_COOLDOWN="${RECOVER_COOLDOWN:-120}"

UNIT_DIR="/etc/systemd/system"
SERVICE_UNIT="${UNIT_DIR}/${TIMER_NAME}.service"
TIMER_UNIT="${UNIT_DIR}/${TIMER_NAME}.timer"

echo "[lifecycle-guard] installing ${TIMER_NAME} (tick ${ON_CALENDAR}, term-grace ${TERM_GRACE}s, health ${HEALTH_TIMEOUT}s, recovery-cooldown ${RECOVER_COOLDOWN}s, api ${API}, user ${WATCHDOG_USER})"
echo "[lifecycle-guard] log/state: <repo>/local-camera-recovery/service-lifecycle-guard.{log,state}"

cat > "$SERVICE_UNIT" <<UNITEOF
[Unit]
Description=BossCamSuite service-lifecycle guard — stale-instance handoff + orphan reaper
Documentation=https://github.com/kingcinder/BossCamSuite
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=${WATCHDOG_USER}
Environment=API=${API}
Environment=TERM_GRACE=${TERM_GRACE}
Environment=HEALTH_TIMEOUT=${HEALTH_TIMEOUT}
Environment=RECOVER_COOLDOWN=${RECOVER_COOLDOWN}
ExecStart=${GUARD_SCRIPT}
# Must exceed TERM_GRACE + HEALTH_TIMEOUT so the handoff can finish.
TimeoutStartSec=180
TimeoutStopSec=30
SyslogIdentifier=${TIMER_NAME}
UNITEOF

cat > "$TIMER_UNIT" <<UNITEOF
[Unit]
Description=Periodic trigger for the BossCamSuite service-lifecycle guard
Documentation=https://github.com/kingcinder/BossCamSuite

[Timer]
OnCalendar=${ON_CALENDAR}
Persistent=true
RandomizedDelaySec=${RANDOM_DELAY}

[Install]
WantedBy=timers.target
UNITEOF

systemctl daemon-reload
systemctl enable "${TIMER_NAME}.timer"

echo "[lifecycle-guard] enabling timer..."
systemctl restart "${TIMER_NAME}.timer"
sleep 1
systemctl --no-pager --full status "${TIMER_NAME}.timer" || true

# Apply immediately: resolve the current stale instance + orphans now, not on
# the first timer tick.
echo ""
echo "[lifecycle-guard] running one-shot apply..."
"$GUARD_SCRIPT" || true
echo "[lifecycle-guard] one-shot apply finished (see journal/log above)."

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Service-lifecycle guard installed: ${TIMER_NAME}.timer"
echo ""
echo "  timer:    systemctl status ${TIMER_NAME}.timer"
echo "  service:  systemctl status ${TIMER_NAME}.service"
echo "  next run: systemctl list-timers ${TIMER_NAME}"
echo "  log:      journalctl -u ${TIMER_NAME}.service -f"
echo "  run once: ${GUARD_SCRIPT}"
echo "  preflight:DRY_RUN=1 ${GUARD_SCRIPT}"
echo "  stop:     sudo systemctl disable --now ${TIMER_NAME}.timer"
echo ""
echo "Each tick: (1) hands a stale manual BossCam.Service off to systemd,"
echo "(2) recovers a crash-looping/failed unit once the port is free (min"
echo "${RECOVER_COOLDOWN}s between attempts), (3) reaps orphaned MJPEG recorder"
echo "processes whose parent died."
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
