#!/usr/bin/env bash
# ── video-feed-watchdog.sh — broken-video-feed watchdog ──────────────────────
#
# Monitors every registered camera's live feed and applies a repair tree to bring
# it back online, paging the operator only when a camera stays dead after the full
# ladder. This is the operational half of the "10.0.0.29 is not displaying"
# troubleshooting: the watchdog encodes the same diagnosis steps a human runs.
#
# Repair tree per tick, per offline camera (escalating):
#   1. PROBE      — POST /api/devices/{id}/probe          (refresh capability/transport state)
#   2. RECONNECT  — POST /api/devices/{id}/connectivity/reconnect  (port fallback + snapshot)
#   3. DIAGNOSE   — POST /api/devices/{id}/connectivity/diagnose   (full diagnostic battery)
#   4. RECORD     — POST /api/recordings/stall-check + start-all    (restart recording jobs)
#   5. HUNT       — POST /api/devices/discover (subnet sweep); if the camera's MAC shows up
#                   at a NEW IP (DHCP renumbering — the exact 10.0.0.29 → 10.0.0.30 case),
#                   re-point it: POST /api/devices/{id}/repoint { ipAddress }
#   6. PAGE       — if still offline after the ladder, page ONCE per outage episode
#                   (state file dedupe) and log RESOLVED when it comes back.
#
# Each step is best-effort and independently guarded so one failing endpoint never
# aborts the tick. Every action is logged to a rolling tick log.
#
# Run: driven by scripts/install-video-feed-watchdog.sh (systemd timer).
# Manual one-shot:  ./scripts/video-feed-watchdog.sh
#
# Env (all optional):
#   API=...             BossCam API base URL        (default http://127.0.0.1:5317)
#   NOTIFY_CMD=...      paging command; SINGLE executable path, receives the
#                       one-line alert summary as its first argument
#   NO_BELL=1           suppress the terminal bell in terminal mode
#   LOG=...             rolling tick log            (default <repo>/local-video-feed/video-feed-watchdog.log)
#   ALERT_LOG=...       append-only alert log       (default <repo>/local-video-feed/video-feed-watchdog-alerts.log)
#   STATE=...           per-device outage state     (default <repo>/local-video-feed/video-feed-watchdog.state)
#   OFFLINE_GRACE=...   offline seconds before the ladder runs  (default 60; avoids
#                       thrash when a camera briefly blips during a scan)
#   MAX_ATTEMPTS=...    ladder attempts per outage episode before paging (default 3)
#   HUNT=0              disable the subnet-sweep hunt/repoint step (default 1 = enabled)

set -u
set -o pipefail

API="${API:-http://127.0.0.1:5317}"
NOTIFY_CMD="${NOTIFY_CMD:-}"
NO_BELL="${NO_BELL:-0}"
OFFLINE_GRACE="${OFFLINE_GRACE:-60}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-3}"
HUNT="${HUNT:-1}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
mkdir -p "$ROOT/local-video-feed" 2>/dev/null || true
LOG="${LOG:-$ROOT/local-video-feed/video-feed-watchdog.log}"
ALERT_LOG="${ALERT_LOG:-$ROOT/local-video-feed/video-feed-watchdog-alerts.log}"
STATE="${STATE:-$ROOT/local-video-feed/video-feed-watchdog.state}"

LOCK="/tmp/video-feed-watchdog.lock"
exec 9>"$LOCK"
if ! flock -n 9; then
  echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') another watchdog tick still active — skipping" >> "$LOG" 2>/dev/null || true
  exit 0
fi

# ── helpers ──────────────────────────────────────────────────────────────────

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }
now_epoch() { date -u +%s; }

# json_get <json> <dotted.key> — extract a scalar field. Prints "" on any failure.
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

# json_objects <json> — one COMPACT JSON OBJECT per line for every element of an
# array (fallback: the "devices"/"items" keys of an object). The API returns a
# single minified array; a while-read over the raw text would treat the whole array
# as one line, so we split into per-device objects first, then json_get each line.
json_objects() {
  python3 -c '
import json, sys
try:
    d = json.loads(sys.stdin.read())
except Exception:
    sys.exit(1)
items = d if isinstance(d, list) else (d.get("devices") or d.get("items") or [])
for it in items if isinstance(items, list) else []:
    if isinstance(it, dict):
        print(json.dumps(it, separators=(",", ":")))
' <<< "$1"
}

