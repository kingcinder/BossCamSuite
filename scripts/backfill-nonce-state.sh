#!/usr/bin/env bash
# ── backfill-nonce-state.sh — rebuild the heartbeat's beacon-nonce state ─────
#
# Scans every historical eseecloud-mitm-<ts>/capture.pcap and, per camera,
# extracts the 40-hex HDS/1.0 beacon nonce using the SAME tcpdump filter the
# heartbeat's nonce_watch() uses. Outputs:
#
#   1. The full historical per-camera nonce timeline (session ts, cam, nonce)
#      built from all pcaps — a tick-by-tick record of every beacon nonce the
#      campaign observed (Aug 8-10).
#   2. An updated captures/gate-flip-heartbeat-nonce.state containing the LAST
#      nonce observed per camera, so the heartbeat's next tick compares
#      against the real historical values instead of starting from
#      nonce_flip=first.
#   3. Every historical flip event (each old→new nonce transition in the
#      timeline) seeded into captures/nonce-flips.log (FLIPS_LOG) in the
#      heartbeat's flip_log() line format, so the machine-readable
#      resurrection history is complete from the start — e.g. .169's 12:01Z
#      flip (2ce84978...->8671353c...) and .29's five flips across Aug 8-9.
#      Deduped by exact line: re-runs are idempotent and never duplicate rows
#      the heartbeat already wrote live. Seeded rows carry the session ts of
#      the first new-nonce capture (live heartbeat rows carry detection time).
#      The heartbeat pruner may later suffix a seeded row's pcap= with
#      (pruned) once KEEP_DAYS removes the source capture — the dedup also
#      accepts that marked form, so a re-run after pruning never re-seeds a
#      duplicate unmarked row.
#
# This is an on-demand backfill, not run by the heartbeat itself; re-run it
# any time a batch of new captures appears (before a heartbeat reinstall, or
# after copying pcaps between hosts). Reading pcaps needs no root; WRITING
# the state / flip-history files does, because captures/ is root-owned (the
# heartbeat runs as root via systemd).
#
# Usage:
#   ./scripts/backfill-nonce-state.sh            # timeline + write state + seed flips
#   ./scripts/backfill-nonce-state.sh --print    # timeline only, no writes
#   Env:
#     NONCE_STATE=...  state file to write
#                      (default captures/gate-flip-heartbeat-nonce.state)
#     FLIPS_LOG=...    flip-event history to seed (default
#                      captures/nonce-flips.log)
#     CAMS=...         cameras to scan (default 10.0.0.169 10.0.0.227 10.0.0.29)

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
STATE="${NONCE_STATE:-$ROOT/captures/gate-flip-heartbeat-nonce.state}"
# NOTE: FLIPS_LOG also references $ROOT, so it must be assigned after ROOT=
FLIPS_LOG="${FLIPS_LOG:-$ROOT/captures/nonce-flips.log}"
CAMS="${CAMS:-10.0.0.169 10.0.0.227 10.0.0.29}"
WRITE_STATE=1
case "${1:-}" in
  --print) WRITE_STATE=0 ;;
  -h|--help) awk 'NR>1 && /^[^#]/{exit} NR>1{print}' "$0"; exit 0 ;;
esac

# ── beacon nonce extraction (identical filter to the heartbeat) ─────────────
# The heartbeat's beacon_nonce() takes the FIRST nonce in a pcap (its sessions
# are short, so emissions are uniform). For backfill we take the LAST nonce in
# each pcap: a capture that happens to span a flip ends in the post-flip
# state, which is exactly what the state file must persist.
beacon_nonce_last() {
  # $1 = pcap, $2 = cam -> echoes the last 40-hex nonce (or empty if none)
  tcpdump -r "$1" -nn -A 2>/dev/null \
    "udp and src host $2 and dst host 255.255.255.255" \
    | grep -oE 'nonce=[0-9a-f]{40}' | tail -1 | sed 's/^nonce=//'
}

# session_ts: eseecloud-mitm-20260809T120147Z -> 2026-08-09T12:01:47Z
session_ts() {
  local d
  d=$(basename "$1" | sed 's/^eseecloud-mitm-//')
  # 20260809T120147Z -> 2026-08-09T12:01:47Z
  printf '%s-%s-%sT%s:%s:%sZ' \
    "${d:0:4}" "${d:4:2}" "${d:6:2}" "${d:9:2}" "${d:11:2}" "${d:13:2}"
}

# ── scan all pcaps, build the timeline ──────────────────────────────────────
# Rows: "iso-session-ts|cam|nonce|pcap" (chronological by construction via
# find's sorted dir names, then re-sorted defensively by session ts).
declare -a ROWS=()
declare -a FLIP_EVENTS=()
PCAP_COUNT=0
# Only eseecloud-mitm-<ts>/capture.pcap (the heartbeat's session_pcap() scope):
# a capture.pcap in any other dir (e.g. controlled-verify-*) would not have a
# parseable session timestamp and must not enter the timeline.
for pcap in $(find "$ROOT/captures" -maxdepth 2 -type f \
  -path '*/eseecloud-mitm-*/capture.pcap' 2>/dev/null | sort); do
  PCAP_COUNT=$((PCAP_COUNT + 1))
  ts=$(session_ts "$(dirname "$pcap")")
  for cam in $CAMS; do
    nonce=$(beacon_nonce_last "$pcap" "$cam")
    [ -n "$nonce" ] && ROWS+=("$ts|$cam|$nonce|$pcap")
  done
done

