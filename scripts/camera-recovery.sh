#!/usr/bin/env bash
# camera-recovery.sh — poll a camera after power-cycle until deviceInfo,
# RTSP, and snapshot are all healthy, then optionally auto-enroll and start
# continuous recording via the BossCam API.
#
# Usage:
#   ./scripts/camera-recovery.sh 10.0.0.169
#   ./scripts/camera-recovery.sh 10.0.0.169 admin mypassword
#   ./scripts/camera-recovery.sh --enroll 10.0.0.169
#   ./scripts/camera-recovery.sh --enroll --record 10.0.0.169 admin mypassword
#   ./scripts/camera-recovery.sh --watchdog 10.0.0.169
#   ./scripts/camera-recovery.sh --enroll --watchdog 10.0.0.169
#   CAMERA_TIMEOUT=120 CAMERA_ENROLL=1 CAMERA_WATCHDOG=1 ./scripts/camera-recovery.sh 10.0.0.169
#
# Env vars:
#   CAMERA_TIMEOUT       seconds before giving up (default 180)
#   CAMERA_INTERVAL      seconds between polls     (default 5)
#   CAMERA_USER          username                  (default admin)
#   CAMERA_PASS          password                  (default blank)
#   CAMERA_PORT          HTTP port                 (default 80)
#   CAMERA_RTSP_PORT     RTSP port                 (default 554)
#   CAMERA_ENROLL        1 to auto-enroll after recovery (also --enroll flag)
#   CAMERA_RECORD        1 to start continuous recording after enroll (also --record flag)
#   CAMERA_WATCHDOG      1 to keep running and re-check after recovery (also --watchdog flag)
#   CAMERA_WATCHDOG_INTERVAL  seconds between watchdog checks (default 60)
#   CAMERA_API           BossCam API base URL      (default http://127.0.0.1:5317)
#   CAMERA_MODEL         hardware model for enroll (default 5523-W)
#   CAMERA_NAME          display name for enroll   (default "Camera <ip>")
set -euo pipefail

# ── helpers ──────────────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }

log()  { printf "${CYAN}[%s]${NC} %s\n" "$(ts)" "$*"; }
pass() { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(ts)" "$*"; }
fail() { printf "${RED}[%s]  ✘ %s${NC}\n" "$(ts)" "$*"; }
warn() { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(ts)" "$*"; }

# ── args / env ───────────────────────────────────────────────────────────

ENROLL=0
RECORD=0
WATCHDOG=0

# Parse flags (--enroll, --record, --watchdog) before positional args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --enroll)   ENROLL=1; shift ;;
    --record)   RECORD=1; shift ;;
    --watchdog) WATCHDOG=1; shift ;;
    *)          break ;;
  esac
done

# Override from env vars if set
[[ "${CAMERA_ENROLL:-0}" == "1" ]] && ENROLL=1
[[ "${CAMERA_RECORD:-0}" == "1" ]] && RECORD=1
[[ "${CAMERA_WATCHDOG:-0}" == "1" ]] && WATCHDOG=1

IP="${1:-}"
USER="${CAMERA_USER:-admin}"
PASS="${CAMERA_PASS:-}"
PORT="${CAMERA_PORT:-80}"
RTSP_PORT="${CAMERA_RTSP_PORT:-554}"
TIMEOUT="${CAMERA_TIMEOUT:-180}"
INTERVAL="${CAMERA_INTERVAL:-5}"
WATCHDOG_INTERVAL="${CAMERA_WATCHDOG_INTERVAL:-60}"
# Enforce a minimum to prevent spin-looping on a misconfigured interval.
[[ $WATCHDOG_INTERVAL -lt 5 ]] && WATCHDOG_INTERVAL=5
MAX_ATTEMPTS=$(( (TIMEOUT + INTERVAL - 1) / INTERVAL ))
API="${CAMERA_API:-http://127.0.0.1:5317}"
MODEL="${CAMERA_MODEL:-5523-W}"
NAME="${CAMERA_NAME:-Camera ${IP}}"

if [[ -z "$IP" ]]; then
  echo "Usage: $0 [--enroll] [--record] [--watchdog] <camera-ip> [username] [password]"
  echo ""
  echo "Flags:"
  echo "  --enroll    Auto-enroll via BossCam API once healthy"
  echo "  --record    Also start continuous recording (requires --enroll)"
  echo "  --watchdog  Keep running and re-check every \$WATCHDOG_INTERVAL seconds"
  echo ""
  echo "Env vars: CAMERA_TIMEOUT CAMERA_INTERVAL CAMERA_USER CAMERA_PASS"
  echo "         CAMERA_PORT CAMERA_RTSP_PORT CAMERA_ENROLL CAMERA_RECORD"
  echo "         CAMERA_WATCHDOG CAMERA_WATCHDOG_INTERVAL"
  echo "         CAMERA_API CAMERA_MODEL CAMERA_NAME"
  exit 1
