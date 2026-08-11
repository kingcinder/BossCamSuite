#!/usr/bin/env bash
# ── flip-count-alert.sh — ultra-minimal flip alert around the heartbeat ─────
#
# The one-grep resurrection alert: wraps gate-flip-heartbeat.sh and compares
# the number of flip-event lines in the STABLE history (captures/nonce-flips.log)
# before and after each tick. If the count grew, a NONCE FLIP / MID-TICK flip
# was logged this tick — page the operator. No log parsing, no watcher, no
# tail -F: just `grep -c '^.*flip'` twice and a count comparison.
#
# It intentionally REPLACES the heartbeat in the systemd unit (ExecStart=this
# script, which runs the heartbeat itself), so one unit does both monitoring
# and alerting. Env is forwarded to the heartbeat unchanged
# (HEARTBEAT_DURATION, CAMS, ...), so the heartbeat's config contract is
# identical to running it directly.
#
# Usage:
#   sudo ./scripts/flip-count-alert.sh [heartbeat args...]
#   Env:
#     HEARTBEAT_BIN=...  heartbeat to wrap (default sibling
#                        gate-flip-heartbeat.sh)
#     FLIPS_LOG=...      flip-event history to count (default
#                        captures/nonce-flips.log)
#     ALERT_LOG=...      append-only alert log (default
#                        captures/flip-count-alerts.log)
#     NOTIFY_CMD=...     custom paging command; must be a SINGLE executable
#                        path, receives the one-line alert summary as its
#                        first argument (same contract as watch-nonce-flips.sh)
#     NO_BELL=1          suppress the terminal bell in terminal mode
#
# Alert channel (same ladder as watch-nonce-flips.sh): NOTIFY_CMD, else
# notify-send when a display is present, else terminal bell + stderr banner.
#
# Notes for operators reading $ALERT_LOG: every tick appends a line (delta=0
# when nothing happened — a per-tick audit trail, ~1 line/hour); only a
# delta>0 line pages. This grep-based alert cannot detect a flip whose
# flip_log() append failed inside the heartbeat (the heartbeat's own
# "!! cannot append" path) — only the watcher's forensics log would surface
# that, so a critical flip should still be cross-checked against
# gate-flip-heartbeat.log.
#
# Install: sudo FLIP_ALERT=1 ./scripts/install-gate-flip-heartbeat.sh

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
HEARTBEAT_BIN="${HEARTBEAT_BIN:-$HERE/gate-flip-heartbeat.sh}"
# NOTE: FLIPS_LOG / ALERT_LOG reference $ROOT, so assign after ROOT=
FLIPS_LOG="${FLIPS_LOG:-$ROOT/captures/nonce-flips.log}"
ALERT_LOG="${ALERT_LOG:-$ROOT/captures/flip-count-alerts.log}"
NOTIFY_CMD="${NOTIFY_CMD:-}"
NO_BELL="${NO_BELL:-0}"

[ -x "$HEARTBEAT_BIN" ] || { echo "!! heartbeat not found: $HEARTBEAT_BIN" >&2; exit 2; }
mkdir -p "$(dirname "$ALERT_LOG")"

# ── count flip events (lines containing "flip") ─────────────────────────────
flip_count() {
  local c
  c=$(grep -c '^.*flip' "$FLIPS_LOG" 2>/dev/null)
  c=${c:-0}
  c=${c//[^0-9]/}
  printf '%s\n' "$c"
}

# ── page the operator (same channel ladder as watch-nonce-flips.sh) ─────────
page_operator() {
  # $1 = one-line summary
  local msg="$1"
  if [ -n "$NOTIFY_CMD" ]; then
    $NOTIFY_CMD "$msg" || true
  elif command -v notify-send >/dev/null 2>&1 \
       && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    notify-send -u critical -a BossCamSuite "★ Flip detected ★" "$msg" >/dev/null 2>&1 || true
  else
    [ "$NO_BELL" = "1" ] || printf '\a' >&2
    echo "*** FLIP ALERT *** $msg" >&2
  fi
}

before=$(flip_count)

# Run the heartbeat, forwarding args + env. Capture its exit code so the
# systemd unit sees the same success/failure as running it directly.
"$HEARTBEAT_BIN" "$@"
rc=$?

after=$(flip_count)

ts=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
if [ "$after" -gt "$before" ]; then
  delta=$(( after - before ))
  msg="BossCam flip alert: $delta new flip event(s) in $(basename "$FLIPS_LOG") this tick (before=$before after=$after)"
  printf '%s flip-alert delta=%s before=%s after=%s\n' "$ts" "$delta" "$before" "$after" >> "$ALERT_LOG" || echo "!! cannot append $ALERT_LOG" >&2
  page_operator "$msg"
else
  printf '%s flip-alert delta=0 before=%s after=%s\n' "$ts" "$before" "$after" >> "$ALERT_LOG" || echo "!! cannot append $ALERT_LOG" >&2
fi

exit "$rc"
