#!/usr/bin/env bash
# ── recovery-watchdog.sh — autonomous camera-recovery watchdog ──────────────
#
# Watches the BossCamSuite camera-recovery machinery and pages the operator
# when something silently stalls:
#
#   1. WORKER STALENESS — the CameraRecoveryAutoWorker records LastScanAtUtc on
#      every cycle (waiting, no-APs, cooldown, auto-starting). If that timestamp
#      stops advancing for STALE_MINUTES (default 5), the worker died or hung —
#      no factory-reset camera will ever be recovered again.
#   2. STUCK CAMERA AP — a factory-reset camera AP (IPCZ7C34…) that stays
#      visible for STUCK_MINUTES (default 60) with no successful recovery. A
#      successful recovery makes the camera rejoin the LAN and its AP disappear;
#      an AP that lingers past the threshold means recovery keeps failing (or
#      never runs). Per-AP first-seen is persisted across ticks in a state file,
#      so the 60-minute clock survives systemd timer restarts. A single missed
#      scan (transient RF blip, rescan in progress) does NOT reset the clock —
#      tracked APs are kept for GRACE_MINUTES (default 15) after their last
#      sighting, and only dropped when absent beyond that.
#   3. SERVICE DOWN — the whole suite is unreachable on $API. No service means
#      no worker, no scan, no recovery: page once, then resolve when it returns.
#
# Each condition pages ONCE (per-AP / per-condition flags in the state file) and
# logs a RESOLVED line when the condition clears, so a stuck hotspot never goes
# unnoticed but a healthy box does not spam.
#
# Run: driven by scripts/install-recovery-watchdog.sh (systemd timer, every 5m).
# Manual one-shot:  ./scripts/recovery-watchdog.sh
#
# Env (all optional):
#   API=...             BossCam API base URL        (default http://127.0.0.1:5317)#   STALE_MINUTES=...   worker-staleness threshold  (default 5)
#   STUCK_MINUTES=...    stuck-AP threshold          (default 60)
#   GRACE_MINUTES=...    AP-absent grace before the first-seen clock is
#                        dropped (default 15; a blip shorter than this does
#                        not reset a stuck AP's clock)
#   NOTIFY_CMD=...      paging command; SINGLE executable path, receives the
#                       one-line alert summary as its first argument (same
#                       contract as flip-count-alert.sh / watch-nonce-flips.sh)
#   NO_BELL=1           suppress the terminal bell in terminal mode
#   LOG=...             rolling tick log            (default <repo>/local-camera-recovery/recovery-watchdog.log)
#   ALERT_LOG=...       append-only alert log       (default <repo>/local-camera-recovery/recovery-watchdog-alerts.log)
#   STATE=...           per-AP first-seen state     (default <repo>/local-camera-recovery/recovery-watchdog.state)
#
# Alert channel ladder: NOTIFY_CMD, else notify-send (when a display is
# present), else terminal bell + stderr banner — identical to the fleet's other
# alert scripts, so one NOTIFY_CMD config pages all monitors.

set -u
set -o pipefail

API="${API:-http://127.0.0.1:5317}"
STALE_MINUTES="${STALE_MINUTES:-5}"
STUCK_MINUTES="${STUCK_MINUTES:-60}"
GRACE_MINUTES="${GRACE_MINUTES:-15}"
NOTIFY_CMD="${NOTIFY_CMD:-}"
NO_BELL="${NO_BELL:-0}"

# Make sure VISIBLE is defined before the eval loop references it (set -u).
VISIBLE=""

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
# NOTE: LOG/ALERT_LOG/STATE reference $ROOT, so assign AFTER ROOT= (set -u).
mkdir -p "$ROOT/local-camera-recovery" 2>/dev/null || true
LOG="${LOG:-$ROOT/local-camera-recovery/recovery-watchdog.log}"
ALERT_LOG="${ALERT_LOG:-$ROOT/local-camera-recovery/recovery-watchdog-alerts.log}"
STATE="${STATE:-$ROOT/local-camera-recovery/recovery-watchdog.state}"

LOCK="/tmp/recovery-watchdog.lock"
exec 9>"$LOCK"
if ! flock -n 9; then
  echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') another watchdog tick still active — skipping" >> "$LOG" 2>/dev/null || true
  exit 0
fi

# ── helpers ──────────────────────────────────────────────────────────────────

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }

now_epoch() { date -u +%s; }

# iso_epoch <ISO8601> — convert a .NET DateTimeOffset string to epoch seconds.
# GNU date handles both "…Z" and "…+00:00". Unknown input -> 0 (caller guards).
iso_epoch() {
  date -u -d "$1" +%s 2>/dev/null || echo 0
}

