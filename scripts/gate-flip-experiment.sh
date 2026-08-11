#!/usr/bin/env bash
# ── gate-flip-experiment.sh — hour-long ANY-reply /user gate-flip measurement ──
#
# Runs the byte-accurate fake EseeCloud server against ONE locked camera for a
# full hour with the ws-server in ROTATE mode (cycling every candidate reply
# shape — cadence/plus1/badoffset/magic/echo/empty — per registration) while a
# parallel poller watches /user/user_list.xml every few seconds. This
# definitively measures whether ANY server reply — not just the byte-accurate
# grant — can flip the camera's "check in falied" gate, and records the exact
# frame in flight at any flip.
#
# Requires root (ARP spoof + iptables REDIRECT + tcpdump) — the underlying
# eseecloud-mitm-capture.sh needs it.
#
# Usage:
#   sudo ./scripts/gate-flip-experiment.sh [duration_seconds] [camera_ip]
#     duration  default 3600 (1 hour)
#     camera    default 10.0.0.169
#   Env:
#     GATE_INTERVAL=5   seconds between /user gate polls
#     WS_NEXT_COUNTER=  cadence (default) | plus1  (forwarded to ws-server)
#     WS_LITE_CADENCE=  0x1E default (forwarded)
#     KEEP_MITM=0       keep the mitm-capture session dir after the run
#
# Output:
#   captures/gate-flip-<ts>/gate-timeline.log   every gate poll: GATED/OPEN/NO-RESP
#   captures/gate-flip-<ts>/experiment.log      orchestration + verdict
#   captures/eseecloud-mitm-<ts>/               the underlying MITM session
#     eseecloud-connections.log                 every frame + every reply hex
#
# Verdict: GATE FLIPPED (with the reply variant + exact hex in flight) or
# NO GATE FLIP (with the number of gated polls and variants cycled).

set -u
set -o pipefail

DURATION="${1:-3600}"
CAM="${2:-10.0.0.169}"
GATE_INTERVAL="${GATE_INTERVAL:-5}"
KEEP_MITM="${KEEP_MITM:-0}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MITM="$HERE/eseecloud-mitm-capture.sh"
[ -x "$MITM" ] || { echo "!! mitm script not found: $MITM"; exit 2; }

TS="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$(dirname "$HERE")/captures/gate-flip-$TS"
mkdir -p "$OUT"
TIMELINE="$OUT/gate-timeline.log"
EXPLOG="$OUT/experiment.log"

log() { echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') $*" | tee -a "$EXPLOG"; }

# ── gate poller (parallel) ───────────────────────────────────────────────────
# Classifies /user/user_list.xml: OPEN (gate flipped!), GATED ("check in
# falied"), or NO-RESP (camera unreachable). Writes one line per poll.
poll_gate() {
  local body
  body=$(curl -sS -m 4 "http://$CAM/user/user_list.xml" 2>/dev/null | tr -d '\r')
  if [ -z "$body" ]; then
    echo "NO-RESP"
  elif echo "$body" | grep -q "check in falied"; then
    echo "GATED"
  else
    echo "OPEN"
  fi
}

gate_loop() {
  local ts end=$((SECONDS + DURATION + 60)) st
  while [ "$SECONDS" -lt "$end" ]; do
    ts=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
    st=$(poll_gate)
    echo "$ts $st" >> "$TIMELINE"
    if [ "$st" = "OPEN" ]; then
      echo "$ts ★★★ GATE OPEN on $CAM ★★★" | tee -a "$EXPLOG"
    fi
    sleep "$GATE_INTERVAL"
  done
}

log "═══ gate-flip experiment started: ${DURATION}s window on $CAM ═══"
log "  gate poll: every ${GATE_INTERVAL}s   reply mode: rotate (cadence→plus1→badoffset→magic→echo→empty)"
log "  timeline: $TIMELINE"
gate_loop &
GATE_PID=$!

# ── the MITM (ws-server in rotate mode) ──────────────────────────────────────
# WS_REPLY_MODE=rotate is consumed by eseecloud-mitm-capture.sh -> ws-server.
# --no-early-abort keeps the MITM capturing the FULL window even when the
# camera stays silent (its check-in timer may exceed the mitm script's 150s
# zero-connection guard) — the whole point of this experiment is to measure
# an hour of every reply variant, so an early abort would void the run.
WS_REPLY_MODE=rotate WS_LITE_MONITOR=1 \
  "$MITM" --no-early-abort "$DURATION" "$CAM" 2>&1 | tee -a "$EXPLOG"
MITM_RC=$?

# ── stop the poller ─────────────────────────────────────────────────────────
kill "$GATE_PID" 2>/dev/null
wait "$GATE_PID" 2>/dev/null

# ── verdict ─────────────────────────────────────────────────────────────────
log "═══ gate-flip experiment complete (mitm rc=$MITM_RC) ═══"
FLIP=$(grep -m1 ' OPEN$' "$TIMELINE" 2>/dev/null || true)
# grep -c prints "0" AND exits 1 on zero matches, so "|| echo 0" would yield
# "0\n0" and break the integer tests below — capture stdout only, default
# empty to 0.
GATED=$(grep -c ' GATED$' "$TIMELINE" 2>/dev/null || true); GATED=${GATED:-0}
NORESP=$(grep -c ' NO-RESP$' "$TIMELINE" 2>/dev/null || true); NORESP=${NORESP:-0}
TOTAL=$(wc -l < "$TIMELINE" 2>/dev/null || echo 0); TOTAL=${TOTAL:-0}
NEWEST=$(ls -dt "$(dirname "$HERE")/captures"/eseecloud-mitm-* 2>/dev/null | head -1)

if [ -n "$FLIP" ]; then
  flip_ts=$(echo "$FLIP" | awk '{print $1}')
  log "★★★ VERDICT: GATE FLIPPED at $flip_ts ★★★"
  if [ -n "$NEWEST" ] && [ -f "$NEWEST/eseecloud-connections.log" ]; then
    # The frame in flight: the LAST REPLY logged BEFORE the flip timestamp.
    # (A tail of the whole log would report end-of-run replies, not the ones
    # that preceded the flip.) ISO timestamps compare lexicographically.
    log "  replies logged before the flip ($NEWEST):"
    # Reply-counted window: collect the last up-to-6 REPLY lines whose ISO
    # timestamp sorts strictly before the flip (REPLY lines are sparse among
    # DATA/REGISTER/CONNECT lines, so a record-indexed window would collapse
    # to one line).
    awk -v t="$flip_ts" '/REPLY/ && $0 < t { n++; lines[n]=$0 }
      END { for (i=(n-5>0?n-5:1); i<=n; i++) print lines[i] }' \
      "$NEWEST/eseecloud-connections.log" 2>/dev/null | tee -a "$EXPLOG"
  fi
else
  log "VERDICT: NO GATE FLIP — $GATED GATED polls over ${DURATION}s"
  if [ "$NORESP" -gt "$GATED" ]; then
    log "  ⚠ $NORESP/$TOTAL polls were NO-RESP (camera unreachable) — verdict "
    log "  may be inconclusive; check whether MITM/ARP broke camera connectivity"
  fi
  log "  every reply variant (cadence/plus1/badoffset/magic/echo/empty) cycled"
  if [ -n "$NEWEST" ] && [ -f "$NEWEST/eseecloud-connections.log" ]; then
    varcount=$(grep -oE 'variant=[a-z-]+' "$NEWEST/eseecloud-connections.log" \
      | sort | uniq -c | tr '\n' ' ')
    log "  variants observed: ${varcount:-none}"
  fi
fi

if [ "$KEEP_MITM" = "0" ] && [ -n "$NEWEST" ]; then
  log "  mitm session kept at $NEWEST (KEEP_MITM=1 to preserve the gate-flip dir only)"
fi
log "  gate timeline: $TIMELINE"
log "═══ done ═══"