fi

# Optional positional overrides for user / pass (after flags consumed)
if [[ $# -ge 2 ]]; then USER="$2"; fi
if [[ $# -ge 3 ]]; then PASS="$3"; fi

# ── enrollment helpers ───────────────────────────────────────────────────

# do_enroll: POST to the BossCam API to enroll the recovered camera.
# Parses the result and sets ENROLLED_DEVICE_ID on success.
ENROLLED_DEVICE_ID=""

do_enroll() {
  log "Enrolling camera via BossCam API (${API}) ..."

  local payload result http_code
  payload=$(cat <<EOJSON
{
  "ipAddress": "${IP}",
  "port": ${PORT},
  "loginName": "${USER}",
  "password": "${PASS}",
  "displayName": "${NAME}",
  "hardwareModel": "${MODEL}",
  "startContinuousRecord": false
}
EOJSON
)

  result=$(curl -sS -w '\n%{http_code}' -X POST "${API}/api/devices/enroll" \
    -H 'Content-Type: application/json' \
    -d "$payload" 2>/dev/null || printf '{}\n000\n')

  http_code=$(echo "$result" | tail -1)
  result=$(echo "$result" | sed '$d')

  if [[ "$http_code" != "200" ]]; then
    fail "Enrollment failed — API returned HTTP ${http_code}"
    warn "  Response: $(echo "$result" | head -c 500)"
    return 1
  fi

  # Parse the enrolled device-id and key fields from the JSON response.
  # We use python3 if available; otherwise fall back to grep.
  local device_id enrolled model_name chosen_source source_role degraded
  if command -v python3 >/dev/null 2>&1; then
    # One python3 call outputs six tab-delimited fields.
    IFS=$'\t' read -r device_id enrolled model_name chosen_source source_role degraded \
      < <(echo "$result" | python3 -c "
import sys,json
r = json.load(sys.stdin)
print(r.get('deviceId',''),
      'true' if r.get('enrolled') else 'false',
      r.get('hardwareModel','') or r.get('displayName',''),
      r.get('chosenSourceUrl','') or '',
      r.get('sourceRole','') or '',
      r.get('degradedReason','') or '',
      sep='\t')
" 2>/dev/null || printf '\tfalse\t\t\t\t')
  else
    device_id=$(echo "$result" | grep -o '"deviceId"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/' || echo "")
    enrolled=$(echo "$result" | grep -o '"enrolled"[[:space:]]*:[[:space:]]*\(true\|false\)' | head -1 | sed 's/.*: *//' || echo "false")
    model_name=""
    source_role=""
    degraded=""
  fi

  ENROLLED_DEVICE_ID="$device_id"

  if [[ "$enrolled" == "true" ]]; then
    pass "Enrolled successfully — deviceId=${device_id}"
    [[ -n "$model_name" ]] && log "  Model: ${model_name}"

    # Show source resolution outcome
    if [[ -n "$chosen_source" && "$chosen_source" != "null" ]]; then
      pass "  Playable source: ${source_role:-?} — ${chosen_source}"
    elif [[ -n "$degraded" && "$degraded" != "null" ]]; then
      warn "  ${degraded}"
    fi

    # Print per-step results
    if command -v python3 >/dev/null 2>&1; then
      echo "$result" | python3 -c "
import sys,json
r = json.load(sys.stdin)
for s in r.get('steps',[]):
    mark = '✔' if s.get('success') else '✘'
    print(f'    {mark} {s[\"step\"]}: {s.get(\"message\",\"\")}')
" 2>/dev/null || true
    fi

    return 0
  else
    fail "Enrollment reported enrolled=false"
    if command -v python3 >/dev/null 2>&1; then
      echo "$result" | python3 -c "
import sys,json
r = json.load(sys.stdin)
for s in r.get('steps',[]):
    mark = '✘'
    print(f'    {mark} {s[\"step\"]}: {s.get(\"message\",\"\")}')
" 2>/dev/null || true
    fi
    return 1
  fi
}

# do_continuous_record: start a recording job on the just-enrolled device.
# Requires ENROLLED_DEVICE_ID to be set (by do_enroll).
do_continuous_record() {
  if [[ -z "$ENROLLED_DEVICE_ID" || "$ENROLLED_DEVICE_ID" == "00000000-0000-0000-0000-000000000000" ]]; then
    warn "Cannot start recording — no valid device-id from enrollment."
    return 1
  fi

  log "Starting continuous recording for device ${ENROLLED_DEVICE_ID} ..."

  local result http_code
  result=$(curl -sS -w '\n%{http_code}' -X POST "${API}/api/recordings/start" \
    -H 'Content-Type: application/json' \
    -d "{\"deviceId\":\"${ENROLLED_DEVICE_ID}\"}" 2>/dev/null || printf '{}\n000\n')

  http_code=$(echo "$result" | tail -1)
  result=$(echo "$result" | sed '$d')

  if [[ "$http_code" != "200" ]]; then
    fail "Recording start failed — API returned HTTP ${http_code}"
    warn "  Response: $(echo "$result" | head -c 500)"
    return 1
  fi

  local job_id running mode source_role
  if command -v python3 >/dev/null 2>&1; then
    # One python3 call outputs four tab-delimited fields.
    IFS=$'\t' read -r job_id running mode source_role \
      < <(echo "$result" | python3 -c "
import sys,json
r = json.load(sys.stdin)
print(r.get('id',''),
      'true' if r.get('isRunning') else 'false',
      r.get('mode','') or '',
      r.get('sourceRole','') or '',
      sep='\t')
" 2>/dev/null || printf '\tfalse\t\t')
  else
    job_id=$(echo "$result" | grep -o '"id"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/' || echo "")
    running="false"
    mode=""
    source_role=""
  fi

  if [[ "$running" == "true" ]]; then
    pass "Recording started — job=${job_id}  mode=${mode}  source=${source_role:-main}"
    return 0
  else
    fail "Recording job created but not running (job=${job_id})"
    return 1
  fi
}

# ── watchdog loop (post-recovery) ─────────────────────────────────────────

# run_watchdog: after recovery + optional enroll/record, stay resident and
# re-probe every WATCHDOG_INTERVAL seconds.  Prints a compact status line
# when all is well; alerts loudly (with bell) when any service drops.
WATCHDOG_CYCLE=0
WATCHDOG_CONSECUTIVE_FAILS=0
WATCHDOG_HAD_FAILURES=0
WATCHDOG_RUNNING=1

run_watchdog() {
  log "Watchdog active — checking every ${WATCHDOG_INTERVAL}s  (Ctrl+C to stop)"
  echo ""

  local p di rt sp ok

  while [[ $WATCHDOG_RUNNING -eq 1 ]]; do
    sleep "$WATCHDOG_INTERVAL"
    WATCHDOG_CYCLE=$((WATCHDOG_CYCLE + 1))

    p=0; di=0; rt=0; sp=0

    probe_ping        && p=1
    probe_deviceinfo  && di=1
    probe_rtsp        && rt=1
    probe_snapshot    && sp=1

    ok=$(( p + di + rt + sp ))

    if [[ $ok -eq 4 ]]; then
      WATCHDOG_CONSECUTIVE_FAILS=0
      log "watchdog #${WATCHDOG_CYCLE}  ${GREEN}✓ all 4 services healthy${NC}"
      # Append to status log
      echo "$(ts) watchdog cycle=${WATCHDOG_CYCLE} ping=${p} deviceInfo=${di} rtsp=${rt} snapshot=${sp} status=healthy" >> "$STATUS_LOG"
    else
      WATCHDOG_CONSECUTIVE_FAILS=$((WATCHDOG_CONSECUTIVE_FAILS + 1))
      WATCHDOG_HAD_FAILURES=1
      local bell=""
      # Ring terminal bell on the first failure after a healthy stretch
      [[ $WATCHDOG_CONSECUTIVE_FAILS -eq 1 ]] && bell=$'\a'

      warn "${bell}watchdog #${WATCHDOG_CYCLE}  ${RED}✘ ${ok}/4 services up  (${WATCHDOG_CONSECUTIVE_FAILS} consecutive failures)${NC}"

      [[ $p -eq 0 ]]  && fail "  ping          — unreachable"
      [[ $di -eq 0 ]] && fail "  deviceInfo    — HTTP REST down (port ${PORT})"
      [[ $rt -eq 0 ]] && fail "  RTSP          — no RTSP response (:${RTSP_PORT})"
      [[ $sp -eq 0 ]] && fail "  snapshot      — no JPEG from encode pipeline"

      echo "$(ts) watchdog cycle=${WATCHDOG_CYCLE} ping=${p} deviceInfo=${di} rtsp=${rt} snapshot=${sp} status=degraded consecutive_fails=${WATCHDOG_CONSECUTIVE_FAILS}" >> "$STATUS_LOG"
    fi
  done
}

# Clean exit handler for watchdog mode — traps SIGINT / SIGTERM.
watchdog_cleanup() {
  WATCHDOG_RUNNING=0
  echo ""
  log "Watchdog stopped after ${WATCHDOG_CYCLE} cycle(s)."
  log "Status log: ${STATUS_LOG}"
  if [[ $WATCHDOG_HAD_FAILURES -eq 1 ]]; then
    warn "Service degradation was detected during the watch period."
    exit 2
  fi
  exit 0
}

# ── probe functions ──────────────────────────────────────────────────────

# 1. PING — basic L3 reachability (quick, no auth)
probe_ping() {
  if timeout 2 ping -c1 -W1 "$IP" >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

# 2. DEVICE-INFO — NetSDK REST deviceInfo (proves HTTP + credentials work)
probe_deviceinfo() {
  local code
  code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u "${USER}:${PASS}" \
    "http://${IP}:${PORT}/NetSDK/System/deviceInfo" 2>/dev/null || echo "000")
  # The camera returns HTTP 200 with a JSON body for deviceInfo when auth is
  # correct.  Any 2xx is success; 401 means auth mismatch; anything else means
  # the HTTP REST surface is not ready.
  [[ "$code" == "200" ]] && return 0
  return 1
}

# 3. RTSP — TCP connect + OPTIONS handshake (matches BossCam.Core RtspProbe)
probe_rtsp() {
  # Send a minimal RTSP OPTIONS request and look for an RTSP/1.x status line.
  # Any RTSP status line (including 401) proves an RTSP server is present —
  # closed ports / silent listeners / non-RTSP banners all count as failure.
  # nc -w 3 alone enforces the connect+read timeout (no outer 'timeout' needed).
  local response
  response=$(printf 'OPTIONS rtsp://%s:%s/ RTSP/1.0\r\nCSeq: 1\r\n\r\n' "$IP" "$RTSP_PORT" \
    | nc -w 3 "$IP" "$RTSP_PORT" 2>/dev/null || true)
  if echo "$response" | grep -qi '^RTSP/1\.'; then
    return 0
  fi
  return 1
}

# 4. SNAPSHOT — JPEG snapshot endpoint (proves encode pipeline is alive)
probe_snapshot() {
  local tmp
  tmp=$(mktemp /tmp/cam-recovery-snap-XXXXXX)
  local code
  code=$(curl -sS -o "$tmp" -w '%{http_code}' -m 8 -u "${USER}:${PASS}" \
    "http://${IP}:${PORT}/NetSDK/Video/encode/channel/101/snapShot" 2>/dev/null || echo "000")
  if [[ "$code" == "200" ]] && file "$tmp" 2>/dev/null | grep -qi 'JPEG'; then
    rm -f "$tmp"
    return 0
  fi
  rm -f "$tmp"
  return 1
}

# ── main poll loop ───────────────────────────────────────────────────────

STATUS_LOG="/tmp/camera-recovery-${IP}.log"
> "$STATUS_LOG"

log "Camera recovery — polling ${IP}:${PORT}  (user=${USER}, rtsp=:${RTSP_PORT})"
log "Timeout ${TIMEOUT}s / interval ${INTERVAL}s / max ${MAX_ATTEMPTS} attempts"
[[ $ENROLL -eq 1 ]] && log "Auto-enroll: YES  (API ${API}, model=${MODEL})"
[[ $RECORD -eq 1 ]] && log "Continuous record: YES"
[[ $WATCHDOG -eq 1 ]] && log "Watchdog: YES  (interval ${WATCHDOG_INTERVAL}s)"
log "Status log: ${STATUS_LOG}"
echo ""

attempt=0
start_ts=$(date +%s)

while [[ $attempt -lt $MAX_ATTEMPTS ]]; do
  attempt=$((attempt + 1))
  elapsed=$(($(date +%s) - start_ts))

  ping_ok=0; di_ok=0; rtsp_ok=0; snap_ok=0
  ping_msg=""; di_msg=""; rtsp_msg=""; snap_msg=""

  # ── ping ──
  if probe_ping; then
    ping_ok=1; ping_msg="$(pass "ping  OK  (${elapsed}s)")"
  else
    ping_msg="$(fail "ping  ——")"
  fi

  # ── deviceInfo ──
  if probe_deviceinfo; then
    di_ok=1; di_msg="$(pass "deviceInfo  OK")"
  else
    di_msg="$(fail "deviceInfo  ——")"
  fi

  # ── RTSP ──
  if probe_rtsp; then
    rtsp_ok=1; rtsp_msg="$(pass "RTSP   OK  (:${RTSP_PORT})")"
  else
    rtsp_msg="$(fail "RTSP   ——  (:${RTSP_PORT})")"
  fi

  # ── snapshot ──
  if probe_snapshot; then
    snap_ok=1; snap_msg="$(pass "snapshot  OK")"
  else
    snap_msg="$(fail "snapshot  ——")"
  fi

  # ── header ──
  summary="attempt ${attempt}/${MAX_ATTEMPTS} | elapsed ${elapsed}s | "
  if [[ $ping_ok -eq 1 ]]; then summary+="ping✓ "; else summary+="ping✗ "; fi
  if [[ $di_ok -eq 1 ]];   then summary+="di✓ ";   else summary+="di✗ ";   fi
  if [[ $rtsp_ok -eq 1 ]]; then summary+="rtsp✓ "; else summary+="rtsp✗ "; fi
  if [[ $snap_ok -eq 1 ]]; then summary+="snap✓ "; else summary+="snap✗ "; fi

  log "$summary"
  printf '%b\n' "$ping_msg"
  printf '%b\n' "$di_msg"
  printf '%b\n' "$rtsp_msg"
  printf '%b\n' "$snap_msg"
  echo ""

  # Append to flat status log
  {
    echo "$(ts) attempt=${attempt} elapsed=${elapsed}s ping=${ping_ok} deviceInfo=${di_ok} rtsp=${rtsp_ok} snapshot=${snap_ok}"
  } >> "$STATUS_LOG"

  # ── all healthy? ──
  if [[ $ping_ok -eq 1 && $di_ok -eq 1 && $rtsp_ok -eq 1 && $snap_ok -eq 1 ]]; then
    log "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    log "${GREEN}✓ ALL SERVICES HEALTHY after ${elapsed}s${NC}"
    log "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo ""

    # ── auto-enroll (when --enroll or CAMERA_ENROLL=1) ──
    if [[ $ENROLL -eq 1 ]]; then
      if do_enroll; then
        # ── continuous record (when --record or CAMERA_RECORD=1) ──
        if [[ $RECORD -eq 1 ]]; then
          do_continuous_record || true  # record failure is non-fatal
        fi
      fi
    else
      log "Camera ${IP} is ready for enrollment and recording."
      log "(Run with --enroll to auto-enroll via the BossCam API.)"
    fi

    echo ""

    # ── watchdog mode (--watchdog or CAMERA_WATCHDOG=1) ──
    if [[ $WATCHDOG -eq 1 ]]; then
      trap watchdog_cleanup INT TERM
      run_watchdog
      # unreachable — watchdog_cleanup exits
    fi

    log "${GREEN}Recovery complete.${NC}"
    exit 0
  fi

  # Check absolute timeout (separate from attempt loop in case probes are slow)
  if [[ $(date +%s) -ge $((start_ts + TIMEOUT)) ]]; then
    warn "Absolute timeout (${TIMEOUT}s) reached."
    break
  fi

  sleep "$INTERVAL"
done

# ── timeout path ─────────────────────────────────────────────────────────

log "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
log "${RED}✘ TIMEOUT after ${TIMEOUT}s — not all services came up${NC}"
log "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
log "Final probe — running one last check for diagnosis..."

ping_ok=0; di_ok=0; rtsp_ok=0; snap_ok=0

probe_ping        && ping_ok=1
probe_deviceinfo  && di_ok=1
probe_rtsp        && rtsp_ok=1
probe_snapshot    && snap_ok=1

if [[ $ping_ok -eq 0 ]]; then
  fail "PING          — camera unreachable (is it powered on?)"
fi
if [[ $di_ok -eq 0 ]]; then
  fail "deviceInfo    — HTTP REST surface not answering (port ${PORT}, user=${USER})"
  warn "  Try: curl -u '${USER}:<pass>' http://${IP}:${PORT}/NetSDK/System/deviceInfo"
fi
if [[ $rtsp_ok -eq 0 ]]; then
  fail "RTSP          — port ${RTSP_PORT} not responding to RTSP OPTIONS"
  warn "  RTSP may be disabled in the camera's stream settings. Re-enable it via NetSDK REST."
  warn "  Try: curl -u '${USER}:<pass>' 'http://${IP}:${PORT}/NetSDK/Video/encode/channel/101'"
fi
if [[ $snap_ok -eq 0 ]]; then
  fail "snapshot      — encode pipeline not serving JPEGs"
  warn "  The video encoder may need configuration before snapshots work."
fi

echo ""
log "Full status log saved to ${STATUS_LOG}"
exit 1
