#!/usr/bin/env bash
# ── watch-nonce-flips.sh — alert watcher for beacon-nonce flips ─────────────
#
# Tails captures/gate-flip-heartbeat.log (the hourly cloud-resurrection
# monitor) and reacts to every beacon-nonce flip. The heartbeat emits a
# ★★★ NONCE FLIP ★★★ line plus a verdict line carrying nonce_flip=YES — the
# flip is the earliest detectable cloud-session resurrection signal (the
# HDS/1.0 UDP beacon flips its 40-hex nonce when the camera's cloud-session
# state changes, ahead of any TCP dialing). A MID-TICK flip (the beacon
# changed DURING a 10-min capture window) is reported by the heartbeat as
# nonce_flip=MIDTICK + nonce_chain="<old>,<new>" and a ★★★ MID-TICK NONCE
# FLIP ★★★ line, and is alerted here exactly like a normal flip. On each
# flip this watcher:
#
#   1. Fires a notification — notify-send (desktop) when a display is
#      present, otherwise a terminal bell + highlighted stderr banner; a
#      custom command can replace both via NOTIFY_CMD.
#   2. Appends a forensics block to captures/gate-flip-nonce-flips.log: the
#      flip line, the old→new nonce (old from the rolling observation
#      window, new from the heartbeat's own verdict line), and every nonce
#      observation for that camera over the preceding WINDOW_HOURS (24 by
#      default) — so a resurrection can be traced tick by tick without
#      grepping the heartbeat log.
#
# The watcher needs NO root — it only reads logs. It is stateless apart from
# the flips log: on startup it re-scans the heartbeat log to rebuild its 24h
# window AND backfills any flips that happened while it was down (deduped by
# the run's gate-flip-<ts> dir, so restarts never double-alert).
#
# With --flip-summary it additionally prints a fleet health digest at startup:
# a per-camera rollup (flip count, first/last event ts, the old→new nonce
# chain, and a 'source pcaps missing' count) of the last SUMMARY_DAYS of flip
# events, read from the STABLE history captures/nonce-flips.log (the
# heartbeat's flip_log() output, seeded historically by
# backfill-nonce-state.sh) — distinct from the forensics log above. A row
# whose pcap= token ends with "(pruned)" cites a source capture that KEEP_DAYS
# removed; the digest counts those per camera so marker-only evidence is
# visible at a glance. Alone it digests then tails; combined with --check it
# digests once and exits.
#
# Trigger contract (matches the heartbeat's documented parse contract):
# the watcher keys on a verdict line carrying nonce_flip=YES or nonce_flip=
# MIDTICK — that line is self-contained (ts, cam, new nonce, dir=) — and
# reconstructs the ★★★ flip line verbatim (a MID-TICK flip additionally
# carries nonce_chain="<old>,<new>", preserved in the forensics block).
# Lines with nonce_flip=first/no/none/off/no-pcap never alert.
#
# Usage:
#   ./scripts/watch-nonce-flips.sh            # scan + tail (foreground)
#   ./scripts/watch-nonce-flips.sh --check    # scan existing log once, backfill
#                                             # missed flips, then exit
#   ./scripts/watch-nonce-flips.sh --flip-summary
#                                             # also print a fleet health digest
#                                             # at startup: per-camera rollup of
#                                             # the last SUMMARY_DAYS (7) of flip
#                                             # events from captures/nonce-flips.log
#   ./scripts/watch-nonce-flips.sh --check --flip-summary
#                                             # one-shot: backfill + digest, exit
#   Env:
#     HEARTBEAT_LOG=...  heartbeat log to watch
#                        (default captures/gate-flip-heartbeat.log)
#     FLIPS_LOG=...      forensics log to append
#                        (default captures/gate-flip-nonce-flips.log)
#     HISTORY_LOG=...    stable flip-event history read by --flip-summary
#                        (default captures/nonce-flips.log)
#     SUMMARY_DAYS=N     --flip-summary window in days (default 7)
#     WINDOW_HOURS=N     forensics window (default 24)
#     NOTIFY_CMD=...     custom notification command; must be a SINGLE
#                        executable path, receives a one-line summary as its
#                        first argument (wrap multi-arg commands like curl to
#                        ntfy.sh / Slack in a small script) — replaces
#                        notify-send + bell
#     NO_BELL=1          suppress the terminal bell in terminal mode
#
# Suggested install: run alongside the heartbeat under the same systemd timer
# (see scripts/install-gate-flip-heartbeat.sh), e.g. a tiny .service whose
# ExecStart is this script; watch its stdout/stderr in the journal.

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
LOG="${HEARTBEAT_LOG:-$ROOT/captures/gate-flip-heartbeat.log}"
FLIPS_LOG="${FLIPS_LOG:-$ROOT/captures/gate-flip-nonce-flips.log}"
# NOTE: HISTORY_LOG also references $ROOT, so it must be assigned after ROOT=
HISTORY_LOG="${HISTORY_LOG:-$ROOT/captures/nonce-flips.log}"
WINDOW_HOURS="${WINDOW_HOURS:-24}"
SUMMARY_DAYS="${SUMMARY_DAYS:-7}"
NOTIFY_CMD="${NOTIFY_CMD:-}"
NO_BELL="${NO_BELL:-0}"

