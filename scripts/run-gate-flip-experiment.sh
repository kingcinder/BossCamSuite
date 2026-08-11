#!/usr/bin/env bash
# ── run-gate-flip-experiment.sh — MITM + gate probe, one elevated command ──
#
# Starts the ARP-spoof + iptables MITM capture (eseecloud-mitm-capture.sh)
# against the locked 5523-W cameras in the background, then polls the
# /user/*.xml gate so we can see whether the forged cloud check-in (incl.
# the new /message/message success replies) flips it open. When the probe
# finishes the MITM is killed and its cleanup trap tears down iptables /
# bettercap / tcpdump.
#
# MUST run as root (ARP spoof + iptables + tcpdump + privileged ports):
#   sudo bash scripts/run-gate-flip-experiment.sh [duration_seconds]
#
# Output:
#   captures/eseecloud-mitm-<ts>/...       full MITM session (fake servers)
#   captures/gate-probe-<ts>.log           gate state timeline

set -u

DURATION="${1:-480}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$PROJECT_ROOT" || exit 1

if [[ $EUID -ne 0 ]]; then
  echo "Must run as root (ARP spoof + iptables + tcpdump + privileged ports)." >&2
  exit 1
fi

PROBE_LOG="captures/gate-probe-$(date -u +%Y%m%dT%H%M%SZ).log"
echo "═══ gate-flip experiment: ${DURATION}s ═══"
echo "  probe log: $PROBE_LOG"
echo ""

# The MITM script runs its own capture window; we probe alongside it. The
# probe runs slightly shorter than the MITM so we can kill it cleanly.
# Guard against tiny durations so the probe arithmetic stays positive.
PROBE_SECS=$(( DURATION > 20 ? DURATION - 15 : 5 ))
bash "$SCRIPT_DIR/eseecloud-mitm-capture.sh" "$DURATION" 10.0.0.29 10.0.0.169 \
  > /tmp/gateflip-mitm.log 2>&1 &
MITM_PID=$!
sleep 6

bash "$SCRIPT_DIR/eseecloud-gate-probe.sh" "$PROBE_SECS" 2>&1 | tee "$PROBE_LOG"

echo ""
echo "═══ stopping MITM ═══"
kill "$MITM_PID" 2>/dev/null
wait "$MITM_PID" 2>/dev/null
echo "  MITM exit: $?"
echo ""
echo "═══ MITM log tail ═══"
tail -50 /tmp/gateflip-mitm.log
echo ""
echo "═══ session files ═══"
ls -1dt captures/eseecloud-mitm-* 2>/dev/null | head -3
