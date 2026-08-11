#!/usr/bin/env bash
# install-gate-flip-heartbeat.sh — install the hourly cloud-resurrection monitor.
#
# Publishes scripts/gate-flip-heartbeat.sh as a root systemd service driven by
# an hourly timer (OnCalendar=*:00:00, Persistent=true so missed ticks fire on
# boot). Each tick runs gate-flip-experiment.sh --no-early-abort for
# HEARTBEAT_DURATION seconds against each camera and appends verdicts to
# captures/gate-flip-heartbeat.log — any future cloud-session resurrection is
# caught and logged within the hour.
#
# Usage:
#   sudo ./scripts/install-gate-flip-heartbeat.sh
#   sudo HEARTBEAT_DURATION=600 CAMS="10.0.0.169 10.0.0.227" ./scripts/install-gate-flip-heartbeat.sh
#
# Env vars (all optional, baked into the unit):
#   HEARTBEAT_DURATION   seconds per camera per tick   (default 600)
#   CAMS                 camera list                   (default 10.0.0.169 10.0.0.227)
#   GATE_INTERVAL        gate poll seconds             (default 5)
#   KEEP_RUNS            newest gate-flip dirs kept    (default 96)
#   KEEP_DAYS            mitm session retention days   (default 3)
#   FLIP_ALERT=1         wrap the heartbeat with scripts/flip-count-alert.sh
#                        so a flip-event count growth in captures/nonce-flips.log
#                        pages the operator each tick (default 0 = run the raw
#                        heartbeat; the wrapper needs no watcher — it greps the
#                        flip history count before/after each tick)
#   TIMER_NAME           unit name suffix              (default bosscam-gate-flip-heartbeat)
#   ON_CALENDAR          systemd OnCalendar spec       (default *:00:00 = hourly)
#   RANDOM_DELAY         RandomizedDelaySec            (default 120s, softens burst)
set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HB_SCRIPT="${SCRIPTS_DIR}/gate-flip-heartbeat.sh"
if [[ ! -x "$HB_SCRIPT" ]]; then
  echo "ERROR: gate-flip-heartbeat.sh not found or not executable: $HB_SCRIPT" >&2
  exit 2
fi
ALERT_SCRIPT="${SCRIPTS_DIR}/flip-count-alert.sh"
FLIP_ALERT="${FLIP_ALERT:-0}"
if [[ "$FLIP_ALERT" = "1" ]]; then
  if [[ ! -x "$ALERT_SCRIPT" ]]; then
    echo "ERROR: flip-count-alert.sh not found or not executable: $ALERT_SCRIPT" >&2
    exit 2
  fi
  EXEC_START="$ALERT_SCRIPT"
else
  EXEC_START="$HB_SCRIPT"
fi

if [[ $EUID -ne 0 ]]; then
  echo "ERROR: This script must be run as root (use sudo)." >&2
  exit 3
fi

HEARTBEAT_DURATION="${HEARTBEAT_DURATION:-600}"
CAMS="${CAMS:-10.0.0.169 10.0.0.227}"
GATE_INTERVAL="${GATE_INTERVAL:-5}"
KEEP_RUNS="${KEEP_RUNS:-96}"
KEEP_DAYS="${KEEP_DAYS:-3}"
TIMER_NAME="${TIMER_NAME:-bosscam-gate-flip-heartbeat}"
ON_CALENDAR="${ON_CALENDAR:-*:00:00}"
RANDOM_DELAY="${RANDOM_DELAY:-120}"

UNIT_DIR="/etc/systemd/system"
SERVICE_UNIT="${UNIT_DIR}/${TIMER_NAME}.service"
TIMER_UNIT="${UNIT_DIR}/${TIMER_NAME}.timer"

# OnCalendar must contain a ':' (hourly spec) — sanity-check the format.
if ! echo "$ON_CALENDAR" | grep -q ':' ; then
  echo "ERROR: ON_CALENDAR='$ON_CALENDAR' looks wrong (expected e.g. *:00:00 or hourly)" >&2
  exit 3
fi

if [[ "$FLIP_ALERT" = "1" ]]; then
  echo "[heartbeat] flip-alert wrapper ENABLED — count growth in nonce-flips.log pages the operator each tick"
fi
echo "[heartbeat] installing ${TIMER_NAME} (${HEARTBEAT_DURATION}s/camera on [${CAMS}], ${ON_CALENDAR})"
echo "[heartbeat] rolling log: $(dirname "$SCRIPTS_DIR")/captures/gate-flip-heartbeat.log"

cat > "$SERVICE_UNIT" <<UNITEOF
[Unit]
Description=BossCam gate-flip heartbeat — hourly cloud-resurrection monitor
Documentation=https://github.com/kingcinder/BossCamSuite
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=root
Environment=HEARTBEAT_DURATION=${HEARTBEAT_DURATION}
Environment=CAMS=${CAMS}
Environment=GATE_INTERVAL=${GATE_INTERVAL}
Environment=KEEP_RUNS=${KEEP_RUNS}
Environment=KEEP_DAYS=${KEEP_DAYS}
# Config comes ONLY from Environment= above (positional args are deliberately
# omitted — the script falls back to HEARTBEAT_DURATION/CAMS env when no args
# are passed, so an operator editing the Environment= lines sees the effect).
ExecStart=${EXEC_START}
# A tick runs HEARTBEAT_DURATION x #cameras + MITM setup/teardown — far beyond
# the 90s oneshot default. 0 = no timeout (systemd waits for the tick to finish;
# the heartbeat's flock guard prevents stacking, so this cannot hang forever).
TimeoutStartSec=0
TimeoutStopSec=30
KillSignal=SIGINT
SyslogIdentifier=${TIMER_NAME}
UNITEOF

cat > "$TIMER_UNIT" <<UNITEOF
[Unit]
Description=Hourly trigger for the BossCam gate-flip heartbeat monitor
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

echo "[heartbeat] enabling timer..."
systemctl restart "${TIMER_NAME}.timer"
sleep 1
systemctl --no-pager --full status "${TIMER_NAME}.timer" || true

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Gate-flip heartbeat installed: ${TIMER_NAME}.timer"
echo ""
echo "  timer:    systemctl status ${TIMER_NAME}.timer"
echo "  service:  systemctl status ${TIMER_NAME}.service"
echo "  next run: systemctl list-timers ${TIMER_NAME}"
echo "  log:      journalctl -u ${TIMER_NAME}.service -f"
echo "  rolling:  <repo>/captures/gate-flip-heartbeat.log"
if [[ "$FLIP_ALERT" = "1" ]]; then
  echo "  alerts:   <repo>/captures/flip-count-alerts.log (flip-count growth pages the operator)"
  echo "  run once: sudo ${ALERT_SCRIPT} ${HEARTBEAT_DURATION} ${CAMS}"
else
  echo "  run once: sudo ${HB_SCRIPT} ${HEARTBEAT_DURATION} ${CAMS}"
fi
echo "  stop:     sudo systemctl disable --now ${TIMER_NAME}.timer"
echo ""
echo "Each hourly tick watches [${CAMS}] for ${HEARTBEAT_DURATION}s per camera;"
echo "a 'GATE OPEN' line in the rolling log = cloud resurrection detected."
if [[ "$FLIP_ALERT" = "1" ]]; then
  echo "A flip-event count growth in nonce-flips.log during a tick pages the operator (${ALERT_SCRIPT})."
fi
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
