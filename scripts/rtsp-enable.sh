#!/usr/bin/env bash
# rtsp-enable.sh — re-enable RTSP streaming on a factory-reset 5523-W camera.
#
# After a factory reset the encode channels are configured but the stream
# transport layer (StreamChannel / transportRTSP) is empty, so port 554
# stays closed.  This script tries several strategies in order and polls
# for the RTSP server to come up after each one.
#
# Usage:
#   ./scripts/rtsp-enable.sh 10.0.0.29
#   ./scripts/rtsp-enable.sh 10.0.0.29 admin mypassword
#   CAMERA_REBOOT=1 ./scripts/rtsp-enable.sh 10.0.0.29   # allow reboot
#
# Env vars:
#   CAMERA_USER       username                  (default admin)
#   CAMERA_PASS       password                  (default blank)
#   CAMERA_PORT       HTTP port                 (default 80)
#   CAMERA_RTSP_PORT  RTSP port                 (default 554)
#   CAMERA_REBOOT     1 to allow reboot as last resort
#   CAMERA_REBOOT_WAIT seconds to wait after reboot (default 45)
#   BOSSAPI           BossCam API base URL (default http://127.0.0.1:5317)
set -euo pipefail

# ── helpers ──────────────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }
log()  { printf "${CYAN}[%s]${NC} %s\n" "$(ts)" "$*"; }
pass() { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(ts)" "$*"; }
fail() { printf "${RED}[%s]  ✘ %s${NC}\n" "$(ts)" "$*"; }
warn() { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(ts)" "$*"; }

# ── args ─────────────────────────────────────────────────────────────────

IP="${1:-}"
USER="${CAMERA_USER:-admin}"
PASS="${CAMERA_PASS:-}"
PORT="${CAMERA_PORT:-80}"
RTSP_PORT="${CAMERA_RTSP_PORT:-554}"
ALLOW_REBOOT="${CAMERA_REBOOT:-0}"
REBOOT_WAIT="${CAMERA_REBOOT_WAIT:-45}"

if [[ -z "$IP" ]]; then
  echo "Usage: $0 <camera-ip> [username] [password]"
  echo ""
  echo "Env vars: CAMERA_USER CAMERA_PASS CAMERA_PORT CAMERA_RTSP_PORT"
  echo "         CAMERA_REBOOT CAMERA_REBOOT_WAIT"
  exit 1
fi
if [[ $# -ge 2 ]]; then USER="$2"; fi
if [[ $# -ge 3 ]]; then PASS="$3"; fi

BASE="http://${IP}:${PORT}"
AUTH="${USER}:${PASS}"

# ── probes ───────────────────────────────────────────────────────────────

# Check if RTSP port is open (TCP connect)
rtsp_port_open() {
  timeout 3 bash -c "echo > /dev/tcp/${IP}/${RTSP_PORT}" 2>/dev/null && return 0
  return 1
}

# Check if RTSP server answers OPTIONS handshake
rtsp_playable() {
  local response
  response=$(printf 'OPTIONS rtsp://%s:%s/ RTSP/1.0\r\nCSeq: 1\r\n\r\n' "$IP" "$RTSP_PORT" \
    | nc -w 3 "$IP" "$RTSP_PORT" 2>/dev/null || true)
  echo "$response" | grep -qi '^RTSP/1\.' && return 0
  return 1
}

# Check HTTP deviceInfo (proves camera is alive)
camera_alive() {
  local code
  code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u "$AUTH" \
    "${BASE}/NetSDK/System/deviceInfo" 2>/dev/null || echo "000")
  [[ "$code" == "200" ]] && return 0
  return 1
}

# Read current encode channel config and print key fields
read_encode_channel() {
  local ch="$1"
  curl -sS -m 5 -u "$AUTH" "${BASE}/NetSDK/Video/encode/channel/${ch}" 2>/dev/null || echo "{}"
}

# Read stream channel list.
# The /NetSDK/Stream/channles endpoint is absent on some firmware
# generations (returns an HTML 404 page).  Validate that the response is
# a real JSON array so callers can distinguish "empty transport" from
# "endpoint unavailable" instead of feeding HTML to the JSON parser.
# Prints "UNAVAILABLE" when the endpoint is missing or non-JSON.
read_stream_channels() {
  local body
  body=$(curl -sS -m 5 -u "$AUTH" "${BASE}/NetSDK/Stream/channles" 2>/dev/null || true)
  if [[ -z "$body" ]]; then
    echo "UNAVAILABLE"
    return 0
  fi
  # Only a response that opens with a JSON array is real channel data.
  if ! echo "$body" | grep -qE '^[[:space:]]*\['; then
    echo "UNAVAILABLE"
    return 0
  fi
  echo "$body"
}

# ── strategies ───────────────────────────────────────────────────────────

enroll_via_api() {
  local api_url="${BOSSAPI:-http://127.0.0.1:5317}"
  log "Enrolling camera via BossCam API (${api_url}) to populate transport profiles..."
  local result
  result=$(curl -sS -w '\n%{http_code}' -m 15 -X POST "${api_url}/api/devices/enroll" \
    -H 'Content-Type: application/json' \
    -d "{\"ipAddress\":\"${IP}\",\"port\":${PORT},\"loginName\":\"${USER}\",\"password\":\"${PASS}\",\"hardwareModel\":\"5523-W\",\"startContinuousRecord\":false}" \
    2>/dev/null || printf '{}\n000\n')
  local http_code
  http_code=$(echo "$result" | tail -1)
  if [[ "$http_code" == "200" ]]; then
    pass "Enrollment API call completed (HTTP 200)."
  else
    warn "Enrollment API returned HTTP ${http_code} — non-fatal, continuing."
  fi
}

# Strategy 1: PUT the encode channel config to "wake up" the pipeline.
# The camera may need a config write to trigger the stream transport layer.
strategy_put_encode() {
  log "Strategy 1: PUT encode channel 101 to wake up video pipeline..."

  # Read current config first
  local cfg
  cfg=$(read_encode_channel 101)
  if [[ -z "$cfg" || "$cfg" == "{}" ]]; then
    fail "  Cannot read encode channel 101 — camera not responding."
    return 1
  fi

  # Extract the enabled field to confirm the channel exists
  local enabled
  enabled=$(echo "$cfg" | python3 -c "import sys,json; print(json.load(sys.stdin).get('enabled',False))" 2>/dev/null || echo "")
  log "  Current encode channel 101 enabled=${enabled}"

  # Build a minimal PUT payload from current values (just enough to trigger re-init)
  local codec res br fr kfi
  codec=$(echo "$cfg" | python3 -c "import sys,json; print(json.load(sys.stdin).get('codecType','H.265+'))" 2>/dev/null || echo "H.265+")
  res=$(echo "$cfg"   | python3 -c "import sys,json; print(json.load(sys.stdin).get('resolution','2560x1920'))" 2>/dev/null || echo "2560x1920")
  br=$(echo "$cfg"    | python3 -c "import sys,json; print(json.load(sys.stdin).get('constantBitRate',1536))" 2>/dev/null || echo "1536")
  fr=$(echo "$cfg"    | python3 -c "import sys,json; print(json.load(sys.stdin).get('frameRate',15))" 2>/dev/null || echo "15")
  kfi=$(echo "$cfg"   | python3 -c "import sys,json; print(json.load(sys.stdin).get('keyFrameInterval',30))" 2>/dev/null || echo "30")

  local payload
  payload=$(cat <<EOPAYLOAD
{
  "id": 101,
  "enabled": true,
  "videoInputChannelID": 101,
  "codecType": "${codec}",
  "h264Profile": "high",
  "resolution": "${res}",
  "constantBitRate": ${br},
  "frameRate": ${fr},
  "keyFrameInterval": ${kfi},
  "bitRateControlType": "VBR",
  "freeResolution": false
}
EOPAYLOAD
)

  local put_result put_code    put_result=$(curl -sS -w '\n%{http_code}' -m 10 -X PUT -u "$AUTH" \
      -H 'Content-Type: application/json' \
      -d "$payload" \
      "${BASE}/NetSDK/Video/encode/channel/101" 2>/dev/null || printf '{}\n000\n')
  put_code=$(echo "$put_result" | tail -1)

  if [[ "$put_code" == "200" ]]; then
    pass "  PUT accepted — encode channel reconfigured."
    return 0
  else
    fail "  PUT returned HTTP ${put_code}"
    return 1
  fi
}

# Strategy 2: Touch the bubble/live FLV endpoint briefly.
# On some 5523-W firmware, accessing the live stream triggers the RTSP
# server to initialise even if FLV-over-HTTP is the primary transport.
strategy_bubble_live() {
  log "Strategy 2: Touch bubble/live endpoint to trigger stream server..."

  # Fetch a tiny bit of the FLV stream then kill it — we don't need data,
  # just the side effect of the server allocating the stream transport.
  local touched
  touched=$(timeout 5 curl -sS -m 4 -u "$AUTH" \
    "${BASE}/bubble/live?ch=1&stream=0" 2>/dev/null | head -c 1 || true)

  if [[ -n "$touched" ]]; then
    pass "  bubble/live responded — stream server touched."
    return 0
  else
    warn "  bubble/live returned no data (may be expected)."
    return 0  # Non-fatal — the side effect may still have occurred
  fi
}

# Strategy 4: Reboot the camera.
# A full reboot often restores all services including RTSP.
strategy_reboot() {
  if [[ $ALLOW_REBOOT -ne 1 ]]; then
    warn "Strategy 4: Reboot SKIPPED (CAMERA_REBOOT=0)."
    warn "  Re-run with CAMERA_REBOOT=1 to allow reboot."
    return 1
  fi

  log "Strategy 4: Rebooting camera (will wait ${REBOOT_WAIT}s)..."
  log "  Trying documented reboot endpoints in order..."

  # Reboot paths ordered by live-proven reliability. The canonical path is
  # /NetSDK/System/operation/reboot (live-proven on 5523-W: GET → 405,
  # PUT with {"reboot":true} works), with legacy /netsdk/Reboot and older
  # firmware variants as fallbacks.
  local -a reboot_paths=(
    "/NetSDK/System/operation/reboot:PUT"
    "/netsdk/Reboot:PUT"
    "/NetSDK/System/reboot:PUT"
    "/NetSDK/Factory?cmd=Reboot:GET"
  )

  local path method reb reb_code
  reb_code=""
  for entry in "${reboot_paths[@]}"; do
    path="${entry%%:*}"
    method="${entry##*:}"
    if [[ "$method" == "PUT" ]]; then
      reb=$(curl -sS -w '\n%{http_code}' -m 10 -X PUT -u "$AUTH" \
        -H 'Content-Type: application/json' \
        -d '{"reboot":true}' \
        "${BASE}${path}" 2>/dev/null || printf '{}')
    else
      reb=$(curl -sS -w '\n%{http_code}' -m 10 -u "$AUTH" \
        "${BASE}${path}" 2>/dev/null || printf '{}')
    fi
    reb_code=$(echo "$reb" | tail -1)
    if [[ "$reb_code" == "200" || "$reb_code" == "202" ]]; then
      pass "  Reboot accepted via ${path} (HTTP ${reb_code})."
      break
    fi
    warn "  ${path} → HTTP ${reb_code} — trying next path..."
    reb_code=""
  done

  if [[ -z "$reb_code" ]]; then
    fail "  Reboot failed — no reboot path returned 2xx."
    return 1
  fi

  log "  Waiting ${REBOOT_WAIT}s for camera to come back..."
  local waited=0
  while [[ $waited -lt $REBOOT_WAIT ]]; do
    sleep 5
    waited=$((waited + 5))
    if camera_alive; then
      pass "  Camera responded after ${waited}s."
      # Give it a few more seconds for RTSP to initialise
      sleep 10
      return 0
    fi
    echo -n "."
  done
  echo ""
  fail "  Camera did not come back within ${REBOOT_WAIT}s."
  return 1
}

# ── main ─────────────────────────────────────────────────────────────────

log "RTSP recovery — camera ${IP}:${PORT}  (user=${USER})"
log "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# Pre-flight: confirm camera is reachable
if ! camera_alive; then
  fail "Camera not reachable at ${BASE}/NetSDK/System/deviceInfo"
  exit 1
fi
pass "Camera reachable — deviceInfo OK"

# Show current state
log ""
log "Current state:"
log "  Encode channel 101:"
read_encode_channel 101 | python3 -c "
import sys,json
c = json.load(sys.stdin)
print(f\"    enabled={c.get('enabled')}  codec={c.get('codecType','?')}  resolution={c.get('resolution','?')}\")
" 2>/dev/null || echo "    (unable to parse)"

log "  Stream channels:"
  streams=$(read_stream_channels)
if [[ "$streams" == "UNAVAILABLE" ]]; then
  warn "    Endpoint unavailable on this firmware (404) — transport state unknown"
elif [[ -z "$streams" || "$streams" == "[]" ]]; then
  warn "    EMPTY — no stream transport configured"
else
  echo "$streams" | python3 -c "
import sys,json
for s in json.load(sys.stdin):
    rtsp = s.get('transportRTSP',{})
    print(f\"    id={s.get('id')}  rtsp.enabled={rtsp.get('enabled')}  rtsp.port={rtsp.get('port')}\")
" 2>/dev/null || echo "    (unable to parse)"
fi

# Check RTSP now
log ""
if rtsp_port_open; then
  pass "RTSP port ${RTSP_PORT} is already OPEN"
  if rtsp_playable; then
    pass "RTSP OPTIONS handshake successful — service is healthy!"
    exit 0
  else
    warn "Port open but RTSP server not answering OPTIONS — continuing recovery."
  fi
else
  fail "RTSP port ${RTSP_PORT} is CLOSED — starting recovery..."
fi

# ── Run strategies ─────────────────────────────────────────────────────

STRATEGIES_RUN=0

# Strategy 1: PUT encode channel
echo ""
strategy_put_encode || true
STRATEGIES_RUN=$((STRATEGIES_RUN + 1))
sleep 3

log "Re-checking RTSP after strategy 1..."
if rtsp_playable; then
  pass "RTSP is now PLAYABLE! Recovery complete."
  exit 0
fi
fail "RTSP still not answering."

# Strategy 2: Touch bubble/live
echo ""
strategy_bubble_live || true
STRATEGIES_RUN=$((STRATEGIES_RUN + 1))
sleep 5

log "Re-checking RTSP after strategy 2..."
if rtsp_playable; then
  pass "RTSP is now PLAYABLE! Recovery complete."
  exit 0
fi
fail "RTSP still not answering."

# Strategy 3: Enroll via BossCam API (populates transport profiles)
echo ""
if curl -fsS "http://127.0.0.1:5317/api/health" >/dev/null 2>&1; then
  enroll_via_api || true
  STRATEGIES_RUN=$((STRATEGIES_RUN + 1))
  sleep 3

  log "Re-checking RTSP after strategy 3..."
  if rtsp_playable; then
    pass "RTSP is now PLAYABLE! Recovery complete."
    exit 0
  fi
  fail "RTSP still not answering."
else
  warn "Strategy 3: BossCam API not reachable — skipping enroll."
fi

# Strategy 4: Reboot (if allowed)
echo ""
STRATEGIES_RUN=$((STRATEGIES_RUN + 1))

if strategy_reboot; then
  log "Re-checking RTSP after reboot..."
  sleep 5
  if rtsp_playable; then
    pass "RTSP is now PLAYABLE after reboot! Recovery complete."
    exit 0
  fi
  fail "RTSP still not answering after reboot."
fi

# ── Final check ────────────────────────────────────────────────────────

log ""
if rtsp_port_open; then
  warn "RTSP port ${RTSP_PORT} is now OPEN but not answering OPTIONS."
  warn "The RTSP server may need more time to initialise."
  warn "Try running camera-recovery.sh to poll until it comes up."
  exit 0
fi

fail "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
fail "All ${STRATEGIES_RUN} strategies attempted — RTSP still not available."
fail ""
fail "Try:"
fail "  1. Re-run with CAMERA_REBOOT=1 (if not already set)"
fail "  2. Manually reboot the camera (power cycle)"
fail "  3. Run camera-recovery.sh --watchdog to wait for RTSP"
fail "  4. Check the camera's web UI for stream settings"
exit 1