# json_get <json> <dotted.key> — extract a scalar field. Prints "" on any
# failure (bad json, missing key, non-scalar). Repo convention uses python3.
json_get() {
  python3 -c '
import json, sys
try:
    d = json.loads(sys.stdin.read())
except Exception:
    sys.exit(1)
def walk(d, path):
    for p in path.split("."):
        if not isinstance(d, dict) or p not in d:
            return ""
        d = d[p]
    return d if isinstance(d, (str, int, float, bool)) else ""
print(walk(d, sys.argv[1]))
' "$2" <<< "$1"
}

# json_ssids <json> — one visible camera AP SSID per line ("" on failure).
json_ssids() {
  python3 -c '
import json, sys
try:
    d = json.loads(sys.stdin.read())
except Exception:
    sys.exit(1)
for ap in (d.get("aps", []) if isinstance(d, dict) else []):
    print(ap.get("ssid", ""))
' <<< "$1"
}

# page_operator <one-line-summary> — NOTIFY_CMD -> notify-send -> bell+stderr.
page_operator() {
  local msg="$1"
  if [ -n "$NOTIFY_CMD" ]; then
    $NOTIFY_CMD "$msg" || true
  elif command -v notify-send >/dev/null 2>&1 \
       && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    notify-send -u critical -a BossCamSuite "★ Camera-recovery watchdog ★" "$msg" >/dev/null 2>&1 || true
  else
    [ "$NO_BELL" = "1" ] || printf '\a' >&2
    echo "*** RECOVERY WATCHDOG *** $msg" >&2
  fi
}

# log <message> — append to the rolling tick log (best-effort, never fatal).
log() { echo "$(ts) $1" >> "$LOG" 2>/dev/null || true; }

# alert <summary> — page once + append to the alert log.
alert() {
  local summary="$1"
  echo "$(ts) ALERT $summary" >> "$ALERT_LOG" 2>/dev/null || true
  page_operator "$summary"
}

# set_flag <name> <value> — atomically upsert a state flag line.
set_flag() {
  local name="$1" value="$2" tmp
  tmp="${STATE}.tmp"
  grep -v "^$name=" "$STATE" 2>/dev/null > "$tmp" || true
  printf '%s=%s\n' "$name" "$value" >> "$tmp"
  mv "$tmp" "$STATE"
}

# get_flag <name> [default] — print a state flag value ("" when absent).
get_flag() {
  local name="$1" def="${2:-}"
  grep "^$name=" "$STATE" 2>/dev/null | tail -1 | cut -d= -f2- || printf '%s' "$def"
}

# ── 1. service reachability ─────────────────────────────────────────────────
AUTO_STATUS=$(curl -sS -m 10 -w '\n%{http_code}' "$API/api/recovery/auto/status" 2>/dev/null || true)
HTTP_CODE=$(printf '%s' "$AUTO_STATUS" | tail -1)
JSON_BODY=$(printf '%s' "$AUTO_STATUS" | sed '$d')
if [ "$HTTP_CODE" != "200" ] || [ -z "$JSON_BODY" ]; then
  if [ "$(get_flag service_alerted 0)" != "1" ]; then
    alert "BossCam service unreachable on $API (HTTP ${HTTP_CODE:-none}) — no camera recovery can run"
    set_flag service_alerted 1
  else
    log "service still unreachable on $API — alert already sent"
  fi
  exit 0
fi
if [ "$(get_flag service_alerted 0)" = "1" ]; then
  log "RESOLVED service reachable again on $API"
  echo "$(ts) RESOLVED service reachable again on $API" >> "$ALERT_LOG" 2>/dev/null || true
  set_flag service_alerted 0
fi

# ── 2. worker staleness ─────────────────────────────────────────────────────
ENABLED=$(json_get "$JSON_BODY" "enabled")
if [ "$(printf '%s' "$ENABLED" | tr 'A-Z' 'a-z')" != "true" ]; then
  log "auto-recovery worker DISABLED (RecoveryAutoScanEnabled=false) — staleness check skipped"
  set_flag stale_alerted 0
