#!/usr/bin/env bash
# ── beacon-listener.sh — camera discovery-beacon cadence vs bettercap flaps ──
#
# Watches a camera's UDP broadcasts to 255.255.255.255 (the 760-byte discovery
# beacons seen on source ports :8002 and :18002) and logs every emission with a
# timestamp, while simultaneously tailing an eseecloud-mitm session's
# bettercap.log for gateway flaps (ARP-spoof state changes). On completion it
# correlates the two timelines to answer: are the beacons ARP-spoof-TRIGGERED,
# or do they run on a FIXED timer independent of spoofing?
#
# Requires root (tcpdump). Does NOT start a MITM — it only listens, so it can
# run with or without bettercap active. To measure the no-spoof baseline, run it
# while no MITM is up (e.g. right after a completed gate-flip run).
#
# Usage:
#   sudo ./scripts/beacon-listener.sh [CAM] [DURATION] [MITM_DIR]
#     CAM        camera IP, default 10.0.0.169
#     DURATION   seconds to listen, default 3600
#     MITM_DIR   eseecloud-mitm-<ts> dir whose bettercap.log to tail for
#                gateway flaps; default = newest eseecloud-mitm-20* dir;
#                pass "none" to skip flap tracking
#
# Env:
#     BEACON_LOG=...   output path (default captures/beacon-listener-<ts>.log)
#
# Output:
#   captures/beacon-listener-<ts>.log   BEACON/FLAP lines + correlation summary
#   stdout                              correlation summary + verdict

set -u
set -o pipefail

CAM="${1:-10.0.0.169}"
DURATION="${2:-3600}"
MITM_DIR="${3:-auto}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
mkdir -p "$ROOT/captures"

TS="$(date -u +%Y%m%dT%H%M%SZ)"
LOG="${BEACON_LOG:-$ROOT/captures/beacon-listener-$TS.log}"
LOCK="/tmp/beacon-listener.lock"

# ── single-instance guard ────────────────────────────────────────────────────
exec 9>"$LOCK"
if ! flock -n 9; then
  echo "another beacon-listener is running — skipping" >&2
  exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
  echo "!! must run as root (tcpdump)" >&2
  exit 2
fi

if [ "$MITM_DIR" = "auto" ]; then
  MITM_DIR="$(ls -dt "$ROOT/captures"/eseecloud-mitm-20* 2>/dev/null | head -1)"
fi
BETTERCAP_LOG=""
if [ -n "${MITM_DIR:-}" ] && [ "$MITM_DIR" != "none" ] && [ -f "${MITM_DIR%/}/bettercap.log" ]; then
  # Freshness guard: only tail a LIVE MITM session. A completed session's
  # bettercap.log is frozen — tail -n 0 -F on it collects zero flaps, so the
  # banner would claim flap-tracking that isn't happening. Skip if the log
  # hasn't been written in the last 5 minutes.
  if [ "$(($(date +%s) - $(stat -c %Y "${MITM_DIR%/}/bettercap.log"))) " -le 300 ]; then
    BETTERCAP_LOG="${MITM_DIR%/}/bettercap.log"
  fi
fi

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ═══ beacon listener start cam=$CAM duration=${DURATION}s flaps=${BETTERCAP_LOG:-off} ═══" | tee -a "$LOG"

# ── beacon capture ───────────────────────────────────────────────────────────
# tcpdump -tt prints epoch-with-microseconds, so the correlator gets precise
# inter-emission intervals regardless of host timezone. Filter: any camera UDP
# to the local broadcast address (catches the :8002 and :18002 beacons and any
# other discovery shape — dst ports vary: 8002, 33748, 37892, ...).
tcpdump -i any -nn -tt -l "src host $CAM and udp and dst host 255.255.255.255" \
  >> "$LOG" 2>/dev/null &
TCPDUMP_PID=$!

# ── flap watcher ─────────────────────────────────────────────────────────────
# tail -F so a live MITM's flaps stream in; stamp each on receipt with UTC so
# both timelines share the same clock reference (bettercap only prints local
# HH:MM:SS, which has no date and would be ambiguous across midnight).
FLAP_PID=""
if [ -n "$BETTERCAP_LOG" ]; then
  (
    tail -n 0 -F "$BETTERCAP_LOG" 2>/dev/null \
      | grep --line-buffered 'gateway.change' \
      | while IFS= read -r line; do
          echo "FLAP $(date -u '+%Y-%m-%dT%H:%M:%SZ') $line" >> "$LOG"
        done
  ) &
  FLAP_PID=$!
fi

cleanup() {
  kill "$TCPDUMP_PID" 2>/dev/null || true
  [ -n "$FLAP_PID" ] && kill "$FLAP_PID" 2>/dev/null || true
}
trap cleanup INT TERM EXIT