# page_operator <one-line-summary> — NOTIFY_CMD -> notify-send -> bell+stderr.
page_operator() {
  local msg="$1"
  if [ -n "$NOTIFY_CMD" ]; then
    $NOTIFY_CMD "$msg" || true
  elif command -v notify-send >/dev/null 2>&1 \
       && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    notify-send -u critical -a BossCamSuite "★ Video-feed watchdog ★" "$msg" >/dev/null 2>&1 || true
  else
    [ "$NO_BELL" = "1" ] || printf '\a' >&2
    echo "*** VIDEO-FEED WATCHDOG *** $msg" >&2
  fi
}

log() { echo "$(ts) $1" >> "$LOG" 2>/dev/null || true; }

alert() {
  local summary="$1"
  echo "$(ts) ALERT $summary" >> "$ALERT_LOG" 2>/dev/null || true
  page_operator "$summary"
}

set_flag() {
  local name="$1" value="$2" tmp
  tmp="${STATE}.tmp"
  grep -v "^$name=" "$STATE" 2>/dev/null > "$tmp" || true
  printf '%s=%s\n' "$name" "$value" >> "$tmp"
  mv "$tmp" "$STATE"
}

get_flag() {
  local name="$1" def="${2:-}"
  grep "^$name=" "$STATE" 2>/dev/null | tail -1 | cut -d= -f2- || printf '%s' "$def"
}

# api_post <path> [timeout] — POST; prints the JSON body, "ERR" on failure.
# Default 25s; slow endpoints (subnet discover, start-all) pass a larger timeout
# so the ladder is not gated on them.
api_post() {
  local path="$1" timeout="${2:-25}"
  local resp
  resp=$(curl -sS -m "$timeout" -X POST "$API$path" 2>/dev/null || true)
  if [ -z "$resp" ]; then echo "ERR"; else printf '%s' "$resp"; fi
}

# repair_ladder <device-json> — run steps 1–5. Prints "online" when the camera is
# reachable again, "offline" when it is still dead.
repair_ladder() {
  local dev="$1"
  local id ip name
  id=$(json_get "$dev" "id")
  ip=$(json_get "$dev" "ipAddress")
  name=$(json_get "$dev" "displayName")
  [ -n "$name" ] || name="$ip"
  [ -n "$id" ] || { echo "offline"; return; }

  log "repair ladder start: $name ($ip) id=$id"

  # 1. Probe — refresh capability/transport state on the stored address.
  api_post "/api/devices/$id/probe" >/dev/null
  # 2. Reconnect — port fallback + fresh connectivity snapshot.
  api_post "/api/devices/$id/connectivity/reconnect" >/dev/null
  # 3. Diagnose — full battery; always returns a report.
  api_post "/api/devices/$id/connectivity/diagnose" >/dev/null

  # Did any of 1–3 make it reachable?
  if device_now_online "$id"; then
    log "repair ladder resolved via probe/reconnect: $name"
    restart_recording
    echo "online"
    return
  fi

  # 4. Restart recording jobs (stall-check auto-restarts dead jobs; start-all is
  #    idempotent and guarantees every registered camera has a continuous job).
  restart_recording

  if device_now_online "$id"; then
    log "repair ladder resolved after recording restart: $name"
    echo "online"
    return
  fi

  # 5. Hunt — subnet sweep; a DHCP-renumbered camera (same MAC, new IP) appears at
  #    a different address, and discovery merges by MAC. Then re-point if needed.
  #    The sweep can exceed 25s on a busy /24, so give it a 90s budget — its body
  #    is discarded anyway; what matters is the merged device list it produces.
  if [ "$HUNT" = "1" ]; then
    log "hunt: running subnet discovery for $name"
    api_post "/api/devices/discover" 90 >/dev/null
    local repointed
    repointed=$(repoint_if_moved "$dev")
    if [ "$repointed" = "repointed" ]; then
      # The record now points at the NEW address, but the stored connectivity
      # snapshot was probed at the OLD one — refresh it before judging online so a
      # successful re-wire is recognized within this same tick.
      log "hunt: re-pointed, refreshing connectivity probe at new address"
      api_post "/api/devices/$id/connectivity/reconnect" 60 >/dev/null
    fi
    if device_now_online "$id"; then
      log "repair ladder resolved via hunt/repoint: $name"
      echo "online"
      return
    fi
  fi

  log "repair ladder finished, still offline: $name ($ip)"
  echo "offline"
}