else
  LAST_SCAN=$(json_get "$JSON_BODY" "lastScanAtUtc")
  NOW=$(now_epoch)
  # default(DateTimeOffset) serializes as year 0001 — a never-scanned worker.
  # Match the raw prefix precisely: never alert on it, wait for the first tick.
  case "$LAST_SCAN" in
    0001-*)
      log "worker enabled but lastScanAtUtc is unset (never scanned yet) — waiting"
      ;;
    *)
      SCAN_EPOCH=$(iso_epoch "$LAST_SCAN")
      if [ "$SCAN_EPOCH" -eq 0 ]; then
        log "could not parse lastScanAtUtc '$LAST_SCAN' — staleness check skipped"
      else
        STALE=$(( NOW - SCAN_EPOCH ))
        if [ "$STALE" -gt $(( STALE_MINUTES * 60 )) ]; then
          if [ "$(get_flag stale_alerted 0)" != "1" ]; then
            alert "auto-recovery worker STALE: no scan for $(( STALE / 60 ))m (threshold ${STALE_MINUTES}m) — worker died/hung?"
            set_flag stale_alerted 1
          else
            log "worker still stale ($(( STALE / 60 ))m since last scan) — alert already sent"
          fi
        else
          if [ "$(get_flag stale_alerted 0)" = "1" ]; then
            log "RESOLVED worker scanning again (last scan $(( STALE / 60 ))m ago)"
            echo "$(ts) RESOLVED worker scanning again" >> "$ALERT_LOG" 2>/dev/null || true
          fi
          set_flag stale_alerted 0
        fi
      fi
      ;;
  esac
fi

# ── 3. stuck camera APs ─────────────────────────────────────────────────────
# NB: -m 20 on the scan probe — /api/recovery/scan triggers a LIVE WiFi scan
# that can exceed 10s on a busy 2.4 GHz band; the worker-staleness probe above
# (a pure status read) keeps the shorter -m 10 bound.
SCAN=$(curl -sS -m 20 -w '\n%{http_code}' "$API/api/recovery/scan" 2>/dev/null || true)
SCAN_HTTP=$(printf '%s' "$SCAN" | tail -1)
SCAN_BODY=$(printf '%s' "$SCAN" | sed '$d')
if [ "$SCAN_HTTP" != "200" ] || [ -z "$SCAN_BODY" ]; then
  log "scan endpoint unreachable (HTTP ${SCAN_HTTP:-none}) — stuck-AP check skipped this tick"
