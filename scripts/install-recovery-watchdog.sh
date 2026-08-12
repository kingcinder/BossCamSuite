#!/usr/bin/env bash
# install-recovery-watchdog.sh — install the autonomous camera-recovery watchdog.
#
# Publishes scripts/recovery-watchdog.sh as a systemd oneshot service driven by
# a timer (default every 5 minutes, Persistent=true so missed ticks fire on
# boot). Each tick checks the auto-recovery worker's last-scan timestamp and
# every visible factory-reset camera AP, paging the operator when the worker
# stalls (>STALE_MINUTES since last scan), a camera AP stays visible past
# STUCK_MINUTES without recovery, or the suite service itself is unreachable.
#
# Usage:
#   sudo ./scripts/install-recovery-watchdog.sh
#   sudo STALE_MINUTES=5 STUCK_MINUTES=60 NOTIFY_CMD=/usr/local/bin/page ./scripts/install-recovery-watchdog.sh
#
# Env vars (all optional, baked into the unit):
#   TIMER_NAME           unit name suffix    (default bosscam-recovery-watchdog)
#   ON_CALENDAR          systemd OnCalendar  (default *:0/5 = every 5 minutes)
#   RANDOM_DELAY         RandomizedDelaySec  (default 15s, softens burst)
#   WATCHDOG_USER        service user        (default $SUDO_USER or $USER)
#   API                  BossCam API base URL(default http://127.0.0.1:5317)
#   STALE_MINUTES        worker-staleness threshold   (default 5)
#   STUCK_MINUTES        stuck-AP threshold           (default 60)
#   GRACE_MINUTES        AP-absent grace before the stuck clock resets
#                        (default 15; a scan blip shorter than this does not
#                        restart a stuck AP's hour-long clock)
#   NOTIFY_CMD           paging command (single executable path)
set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WD_SCRIPT="${SCRIPTS_DIR}/recovery-watchdog.sh"
if [[ ! -x "$WD_SCRIPT" ]]; then
  echo "ERROR: recovery-watchdog.sh not found or not executable: $WD_SCRIPT" >&2
  exit 2
fi

if [[ $EUID -ne 0 ]]; then
  echo "ERROR: This script must be run as root (use sudo)." >&2
  exit 3
fi

TIMER_NAME="${TIMER_NAME:-bosscam-recovery-watchdog}"
ON_CALENDAR="${ON_CALENDAR:-*:0/5}"
RANDOM_DELAY="${RANDOM_DELAY:-15}"
WATCHDOG_USER="${WATCHDOG_USER:-${SUDO_USER:-$USER}}"
API="${API:-http://127.0.0.1:5317}"
STALE_MINUTES="${STALE_MINUTES:-5}"
STUCK_MINUTES="${STUCK_MINUTES:-60}"
GRACE_MINUTES="${GRACE_MINUTES:-15}"
NOTIFY_CMD="${NOTIFY_CMD:-}"

UNIT_DIR="/etc/systemd/system"
SERVICE_UNIT="${UNIT_DIR}/${TIMER_NAME}.service"
TIMER_UNIT="${UNIT_DIR}/${TIMER_NAME}.timer"

if ! echo "$ON_CALENDAR" | grep -q ':' ; then
  echo "ERROR: ON_CALENDAR='$ON_CALENDAR' looks wrong (expected e.g. *:0/5)" >&2
  exit 3
fi

echo "[watchdog] installing ${TIMER_NAME} (tick ${ON_CALENDAR}, stale>${STALE_MINUTES}m, stuck>${STUCK_MINUTES}m, grace ${GRACE_MINUTES}m, api ${API})"
echo "[watchdog] state/log: <repo>/local-camera-recovery/recovery-watchdog.{state,log,alerts.log}"
if [[ -n "$NOTIFY_CMD" ]]; then
  echo "[watchdog] paging via: ${NOTIFY_CMD}"
fi

cat > "$SERVICE_UNIT" <<UNITEOF
[Unit]
Description=BossCam camera-recovery watchdog — worker staleness + stuck-AP monitor
Documentation=https://github.com/kingcinder/BossCamSuite
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=${WATCHDOG_USER}
Environment=API=${API}
Environment=STALE_MINUTES=${STALE_MINUTES}
Environment=STUCK_MINUTES=${STUCK_MINUTES}
Environment=GRACE_MINUTES=${GRACE_MINUTES}
Environment=NOTIFY_CMD=${NOTIFY_CMD}
# Config comes ONLY from Environment= above (no positional args — the script
# reads its env, so an operator editing the Environment= lines sees the effect).
ExecStart=${WD_SCRIPT}
TimeoutStartSec=30
TimeoutStopSec=10
SyslogIdentifier=${TIMER_NAME}
UNITEOF

cat > "$TIMER_UNIT" <<UNITEOF
[Unit]
Description=Periodic trigger for the BossCam camera-recovery watchdog
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

echo "[watchdog] enabling timer..."
systemctl restart "${TIMER_NAME}.timer"
sleep 1
systemctl --no-pager --full status "${TIMER_NAME}.timer" || true

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Camera-recovery watchdog installed: ${TIMER_NAME}.timer"
echo ""
echo "  timer:    systemctl status ${TIMER_NAME}.timer"
echo "  service:  systemctl status ${TIMER_NAME}.service"
echo "  next run: systemctl list-timers ${TIMER_NAME}"
echo "  log:      journalctl -u ${TIMER_NAME}.service -f"
echo "  state:    <repo>/local-camera-recovery/recovery-watchdog.state"
echo "  alerts:   <repo>/local-camera-recovery/recovery-watchdog-alerts.log"
echo "  run once: ${WD_SCRIPT}"
echo "  stop:     sudo systemctl disable --now ${TIMER_NAME}.timer"
echo ""
echo "Each tick pages once when: the worker stops scanning (>${STALE_MINUTES}m),"
echo "a camera AP stays visible >${STUCK_MINUTES}m without recovery (a blip"
echo "under ${GRACE_MINUTES}m does not reset that clock), or the suite service"
echo "is unreachable on ${API}."
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