# device_now_online <id> — true when the stored connectivity snapshot is Healthy/Degraded.
device_now_online() {
  local id="$1"
  local snap
  snap=$(curl -sS -m 15 "$API/api/devices/$id/connectivity" 2>/dev/null || true)
  local status
  status=$(json_get "$snap" "status")
  case "$status" in
    Healthy|Degraded) return 0 ;;
    *) return 1 ;;
  esac
}

# restart_recording — stall-check + idempotent start-all. Never fatal. start-all
# probes EVERY camera and can take tens of seconds (120s budget), so it is guarded
# to run at most ONCE per tick regardless of how many cameras the ladder touches —
# the fleet-wide restart already covers them all.
RECORD_RESTARTED=0
restart_recording() {
  [ "$RECORD_RESTARTED" = "1" ] && return
  RECORD_RESTARTED=1
  api_post "/api/recordings/stall-check" >/dev/null
  api_post "/api/recordings/start-all" 120 >/dev/null
}

# repoint_if_moved <device-json> — if the camera's MAC now lives at another IP in
# the registered fleet, re-point this device record to it (the 10.0.0.29 → new-IP
# case). Prints "repointed" when it re-wired the record, "noop" otherwise, so the
# caller can refresh the connectivity snapshot (which was last probed at the OLD
# address) before judging the camera online.
repoint_if_moved() {
  local dev="$1"
  local id mac
  id=$(json_get "$dev" "id")
  mac=$(json_get "$dev" "macAddress")
  [ -n "$id" ] || { echo "noop"; return; }
  [ -n "$mac" ] || { log "hunt: $id has no MAC — repoint skipped (cannot match by identity)"; echo "noop"; return; }

  local devices
  devices=$(curl -sS -m 15 "$API/api/devices" 2>/dev/null || true)
  local other_id other_ip other_mac
  local line
  # Walk every registered device (one compact object per line via json_objects);
  # find one with the same MAC at a different IP — the DHCP-renumbered twin.
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    other_id=$(json_get "$line" "id")
    other_mac=$(json_get "$line" "macAddress")
    other_ip=$(json_get "$line" "ipAddress")
    if [ "$other_mac" = "$mac" ] && [ "$other_id" != "$id" ] && [ -n "$other_ip" ]; then
      log "hunt: $mac seen at $other_ip (id=$other_id) — re-pointing id=$id"
      local body="{\"ipAddress\":\"$other_ip\"}"
      local resp
      resp=$(curl -sS -m 15 -X POST -H 'Content-Type: application/json' -d "$body" "$API/api/devices/$id/repoint" 2>/dev/null || true)
      if [ -n "$resp" ]; then
        log "hunt: repoint response: $resp"
      fi
      echo "repointed"
      return
    fi
  done <<< "$(json_objects "$devices")"
  echo "noop"
}

# ── 0. service reachability ─────────────────────────────────────────────────
HEALTH=$(curl -sS -m 10 -w '\n%{http_code}' "$API/api/health" 2>/dev/null || true)
HTTP_CODE=$(printf '%s' "$HEALTH" | tail -1)
if [ "$HTTP_CODE" != "200" ]; then
  if [ "$(get_flag service_alerted 0)" != "1" ]; then
    alert "BossCam service unreachable on $API (HTTP ${HTTP_CODE:-none}) — no feed can be watched or repaired"
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

# ── 1. enumerate registered cameras ─────────────────────────────────────────
DEVICES=$(curl -sS -m 15 "$API/api/devices" 2>/dev/null || true)
if [ -z "$DEVICES" ]; then
  log "no device list returned — skipping this tick"
  exit 0
fi

NOW=$(now_epoch)
touch "$STATE" 2>/dev/null || true