else
  NOW=$(now_epoch)
  THRESHOLD=$(( STUCK_MINUTES * 60 ))
  GRACE=$(( GRACE_MINUTES * 60 ))

  # visible set: SSID per line (SSIDs are IPCZ7C34… alphanumeric)
  VISIBLE=$(json_ssids "$SCAN_BODY")
  VISIBLE_CSV=$(printf '%s' "$VISIBLE" | paste -sd, -)

  # awk fails on a missing input file, and the staleness section may not have
  # created STATE yet (e.g. worker enabled but never scanned, or freshly
  # deployed). touch ensures the END block can insert first-seen rows for
  # brand-new APs on the very first tick instead of silently dropping them.
  touch "$STATE" 2>/dev/null || true

  # One awk pass over the state file: refresh last= for visible APs (KEEPING
  # first= and alerted= flags — a clobber here would re-alert every tick and
  # lose the service/stale flags), KEEP rows for APs absent less than GRACE
  # (so a transient scan blip does not reset a stuck AP's 60m clock — the
  # reviewer-flagged case where a flickering AP evades the alert forever),
  # drop rows absent beyond GRACE (their camera either recovered or powered
  # off), and insert first-seen rows for brand-new APs. Flag lines
  # (service_alerted=, stale_alerted=) pass through untouched.
  NEXT="${STATE}.next"
  EVICTED="${STATE}.evicted"
  : > "$EVICTED"
  # Only replace the state file when awk actually succeeded: a failed awk must
  # never wipe the alert flags (a partial/empty NEXT is discarded). Empty output
  # on success (all tracked APs vanished past grace) IS a legitimate replacement.
  if awk -v now="$NOW" -v grace="$GRACE" -v csv="$VISIBLE_CSV" -v evicted="$EVICTED" '
    BEGIN { n = split(csv, v, ","); for (i = 1; i <= n; i++) if (v[i] != "") vis[v[i]] = 1 }
    /^ap=/ {
      ssid = substr($1, 4)
      first = ""; alerted = ""; last = ""
      for (i = 2; i <= NF; i++) {
        # NB offsets: "first="=6 chars (value at 7), "alerted="=8 (value at 9),
        # "last="=5 (value at 6). Getting these wrong silently corrupts the
        # grace clock — a smoke test caught last= parsed one digit short, which
        # made every kept AP look ancient and drop immediately.
        if ($i ~ /^first=/)   first   = substr($i, 7)
        if ($i ~ /^alerted=/) alerted = substr($i, 9)
        if ($i ~ /^last=/)    last    = substr($i, 6)
      }
      if (!(ssid in vis)) {
        # Absent this tick. Keep within grace (last sighting fresh enough that
        # this is likely a scan blip, not a recovery); drop beyond grace.
        # An evicted row that had alerted=1 means a previously-paged stuck AP
        # is gone — log the closure on the eviction path here so the operator
        # sees RESOLVED in the same log they saw the page (see below).
        if (last != "" && (now - last) <= grace) { print; tracked[ssid] = 1; next }
        # A previously-paged stuck AP is gone (recovered or powered off):
        # record the closure so the tick logs a RESOLVED line in the same log
        # the operator saw the page. Written to a side file (not stderr — the
        # awk call redirects stderr to /dev/null) and consumed after the state
        # swap below.
        if (alerted == "1") print ssid > evicted
        next
      }
      out = "ap=" ssid " first=" first " last=" now
      if (alerted != "") out = out " alerted=" alerted
      print out
      tracked[ssid] = 1
      next
    }
    { print }                       # flag lines pass through
    END {
      for (ssid in vis) if (!(ssid in tracked)) print "ap=" ssid " first=" now " last=" now
    }
  ' "$STATE" > "$NEXT" 2>/dev/null; then
    mv "$NEXT" "$STATE"
    # Log RESOLVED for every alerted AP that was just evicted — the stuck
    # hotspot cleared (camera recovered and rejoined the LAN, or powered off).
    if [ -s "$EVICTED" ]; then
      while IFS= read -r ssid; do
        [ -n "$ssid" ] || continue
        log "RESOLVED camera AP $ssid no longer visible (recovered?)"
        echo "$(ts) RESOLVED camera AP $ssid no longer visible (recovered?)" >> "$ALERT_LOG" 2>/dev/null || true
      done < "$EVICTED"
    fi
  else
    rm -f "$NEXT"
  fi
  rm -f "$EVICTED"

  # Evaluate every tracked AP: stuck when now - first > threshold AND not yet
  # alerted. Writes the updated file back, preserving first/last/alerted.
  if [ -f "$STATE" ]; then
    AP_TMP="${STATE}.eval"
    : > "$AP_TMP"
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      case "$line" in
        ap=*)
          ssid=$(printf '%s' "$line" | awk '{print $1}' | cut -d= -f2)
          first=$(printf '%s' "$line" | awk '{for(i=1;i<=NF;i++) if($i~/^first=/) print substr($i,7)}')
          alerted=$(printf '%s' "$line" | awk '{for(i=1;i<=NF;i++) if($i~/^alerted=/) print substr($i,9)}')
          if [ -n "$first" ]; then
            age=$(( NOW - first ))
            # Alert only when the AP is CURRENTLY visible: an absent-but-within-
            # grace row means the AP just vanished (possibly recovered), so a
            # late "STUCK" page ~60m after the fact would be noise. The clock
            # keeps running in the kept row, so if the AP reappears the alert
            # fires then — exactly "visible for over an hour".
            if [ "$age" -gt "$THRESHOLD" ] \
               && [ "${alerted:-0}" != "1" ] \
               && printf '%s\n' "$VISIBLE" | grep -Fqx "$ssid"; then
              alert "camera AP $ssid STUCK: visible $(( age / 60 ))m (threshold ${STUCK_MINUTES}m) without successful recovery (first seen $(date -u -d "@$first" '+%H:%MZ' 2>/dev/null || echo "?") — recovery failing or never running)"
              printf '%s alerted=1\n' "$(printf '%s' "$line" | sed 's/ alerted=[01]$//')" >> "$AP_TMP"
              continue
            fi
          fi
          printf '%s\n' "$line" >> "$AP_TMP"
          ;;
        *) printf '%s\n' "$line" >> "$AP_TMP" ;;
      esac
    done < "$STATE"
    # Guarded like the awk pass: only replace STATE with a non-empty rewrite,
    # or when STATE was already empty. A mid-loop failure must not clobber the
    # alert flags with a partial write.
    if [ -s "$AP_TMP" ] || [ ! -s "$STATE" ]; then
      mv "$AP_TMP" "$STATE"
    else
      rm -f "$AP_TMP"
    fi
  fi

  AP_COUNT=$(printf '%s' "$VISIBLE" | grep -c . 2>/dev/null || true)
  AP_COUNT=${AP_COUNT:-0}
  log "scan ok: $AP_COUNT camera AP(s) visible"
fi

exit 0