MODE=tail
FLIP_SUMMARY=0
for arg in "$@"; do
  case "$arg" in
    -h|--help) awk 'NR>1 && /^[^#]/{exit} NR>1{print}' "$0"; exit 0 ;;
    --check)        MODE=check ;;
    --flip-summary) FLIP_SUMMARY=1 ;;
    *) echo "!! unknown argument: $arg (see --help)" >&2; exit 2 ;;
  esac
done

[ -f "$LOG" ] || { echo "!! heartbeat log not found: $LOG" >&2; exit 2; }
mkdir -p "$(dirname "$FLIPS_LOG")"
touch "$FLIPS_LOG"

FLIP_WRITES=0

# ── rolling nonce-observation window (newest last) ──────────────────────────
# Entries: "epoch|iso-ts|cam|nonce". Pruned to the last WINDOW_HOURS on every
# record. Built from the heartbeat log at startup; appended per verdict line
# so the "preceding 24h" listing in a flip block is always chronological.
WINDOW=()

window_prune() {
  # Keep only entries within WINDOW_HOURS of the NEWEST observation (not of
  # wall-clock now) so a backfilled flip's forensics listing shows the 24h
  # BEFORE the flip even when the scan runs hours later.
  local newest max out=() e
  [ "${#WINDOW[@]}" -gt 0 ] || return 0
  newest="${WINDOW[${#WINDOW[@]} - 1]}"
  newest="${newest%%|*}"
  max=$(( newest - WINDOW_HOURS * 3600 ))
  for e in "${WINDOW[@]:-}"; do
    [ -z "$e" ] && continue
    [ "${e%%|*}" -ge "$max" ] && out+=("$e")
  done
  if [ "${#out[@]}" -gt 0 ]; then
    WINDOW=("${out[@]}")
  else
    WINDOW=()
  fi
}

window_record() {
  # $1 = iso ts, $2 = cam, $3 = nonce
  local ep
  ep=$(date -u -d "$1" +%s 2>/dev/null) || ep=$(date +%s)
  WINDOW+=("$ep|$1|$2|$3")
  window_prune
}