[ "${#ROWS[@]}" -eq 0 ] && { echo "!! no beacon nonces found in any pcap" >&2; exit 1; }
# mapfile (not `IFS=$'\n' ROWS=(...)`) so IFS is left untouched — the summary
# loop below relies on space word-splitting of $CAMS.
mapfile -t ROWS < <(printf '%s\n' "${ROWS[@]}" | sort)

# ── print the timeline ───────────────────────────────────────────────────────
echo "=== beacon-nonce timeline: ${#ROWS[@]} observations across $PCAP_COUNT pcaps ==="
printf '%-21s  %-12s  %s\n' "session" "cam" "nonce"
for row in "${ROWS[@]}"; do
  printf '%s  %-12s  %s\n' "${row%%|*}" "$(printf '%s' "$row" | cut -d'|' -f2)" "$(printf '%s' "$row" | cut -d'|' -f3)"
done

# ── per-camera summary + last known nonce → state file ──────────────────────
# NOTE: captures/ is root-owned (the heartbeat runs as root via systemd), so a
# normal-user run can NEVER write the state file. Open the tmp ONLY when
# actually writing, fail loudly otherwise, and count rows from the tmp we
# wrote — never from $STATE (which is the stale pre-mv file).
if [ "$WRITE_STATE" = "1" ]; then
  mkdir -p "$(dirname "$STATE")"
  if ! : > "$STATE.tmp" 2>/dev/null; then
    echo "!! cannot create $STATE.tmp — captures/ is root-owned (the heartbeat" >&2
    echo "   runs as root via systemd); re-run the write as root:" >&2
    echo "   sudo $(basename "$0")" >&2
    exit 1
  fi
  echo ""
  echo "=== per-camera last nonce (writing to $STATE) ==="
else
  echo ""
  echo "=== per-camera last nonce (dry-run --print) ==="
fi
for cam in $CAMS; do
  last=""
  prev=""
  flips=0
  first_seen=""
  for row in "${ROWS[@]}"; do
    [ "$(printf '%s' "$row" | cut -d'|' -f2)" = "$cam" ] || continue
    ts="${row%%|*}"
    nonce=$(printf '%s' "$row" | cut -d'|' -f3)
    pcap=$(printf '%s' "$row" | cut -d'|' -f4)
    [ -z "$first_seen" ] && first_seen="$ts"
    if [ -n "$prev" ] && [ "$nonce" != "$prev" ]; then
      flips=$((flips + 1))
      # Historical flip event: the session ts + pcap of the FIRST capture
      # showing the new nonce, in the heartbeat's flip_log() line shape.
      FLIP_EVENTS+=("$ts flip cam=$cam old=$prev new=$nonce kind=NONCE_FLIP pcap=$pcap")
    fi
    prev="$nonce"
    last="$nonce"
  done
  if [ -n "$last" ]; then
    if [ "$WRITE_STATE" = "1" ]; then
      printf 'cam=%s %s\n' "$cam" "$last" >> "$STATE.tmp"
    fi
    echo "cam=$cam  last=$last  (${#last} hex, first seen $first_seen, $flips flip(s))"
  else
    echo "cam=$cam  no beacons in any pcap — not written"
  fi
done
if [ "$WRITE_STATE" = "1" ]; then
  if mv "$STATE.tmp" "$STATE"; then
    echo ""
    echo "wrote $(grep -c '^cam=' "$STATE") camera(s) to $STATE"
  else
    echo "!! mv $STATE.tmp -> $STATE failed" >&2
    exit 1
  fi
  # ── seed the flip-event history ──────────────────────────────────────────
  # Append every historical flip to captures/nonce-flips.log in the heartbeat's
  # flip_log() format, deduped by exact line so re-runs are idempotent and
  # never duplicate rows the heartbeat already wrote live. NOTE: seeded rows
  # carry the SESSION ts of the first new-nonce capture, whereas the heartbeat
  # writes live rows at DETECTION (tick) time — the two never collide (exact-
  # line dedup) and the session ts is the right value for historical events.
  mkdir -p "$(dirname "$FLIPS_LOG")"
  seeded=0
  for ev in "${FLIP_EVENTS[@]}"; do
    grep -qxF -- "$ev" "$FLIPS_LOG" 2>/dev/null && continue
    # The heartbeat pruner suffixes pcap= with (pruned) when KEEP_DAYS removes
    # the source capture — accept that marked form as already seeded so a
    # re-run after pruning never duplicates an event.
    ev_marked=$(printf '%s\n' "$ev" | sed 's|pcap=[^ ]*|&(pruned)|')
    grep -qxF -- "$ev_marked" "$FLIPS_LOG" 2>/dev/null && continue
    if printf '%s\n' "$ev" >> "$FLIPS_LOG" 2>/dev/null; then
      seeded=$((seeded + 1))
    else
      echo "!! cannot append $FLIPS_LOG — captures/ is root-owned; re-run as root:" >&2
      echo "   sudo $(basename "$0")" >&2
      exit 1
    fi
  done
  echo "seeded $seeded new flip event(s) into $FLIPS_LOG (${#FLIP_EVENTS[@]} historical flip(s) in timeline)"
else
  echo ""
  echo "--print: state file NOT written"
  if [ "${#FLIP_EVENTS[@]}" -gt 0 ]; then
    echo ""
    echo "would seed ${#FLIP_EVENTS[@]} flip event(s) into $FLIPS_LOG:"
    for ev in "${FLIP_EVENTS[@]}"; do echo "  $ev"; done
  fi
fi