# ── 2. per-camera offline detection + repair ladder ─────────────────────────
# json_objects splits the minified array into one compact device object per line,
# so the loop below processes each camera exactly once (id/ip/name via json_get).
declare -A SEEN  # device ids seen this tick (to clear stale outage state)
count=0
while IFS= read -r line; do
  [ -n "$line" ] || continue
  count=$((count + 1))
  id=$(json_get "$line" "id")
  ip=$(json_get "$line" "ipAddress")
  name=$(json_get "$line" "displayName")
  [ -n "$name" ] || name="$ip"
  [ -n "$id" ] || continue
  SEEN["$id"]=1

  # Offline-state flags (per device): first_seen, attempts, alerted.
  key="feed_${id}_"

  if device_now_online "$id"; then
    # Camera is healthy: clear any outage episode state.
    if [ "$(get_flag "${key}alerted" 0)" = "1" ]; then
      log "RESOLVED camera online again: $name ($ip)"
      echo "$(ts) RESOLVED camera online again: $name ($ip)" >> "$ALERT_LOG" 2>/dev/null || true
    fi
    set_flag "${key}first_seen" ""
    set_flag "${key}attempts" "0"
    set_flag "${key}alerted" "0"
    continue
  fi

  # Offline. Grace window: a brief blip (during a scan, camera reboot) does not
  # start the ladder — wait OFFLINE_GRACE seconds of continuous offline first.
  first_seen=$(get_flag "${key}first_seen" "")
  if [ -z "$first_seen" ]; then
    set_flag "${key}first_seen" "$NOW"
    log "camera offline (watch): $name ($ip)"
    continue
  fi

  offline_for=$(( NOW - first_seen ))
  if [ "$offline_for" -lt "$OFFLINE_GRACE" ]; then
    log "camera offline ${offline_for}s (<${OFFLINE_GRACE}s grace), ladder not started yet: $name"
    continue
  fi

  # Ladder attempts for this episode.
  attempts=$(get_flag "${key}attempts" "0")
  attempts=$(( attempts + 1 ))
  set_flag "${key}attempts" "$attempts"

  log "camera offline ${offline_for}s, running repair ladder attempt $attempts/$MAX_ATTEMPTS: $name ($ip)"
  result=$(repair_ladder "$line")

  if [ "$result" = "online" ]; then
    log "repair succeeded after attempt $attempts: $name"
    set_flag "${key}first_seen" ""
    set_flag "${key}attempts" "0"
    if [ "$(get_flag "${key}alerted" 0)" = "1" ]; then
      echo "$(ts) RESOLVED camera online after repair: $name ($ip)" >> "$ALERT_LOG" 2>/dev/null || true
      set_flag "${key}alerted" "0"
    fi
  elif [ "$attempts" -ge "$MAX_ATTEMPTS" ]; then
    if [ "$(get_flag "${key}alerted" 0)" != "1" ]; then
      alert "camera $name ($ip) offline for $(( offline_for / 60 ))m — probe/reconnect/record/hunt exhausted (id=$id). Check power and network."
      set_flag "${key}alerted" "1"
    else
      log "camera still offline after $attempts attempts, page already sent: $name"
    fi
    # Keep attempts from growing unbounded; the timer keeps re-attempting.
    set_flag "${key}attempts" "0"
  else
    log "camera offline, will retry next tick (attempt $attempts/$MAX_ATTEMPTS): $name"
  fi
done <<< "$(json_objects "$DEVICES")"

# ── 3. clear outage state for devices that vanished from the fleet ──────────
# Remove flags for ids not seen this tick (device unregistered) — keeps the state
# file from growing forever. Done after the loop so SEEN is complete.
if [ "$count" -gt 0 ]; then
  NEXT="${STATE}.clean"
  : > "$NEXT"
  while IFS= read -r flagline; do
    [ -n "$flagline" ] || continue
    id=$(printf '%s' "$flagline" | sed -n 's/^feed_\([0-9a-f-]*\)_first_seen=.*/\1/p')
    if [ -n "$id" ] && [ -z "${SEEN[$id]:-}" ]; then
      continue  # drop stale per-device flags
    fi
    printf '%s\n' "$flagline" >> "$NEXT"
  done < "$STATE"
  if [ -s "$NEXT" ] || [ ! -s "$STATE" ]; then
    mv "$NEXT" "$STATE"
  else
    rm -f "$NEXT"
  fi
fi

log "tick complete: $count camera(s) checked"
exit 0