window_last() {
  # $1 = cam -> echoes the newest window entry for that cam (empty if none)
  # Entry format: epoch|iso-ts|cam|nonce — cam is field 3.
  local i e cam
  for (( i = ${#WINDOW[@]} - 1; i >= 0; i-- )); do
    e="${WINDOW[$i]}"
    cam="${e#*|}"; cam="${cam#*|}"; cam="${cam%%|*}"
    if [ "$cam" = "$1" ]; then
      printf '%s\n' "$e"
      return 0
    fi
  done
  return 1
}

# ── notification ─────────────────────────────────────────────────────────────
notify() {
  # $1 = cam, $2 = old nonce (may be empty), $3 = new nonce, $4 = dir,
  # $5 = kind (NONCE FLIP | MID-TICK NONCE FLIP)
  local cam="$1" oldn="$2" newn="$3" dir="$4" kind="${5:-NONCE FLIP}"
  local short_old short_new body
  short_old=$(printf '%s' "$oldn" | cut -c1-8); [ -n "$short_old" ] || short_old="unknown"
  short_new=$(printf '%s' "$newn" | cut -c1-8)
  body="$kind cam=$cam  $short_old→$short_new (UDP beacon session-state change, $dir)"
  if [ -n "$NOTIFY_CMD" ]; then
    $NOTIFY_CMD "$body" || true
  elif command -v notify-send >/dev/null 2>&1 \
       && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    notify-send -u critical -a BossCamSuite "★★ $kind ★★" "$body" >/dev/null 2>&1 || true
  else
    [ "$NO_BELL" = "1" ] || printf '\a' >&2
    echo "*** ALERT *** $kind $body" >&2
  fi
}

# ── forensics block ──────────────────────────────────────────────────────────
write_flip_block() {
  # $1 = iso ts, $2 = cam, $3 = old nonce (may be empty), $4 = old ts,
  # $5 = new nonce, $6 = dir, $7 = kind (NONCE FLIP | MID-TICK NONCE FLIP),
  # $8 = nonce chain for a MID-TICK flip ("<old>,<new>", may be empty)
  local ts="$1" cam="$2" oldn="$3" oldts="$4" newn="$5" dir="$6"
  local kind="${7:-NONCE FLIP}" chain="${8:-}"
  local key raw sep e e_cam e_ts e_n found
  key="$dir"
  # Dedup by run dir: a flip is recorded exactly once even if the watcher
  # restarts and re-scans the log (backfill) — never double-alert.
  grep -qF "  key: $key" "$FLIPS_LOG" 2>/dev/null && return 0
  if [ "$kind" = "MID-TICK NONCE FLIP" ] && [ -n "$chain" ]; then
    raw="★★★ $cam MID-TICK NONCE FLIP — beacon changed during capture ($chain, see $dir) ★★★"
  else
    raw="★★★ $cam NONCE FLIP — CLOUD-SESSION STATE CHANGED (UDP beacon, see $dir) ★★★"
  fi
  sep=$(printf '%*s' 78 '' | tr ' ' '═')
  if { cat <<EOF
$sep
★ $ts  $kind  cam=$cam
  old nonce: ${oldn:-(none prior in the ${WINDOW_HOURS}h window)}${oldts:+   (last observed $oldts)}
  new nonce: $newn
  source: $dir
  key: $key
  flip line: $raw

  preceding ${WINDOW_HOURS}h of nonce observations for $cam:
EOF
    found=0
    for e in "${WINDOW[@]:-}"; do
      e_cam="${e#*|}"; e_cam="${e_cam#*|}"; e_cam="${e_cam%%|*}"
      [ "$e_cam" = "$cam" ] || continue
      e_ts=$(printf '%s\n' "$e" | cut -d'|' -f2)
      e_n=$(printf '%s\n' "$e" | cut -d'|' -f4)
      printf '    %s  %s\n' "$e_ts" "$e_n"
      found=1
    done
    [ "$found" = "1" ] || echo "    (none)"
    echo ""
    echo "$sep"
    echo ""
  } >> "$FLIPS_LOG"; then
    FLIP_WRITES=$((FLIP_WRITES + 1))
    echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] ALERT: $kind cam=$cam $(printf '%s' "$oldn" | cut -c1-8)→$(printf '%s' "$newn" | cut -c1-8) (see $dir) — appended to $FLIPS_LOG"
  else
    echo "!! cannot append to $FLIPS_LOG" >&2
    echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] ALERT: $kind cam=$cam $(printf '%s' "$oldn" | cut -c1-8)→$(printf '%s' "$newn" | cut -c1-8) — FLIPS LOG WRITE FAILED" >&2
  fi
  notify "$cam" "$oldn" "$newn" "$dir" "$kind"
}

# ── per-line processing ──────────────────────────────────────────────────────
handle_line() {
  # $1 = a heartbeat log line. Verdict lines (with nonce="<40hex>") update the
  # window; one carrying nonce_flip=YES or nonce_flip=MIDTICK triggers the
  # alert + forensics block (using the window state BEFORE this line is
  # recorded, so old != new). A MID-TICK verdict also carries the mid-capture
  # nonce_chain="<old>,<new>" token, preserved in the forensics block.
  local line="$1" ts cam nonce dir chain isflip kind old oldn oldts
  ts="${line%% *}"
  cam=$(printf '%s\n' "$line" | sed -n 's/.*cam=\([0-9.]*\).*/\1/p' | head -1)
  nonce=$(printf '%s\n' "$line" | sed -n 's/.*nonce="\([0-9a-f]\{40\}\)".*/\1/p' | head -1)
  dir=$(printf '%s\n' "$line" | sed -n 's/.*dir=\([^ ]*\).*/\1/p' | head -1)
  chain=$(printf '%s\n' "$line" | sed -n 's/.*nonce_chain="\([^"]*\)".*/\1/p')
  case "$line" in
    *nonce_flip=YES*)     isflip=1; kind="NONCE FLIP" ;;
    *nonce_flip=MIDTICK*) isflip=1; kind="MID-TICK NONCE FLIP" ;;
    *)                    isflip=0 ;;
  esac
  [ -n "$nonce" ] && [ -n "$cam" ] || return 0

  if [ "$isflip" = "1" ] && [ -n "$dir" ]; then
    old=$(window_last "$cam" | tail -1)
    oldn=""; oldts=""
    if [ -n "$old" ]; then
      oldn=$(printf '%s\n' "$old" | cut -d'|' -f4)
      oldts=$(printf '%s\n' "$old" | cut -d'|' -f2)
    fi
    write_flip_block "$ts" "$cam" "$oldn" "$oldts" "$nonce" "$dir" "$kind" "$chain"
  fi
  window_record "$ts" "$cam" "$nonce"
}

