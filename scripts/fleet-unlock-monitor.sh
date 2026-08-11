#!/usr/bin/env bash
# ── fleet-unlock-monitor.sh — periodic fleet port/gate poller + sweep auto-resume ──
#
# Polls each camera's port state (NetSDK HTTP :80, RTSP :554, eseecloud :8899,
# proprietary :34567) and the /user/user_list.xml gate every INTERVAL seconds
# for a DURATION-second window (default: 300s / 3600s = 5 min for 1 hour).
#
# When a camera is HTTP-reachable and its gate is still closed ("check in falied"),
# the full-pool sweep (sweep-full-pool.sh) is auto-resumed for that camera —
# but only when:
#   1. no sweep is already running for that camera (pgrep single-instance guard)
#   2. the camera's checkpoint is NOT already at the end of the pool
#      (an un-resumed checkpoint means the camera went offline mid-sweep, which
#      is exactly the "came back online" case this monitor exists to catch).
#   3. FORCE=1 overrides the completed-checkpoint guard for manual re-verification.
#
# A camera whose gate reads OPEN is logged loudly and appended to
# /tmp/fleet-gate-open.log — that is the unlock win this monitor is watching for.
#
# Usage:
#   ./scripts/fleet-unlock-monitor.sh [duration_seconds] [interval_seconds] [cams...]
#     duration  default 3600 (1 hour)
#     interval  default 300  (5 min)
#     cams      default 10.0.0.169 10.0.0.227 10.0.0.29
#   Env:
#     POOL=...            candidate pool (default /tmp/sweep-full.txt)
#     FORCE=1             re-run sweep even when the checkpoint is complete
#     MONITOR_LOG=...     log path (default /tmp/fleet-monitor-<ts>.log)
#   Output:
#     /tmp/fleet-monitor-<ts>.log   per-cycle probe lines + sweep triggers
#     /tmp/fleet-sweep-<ip>.log     launched sweep output per camera
#     /tmp/fleet-gate-open.log      appended whenever any gate reads OPEN

set -u
set -o pipefail

DURATION="${1:-3600}"
INTERVAL="${2:-300}"
if [ "$#" -gt 2 ]; then
  shift 2; CAMS="$*"
else
  CAMS="${CAMS:-10.0.0.169 10.0.0.227 10.0.0.29}"
fi
POOL="${POOL:-/tmp/sweep-full.txt}"
SWEEP_BIN="${SWEEP_BIN:-$(cd "$(dirname "$0")" && pwd)/sweep-full-pool.sh}"
LOG="${MONITOR_LOG:-/tmp/fleet-monitor-$(date -u +%Y%m%dT%H%M%SZ).log}"
GATE_LOG=/tmp/fleet-gate-open.log

total=$(wc -l < "$POOL" 2>/dev/null || echo 0)
pool_fp=$(md5sum "$POOL" 2>/dev/null | awk '{print $1}' || echo "")
[ -f "$SWEEP_BIN" ] || { echo "!! sweep script not found: $SWEEP_BIN"; exit 2; }

log() { echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') $*" | tee -a "$LOG"; }

probe_tcp() { timeout 2 bash -c "echo > /dev/tcp/$1/$2" 2>/dev/null && echo open || echo closed; }

# Prints a single space-free state line for one camera.
probe_camera() {
  local ip="$1" http gate gbody
  http=$(curl -sS -o /dev/null -w '%{http_code}' -m 3 "http://$ip/NetSDK/System/deviceInfo" 2>/dev/null)
  http="${http:-000}"
  gbody=$(curl -sS -m 4 "http://$ip/user/user_list.xml" 2>/dev/null | tr -d '\r')
  if [ -z "$gbody" ]; then
    gate="NO-RESPONSE"
  elif echo "$gbody" | grep -q "check in falied"; then
    gate="GATED"
  else
    gate="OPEN"
  fi
  echo "HTTP=$http GATE=$gate RTSP=$(probe_tcp "$ip" 554) 8899=$(probe_tcp "$ip" 8899) 34567=$(probe_tcp "$ip" 34567)"
}

# Launches a background full-pool sweep resume for one camera (guarded).
maybe_resume_sweep() {
  local ip="$1" ckpt="/tmp/sweep-full-$ip.ckpt" ckpt_line=0

  if pgrep -f "sweep-full-pool.sh.*$ip" >/dev/null 2>&1; then
    log "[$ip] sweep already running — not re-triggering"
    return 0
  fi

  if [ -f "$ckpt" ]; then
    ckpt_line=$(sed -n '1p' "$ckpt" 2>/dev/null || echo 0)
    ckpt_line=${ckpt_line:-0}
    ckpt_fp=$(sed -n '2p' "$ckpt" 2>/dev/null || echo "")
    # Skip only when the pool is fully swept AND the pool file is unchanged;
    # a changed pool (new candidates) must always re-trigger the sweep.
    if [ "$ckpt_line" -ge "$total" ] && [ "$ckpt_fp" = "$pool_fp" ] && [ "${FORCE:-0}" != "1" ]; then
      log "[$ip] pool already fully swept (checkpoint $ckpt_line/$total) — skipping resume (FORCE=1 to override)"
      return 0
    fi
    if [ "$ckpt_fp" != "$pool_fp" ]; then
      log "[$ip] checkpoint fingerprint mismatch — pool changed, will re-sweep"
    fi
  fi

  log "[$ip] ★ reachable + GATED, checkpoint $ckpt_line/$total — launching full-pool sweep resume"
  nohup setsid "$SWEEP_BIN" "$POOL" "$ip" >>"/tmp/fleet-sweep-$ip.log" 2>&1 &
  log "[$ip] sweep launched (pid $!) → /tmp/fleet-sweep-$ip.log"
}

log "═══ fleet unlock monitor started: ${DURATION}s window, ${INTERVAL}s interval ═══"
log "  cams : $CAMS"
log "  pool : $POOL ($total candidates)   sweep: $SWEEP_BIN"
END=$((SECONDS + DURATION))
cycle=0
while [ "$SECONDS" -lt "$END" ]; do
  cycle=$((cycle + 1))
  log "── cycle $cycle ($(date -u '+%H:%M:%S'), ${DURATION}s window, $((${END} - SECONDS))s remaining) ──"
  for ip in $CAMS; do
    state=$(probe_camera "$ip")
    log "[$ip] $state"
    http=$(echo "$state" | grep -o 'HTTP=[0-9]*' | cut -d= -f2)
    gate=$(echo "$state" | grep -o 'GATE=[A-Z-]*' | cut -d= -f2)
    if [ "$gate" = "OPEN" ]; then
      log "[$ip] ★★★ GATE OPEN — camera unlocked!"
      echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') $ip GATE OPEN" >> "$GATE_LOG"
    elif [ "$http" != "000" ] && [ "$gate" != "OPEN" ]; then
      # Reachable but not open — GATED (normal locked state) or NO-RESPONSE gate
      # body (live device whose gate endpoint answered empty). Either way a sweep
      # resume is worth launching; the sweep's own 3-probe preflight is the real
      # reachability gate and keeps this from hammering a dead peer.
      maybe_resume_sweep "$ip"
    fi
  done
  [ $((SECONDS + INTERVAL)) -ge "$END" ] && break
  sleep "$INTERVAL"
done
log "═══ monitor window complete ($cycle cycles) — final state ═══"
for ip in $CAMS; do
  log "[$ip] $(probe_camera "$ip")"
done
log "═══ done — sweeps (if any) keep running in the background; see /tmp/fleet-sweep-*.log ═══"
