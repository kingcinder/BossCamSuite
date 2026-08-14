#!/usr/bin/env bash
# install-video-feed-watchdog.sh — install the broken-video-feed watchdog.
#
# Publishes scripts/video-feed-watchdog.sh as a systemd oneshot service driven by
# a timer (default every 2 minutes, Persistent=true so missed ticks fire on boot).
# Each tick detects cameras whose live feed is down and runs the repair ladder:
# probe -> reconnect -> diagnose -> restart recording -> subnet hunt/repoint ->
# page once per outage episode.
#
# Usage:
#   sudo ./scripts/install-video-feed-watchdog.sh
#   sudo OFFLINE_GRACE=120 NOTIFY_CMD=/usr/local/bin/page ./scripts/install-video-feed-watchdog.sh
#
# Env vars (all optional, baked into the unit):
#   TIMER_NAME           unit name suffix    (default bosscam-video-feed-watchdog)
#   ON_CALENDAR          systemd OnCalendar  (default *:0/2 = every 2 minutes)
#   RANDOM_DELAY         RandomizedDelaySec  (default 15s, softens burst)
#   WATCHDOG_USER        service user        (default $SUDO_USER or $USER)
#   API                  BossCam API base URL(default http://127.0.0.1:5317)
#   OFFLINE_GRACE        offline seconds before the ladder runs (default 60)
#   MAX_ATTEMPTS         ladder attempts before paging          (default 3)
#   HUNT                 0 disables the subnet hunt/repoint step (default 1)
#   NOTIFY_CMD           paging command (single executable path)
set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WD_SCRIPT="${SCRIPTS_DIR}/video-feed-watchdog.sh"
if [[ ! -x "$WD_SCRIPT" ]]; then
  chmod +x "$WD_SCRIPT" 2>/dev/null || true
fi
if [[ ! -f "$WD_SCRIPT" ]]; then
  echo "ERROR: video-feed-watchdog.sh not found: $WD_SCRIPT" >&2
  exit 2
fi

if [[ $EUID -ne 0 ]]; then
  echo "ERROR: This script must be run as root (use sudo)." >&2
  exit 3
fi

TIMER_NAME="${TIMER_NAME:-bosscam-video-feed-watchdog}"
ON_CALENDAR="${ON_CALENDAR:-*:0/2}"
RANDOM_DELAY="${RANDOM_DELAY:-15}"
WATCHDOG_USER="${WATCHDOG_USER:-${SUDO_USER:-$USER}}"
API="${API:-http://127.0.0.1:5317}"
OFFLINE_GRACE="${OFFLINE_GRACE:-60}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-3}"
HUNT="${HUNT:-1}"
NOTIFY_CMD="${NOTIFY_CMD:-}"

UNIT_DIR="/etc/systemd/system"
SERVICE_UNIT="${UNIT_DIR}/${TIMER_NAME}.service"
TIMER_UNIT="${UNIT_DIR}/${TIMER_NAME}.timer"

if ! echo "$ON_CALENDAR" | grep -q ':' ; then
  echo "ERROR: ON_CALENDAR='$ON_CALENDAR' looks wrong (expected e.g. *:0/2)" >&2
  exit 3
fi

echo "[watchdog] installing ${TIMER_NAME} (tick ${ON_CALENDAR}, grace ${OFFLINE_GRACE}s, ${MAX_ATTEMPTS} attempts before paging, hunt=${HUNT}, api ${API})"
echo "[watchdog] state/log: <repo>/local-video-feed/video-feed-watchdog.{state,log,alerts.log}"
if [[ -n "$NOTIFY_CMD" ]]; then
  echo "[watchdog] paging via: ${NOTIFY_CMD}"
fi

cat > "$SERVICE_UNIT" <<UNITEOF
[Unit]
Description=BossCam broken-video-feed watchdog — repair ladder + operator paging
Documentation=https://github.com/kingcinder/BossCamSuite
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=${WATCHDOG_USER}
Environment=API=${API}
Environment=OFFLINE_GRACE=${OFFLINE_GRACE}
Environment=MAX_ATTEMPTS=${MAX_ATTEMPTS}
Environment=HUNT=${HUNT}
Environment=NOTIFY_CMD=${NOTIFY_CMD}
# Config comes ONLY from Environment= above (no positional args — the script
# reads its env, so an operator editing the Environment= lines sees the effect).
ExecStart=${WD_SCRIPT}
TimeoutStartSec=300
TimeoutStopSec=15
SyslogIdentifier=${TIMER_NAME}
UNITEOF

cat > "$TIMER_UNIT" <<UNITEOF
[Unit]
Description=Periodic trigger for the BossCam broken-video-feed watchdog
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
echo "Video-feed watchdog installed: ${TIMER_NAME}.timer"
echo ""
echo "  timer:    systemctl status ${TIMER_NAME}.timer"
echo "  service:  systemctl status ${TIMER_NAME}.service"
echo "  next run: systemctl list-timers ${TIMER_NAME}"
echo "  log:      journalctl -u ${TIMER_NAME}.service -f"
echo "  state:    <repo>/local-video-feed/video-feed-watchdog.state"
echo "  alerts:   <repo>/local-video-feed/video-feed-watchdog-alerts.log"
echo "  run once: ${WD_SCRIPT}"
echo "  stop:     sudo systemctl disable --now ${TIMER_NAME}.timer"
echo ""
echo "Each tick runs the repair ladder on any camera offline past ${OFFLINE_GRACE}s:"
echo "  probe -> reconnect -> diagnose -> restart recording -> subnet hunt/repoint"
echo "and pages once per outage episode after ${MAX_ATTEMPTS} exhausted attempts."
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