trap 'exit 0' INT TERM

# ── fleet health digest (--flip-summary) ────────────────────────────────────
# Reads the STABLE flip history captures/nonce-flips.log (written by the
# heartbeat's flip_log() and seeded historically by backfill-nonce-state.sh —
# distinct from $FLIPS_LOG, the watcher's own forensics log) and prints a
# per-camera rollup of the last SUMMARY_DAYS of flip events: flip count,
# first/last event ts, the old→new nonce chain, and a 'source pcaps missing'
# count — rows whose pcap= token ends with "(pruned)" (the heartbeat pruner
# suffixes that when KEEP_DAYS removes the cited capture). Cameras with zero
# flips in the window are omitted — the digest is a "who flipped" health view.
# Parse contract: one line per event, "<ts> flip cam=<ip> old=<hex> new=<hex>
# kind=... pcap=..." (optionally chain=<a,b> for MID_TICK; pcap may carry a
# trailing "(pruned)" marker) — the exact shape flip_log() writes,
# backfill-nonce-state.sh seeds, and the heartbeat pruner may rewrite.
flip_summary() {
  local hist="$HISTORY_LOG" days="$SUMMARY_DAYS"
  local now cutoff ep ts cam oldn newn pcap ch line
  local -A cnt=() first=() last=() chains=() pruned=()
  local total=0 total_pruned=0 el d n
  now=$(date +%s)
  cutoff=$(( now - days * 86400 ))
  [ -f "$hist" ] || { echo "!! flip history not found: $hist (run backfill-nonce-state.sh first)" >&2; return 1; }
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    # Only flip-event rows: "<ts> flip cam=..."
    case "$line" in
      *" flip cam="*) ;;
      *) continue ;;
    esac
    ts="${line%% *}"
    # Stale rows outside the digest window are excluded; unparseable
    # timestamps are dropped, not treated as "now" (which would wrongly
    # pass the cutoff and pollute the digest).
    ep=$(date -u -d "$ts" +%s 2>/dev/null) || continue
    [ "$ep" -ge "$cutoff" ] || continue
    cam=$(printf '%s\n' "$line" | sed -n 's/.*cam=\([0-9.]*\).*/\1/p' | head -1)
    oldn=$(printf '%s\n' "$line" | sed -n 's/.*old=\([0-9a-f]\{40\}\).*/\1/p' | head -1)
    newn=$(printf '%s\n' "$line" | sed -n 's/.*new=\([0-9a-f]\{40\}\).*/\1/p' | head -1)
    pcap=$(printf '%s\n' "$line" | sed -n 's/.*pcap=\([^ ]*\).*/\1/p' | head -1)
    [ -n "$cam" ] || continue
    cnt["$cam"]=$(( ${cnt["$cam"]:-0} + 1 ))
    total=$(( total + 1 ))
    # Marker-only evidence: the heartbeat pruner suffixes pcap= with
    # "(pruned)" when KEEP_DAYS removes the cited capture. Count per camera.
    # (Literal case pattern — same idiom as flips_log_prune_mark — NOT an
    # extglob, which would flip behavior if extglob were ever enabled.)
    if [ -n "$pcap" ]; then
      case "$pcap" in
        *"(pruned)") pruned["$cam"]=$(( ${pruned["$cam"]:-0} + 1 )); total_pruned=$(( total_pruned + 1 )) ;;
      esac
    fi
    if [ -z "${first["$cam"]:-}" ] || [ "$ts" \< "${first["$cam"]}" ]; then
      first["$cam"]="$ts"
    fi
    if [ -z "${last["$cam"]:-}" ] || [ "$ts" \> "${last["$cam"]}" ]; then
      last["$cam"]="$ts"
    fi
    # Build the old→new nonce chain for this camera: seed with the first old
    # (if present), then append each new nonce that isn't already the tail.
    ch="${chains["$cam"]:-}"
    [ -n "$ch" ] || ch="$oldn"
    if [ "${ch##*,}" != "$newn" ]; then
      [ -n "$ch" ] && ch="$ch,"
      ch="$ch$newn"
    fi
    chains["$cam"]="$ch"
  done < "$hist"
  if [ "$total" -eq 0 ]; then
    echo "fleet flip digest (last ${days}d of $hist): no flip events"
    return 0
  fi
  echo ""
  echo "=== fleet flip digest — last ${days}d ($hist) ==="
  echo "cameras with flips: ${#cnt[@]}  total events: $total  pruned-evidence: $total_pruned"
  for cam in $(printf '%s\n' "${!cnt[@]}" | sort -t. -k1,1n -k2,2n -k3,3n -k4,4n); do
    # Render the chain with 8-char nonce prefixes and → separators.
    d=""
    IFS=, read -r -a el <<< "${chains["$cam"]:-}"
    for n in "${el[@]:-}"; do
      [ -n "$n" ] && d="$d$(printf '%s' "$n" | cut -c1-8)→"
    done
    d="${d%→}"
    printf '  %-12s  %2d flip(s)  %s  →  %s\n' "$cam" "${cnt["$cam"]}" "${first["$cam"]}" "${last["$cam"]}"
    [ -n "$d" ] && printf '      chain: %s\n' "$d"
    if [ "${pruned["$cam"]:-0}" -gt 0 ]; then
      printf '      source pcaps missing: %d (marker-only evidence)\n' "${pruned["$cam"]}"
    fi
  done
}

# ── startup scan (rebuild window + backfill flips missed while down) ────────
while IFS= read -r line; do
  handle_line "$line"
done < "$LOG"

if [ "$FLIP_SUMMARY" = "1" ]; then
  flip_summary
fi

if [ "$MODE" = "check" ]; then
  echo "check complete — window holds ${#WINDOW[@]} nonce observations; $FLIP_WRITES flip(s) written to $FLIPS_LOG"
  exit 0
fi

echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] watching $LOG for NONCE FLIP lines (forensics window ${WINDOW_HOURS}h) — flips appended to $FLIPS_LOG"
tail -n 0 -F "$LOG" | while IFS= read -r line; do
  handle_line "$line"
done
