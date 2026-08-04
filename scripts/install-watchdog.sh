#!/usr/bin/env bash
# install-watchdog.sh — install a systemd unit that runs
# camera-recovery.sh --watchdog as a persistent service.
#
# The watchdog polls the camera every N seconds and alerts if any service
# drops.  The unit auto-starts at boot and restarts on failure.
#
# Usage:
#   sudo ./scripts/install-watchdog.sh 10.0.0.169
#   sudo ./scripts/install-watchdog.sh 10.0.0.169 admin mypassword
#   sudo WATCHDOG_INTERVAL=30 WATCHDOG_NAME=driveway ./scripts/install-watchdog.sh 10.0.0.169
#
# Env vars (all optional):
#   WATCHDOG_NAME        systemd unit name suffix       (default: derived from IP)
#   WATCHDOG_USER        user to run the service        (default: $SUDO_USER or $USER)
#   WATCHDOG_INTERVAL    seconds between health checks  (default: 60)
#   WATCHDOG_SCRIPT      path to camera-recovery.sh     (default: auto-detected)
#   CAMERA_USER          camera username                (default: admin)
#   CAMERA_PASS          camera password                (default: blank)
#   CAMERA_PORT          camera HTTP port               (default: 80)
#   CAMERA_RTSP_PORT     camera RTSP port               (default: 554)
#   CAMERA_TIMEOUT       initial recovery timeout       (default: 300)
#   CAMERA_INTERVAL      initial poll interval          (default: 5)
set -euo pipefail

# ── args ─────────────────────────────────────────────────────────────────

IP="${1:-}"
if [[ -z "$IP" ]]; then
  echo "Usage: sudo $0 <camera-ip> [username] [password]"
  echo ""
  echo "Env vars (optional):"
  echo "  WATCHDOG_NAME WATCHDOG_USER WATCHDOG_INTERVAL WATCHDOG_SCRIPT"
  echo "  CAMERA_USER CAMERA_PASS CAMERA_PORT CAMERA_RTSP_PORT"
  echo "  CAMERA_TIMEOUT CAMERA_INTERVAL"
  exit 1
fi

USERNAME="${2:-${CAMERA_USER:-admin}}"
PASSWORD="${3:-${CAMERA_PASS:-}}"
PORT="${CAMERA_PORT:-80}"
RTSP_PORT="${CAMERA_RTSP_PORT:-554}"
TIMEOUT="${CAMERA_TIMEOUT:-300}"
POLL_INTERVAL="${CAMERA_INTERVAL:-5}"
WD_INTERVAL="${WATCHDOG_INTERVAL:-60}"

# ── derive paths and names ──────────────────────────────────────────────

SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECOVERY_SCRIPT="${WATCHDOG_SCRIPT:-${SCRIPTS_DIR}/camera-recovery.sh}"

if [[ ! -x "$RECOVERY_SCRIPT" ]]; then
  echo "ERROR: camera-recovery.sh not found or not executable: $RECOVERY_SCRIPT" >&2
  echo "       Build it first or set WATCHDOG_SCRIPT=/path/to/camera-recovery.sh" >&2
  exit 2
fi

# Sanitize IP for use in unit name (replace dots with dashes)
SAFE_IP="${IP//./-}"
UNIT_NAME="bosscam-watchdog-${WATCHDOG_NAME:-${SAFE_IP}}"
SERVICE_USER="${WATCHDOG_USER:-${SUDO_USER:-$USER}}"
UNIT_FILE="/etc/systemd/system/${UNIT_NAME}.service"

# ── check for root ──────────────────────────────────────────────────────

if [[ $EUID -ne 0 ]]; then
  echo "ERROR: This script must be run as root (use sudo)." >&2
  echo "  sudo $0 $IP" >&2
  exit 3
fi

# ── generate unit ───────────────────────────────────────────────────────

echo "Installing systemd unit: ${UNIT_NAME}"
echo ""
echo "  Camera IP:       ${IP}:${PORT}"
echo "  RTSP port:       ${RTSP_PORT}"
echo "  Watchdog user:   ${SERVICE_USER}"
echo "  Check interval:  ${WD_INTERVAL}s"
echo "  Recovery timeout:${TIMEOUT}s"
echo "  Recovery script: ${RECOVERY_SCRIPT}"
echo ""

cat > "$UNIT_FILE" << UNITEOF
[Unit]
Description=BossCam camera watchdog — ${IP}:${PORT}
Documentation=https://github.com/kingcinder/BossCamSuite
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Environment=CAMERA_TIMEOUT=${TIMEOUT}
Environment=CAMERA_INTERVAL=${POLL_INTERVAL}
Environment=CAMERA_USER=${USERNAME}
Environment=CAMERA_PASS=${PASSWORD}
Environment=CAMERA_PORT=${PORT}
Environment=CAMERA_RTSP_PORT=${RTSP_PORT}
Environment=CAMERA_WATCHDOG_INTERVAL=${WD_INTERVAL}
ExecStart=${RECOVERY_SCRIPT} --watchdog ${IP}
Restart=on-failure
RestartSec=10
# After 5 restarts in 120s, stop trying — prevents thrashing if the
# camera is permanently unreachable.
StartLimitBurst=5
StartLimitIntervalSec=120
KillSignal=SIGINT
# Must exceed the longest probe timeout (snapshot curl -m 8) so an
# in-flight probe can finish before SIGKILL.
TimeoutStopSec=15
SyslogIdentifier=${UNIT_NAME}

[Install]
WantedBy=multi-user.target
UNITEOF

# ── enable and start ────────────────────────────────────────────────────

systemctl daemon-reload
systemctl enable "${UNIT_NAME}.service"

echo "Unit installed. Starting..."
systemctl restart "${UNIT_NAME}.service" || true

sleep 2
echo ""
systemctl --no-pager --full status "${UNIT_NAME}.service" || true

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Watchdog service installed: ${UNIT_NAME}"
echo ""
echo "  status:   systemctl status ${UNIT_NAME}"
echo "  logs:     journalctl -u ${UNIT_NAME} -f"
echo "  stop:     systemctl stop ${UNIT_NAME}"
echo "  disable:  systemctl disable ${UNIT_NAME}"
echo "  remove:   sudo rm ${UNIT_FILE} && sudo systemctl daemon-reload"
echo ""
echo "The watchdog stays resident and re-checks the camera every ${WD_INTERVAL}s."
echo "It alerts (terminal bell + syslog) if any service drops."
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