sleep "$DURATION"
cleanup
wait 2>/dev/null || true
trap - INT TERM EXIT

echo "═══════════════════════════════════════════════════════════" | tee -a "$LOG"
echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ═══ correlation ═══" | tee -a "$LOG"

# ── correlation ──────────────────────────────────────────────────────────────
python3 - "$LOG" "$CAM" <<'PY' | tee -a "$LOG"
import re, statistics, sys
from datetime import datetime, timezone

log, cam = sys.argv[1], sys.argv[2]

# BEACON: tcpdump -tt lines: "<epoch.micro> [iface] [dir] IP <cam>.<sport> > 255.255.255.255.<dport>: UDP, length <n>"
# FLAP:   "FLAP <ISO-UTC> [HH:MM:SS] [gateway.change] ..."
# Note: on -i any (LINUX_SLL2) tcpdump inserts the interface name + direction
# between the timestamp and "IP" (e.g. "1786... wlp5s0 B   IP 10.0.0.169..."),
# so the regex allows 0-2 tokens before "IP".
beacon_re = re.compile(
    r'^(\d+\.\d+)\s+(?:\S+\s+){0,2}IP\s+\S+\.(\d+)\s+>\s+255\.255\.255\.255\.(\d+):\s+UDP,\s+length\s+(\d+)')
flap_re = re.compile(r'^FLAP\s+(\S+)')

beacons, flaps = [], []
for line in open(log, errors="replace"):
    m = beacon_re.match(line)
    if m:
        epoch = float(m.group(1))
        beacons.append((epoch, int(m.group(2)), int(m.group(3)), int(m.group(4))))
        continue
    m = flap_re.match(line)
    if m:
        try:
            flaps.append(datetime.fromisoformat(m.group(1).replace('Z', '+00:00')).timestamp())
        except ValueError:
            pass

def iso(epoch):
    return datetime.fromtimestamp(epoch, tz=timezone.utc).strftime('%H:%M:%S')

# Group emissions: the :8002 + :18002 pair fires ~8ms apart = ONE emission.
emissions = []
for e, sport, dport, ln in beacons:
    if emissions and e - emissions[-1][0] < 2.0:
        emissions[-1][1].append((sport, dport, ln))
    else:
        emissions.append([e, [(sport, dport, ln)]])

print(f"beacons captured: {len(beacons)}  emissions (pairs grouped): {len(emissions)}  flaps: {len(flaps)}")
for e, parts in emissions:
    sp = ','.join(f"{s}:{d}({l}B)" for s, d, l in parts)
    print(f"  emission {iso(e)}Z  [{sp}]")

if len(emissions) >= 2:
    intervals = [b[0] - a[0] for a, b in zip(emissions, emissions[1:])]
    med = statistics.median(intervals)
    spread = max(abs(i - med) for i in intervals) / med if med else 1.0
    print(f"inter-emission intervals (s): {[round(i,1) for i in intervals]}  median={round(med,1)}  max-spread={round(spread*100)}%")
    fixed_timer = spread < 0.15
else:
    med, spread, fixed_timer = 0.0, 1.0, False
    print("fewer than 2 emissions — cannot measure cadence")

# flap pairing: a causal trigger must PRECEDE the emission — flaps after it
# are not triggers (an emission 1s before a flap would otherwise count).
if flaps and emissions:
    deltas = []
    for e, _ in emissions:
        prior = [e - f for f in flaps if f <= e]
        deltas.append(min(prior) if prior else None)
    near = sum(1 for d in deltas if d is not None and d <= 3.0)
    print("seconds since nearest PRIOR flap per emission: " +
          ", ".join(f"{iso(e)}Z d={d:.1f}" if d is not None else f"{iso(e)}Z d=none(no prior flap)" for (e, _), d in zip(emissions, deltas)))
    flap_triggered = near == len(emissions) and not fixed_timer
else:
    near, flap_triggered = 0, False

print("── verdict ──")
if fixed_timer:
    print(f"FIXED-TIMER: median interval ~{med:.0f}s (~{med/60:.1f} min), all within ±{round(spread*100)}% — beacons independent of ARP spoofing")
elif flap_triggered:
    print(f"FLAP-TRIGGERED: {near}/{len(emissions)} emissions within 3s of a PRIOR gateway flap, intervals irregular — beacons fire when spoofing changes state")
elif not emissions:
    print(f"NO BEACONS OBSERVED in window — camera is not broadcasting (gate still closed / beacon timer dormant)")
else:
    print("OBSERVATIONAL: beacons present but not clearly timer- or flap-locked — see raw data above")
    if flaps:
        print(f"  {near}/{len(emissions)} emissions within 3s of a PRIOR flap")
PY

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ═══ beacon listener done — log: $LOG ═══" | tee -a "$LOG"
exit 0
