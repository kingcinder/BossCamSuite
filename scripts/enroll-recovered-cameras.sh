#!/usr/bin/env bash
# ── Enroll & Record: 5523-W cameras after factory reset ──────────────────
# Run this AFTER physically factory-resetting the cameras (hold reset button
# 10-15s until reboot).  Cameras must be back in true factory state where
# admin: (blank) is accepted on the HTTP REST API.
#
# Usage:  bash scripts/enroll-recovered-cameras.sh
#
# The script polls for each camera to come back, enrolls it through the
# BossCam API, starts continuous recording, and verifies segments appear.

set -euo pipefail

API="http://127.0.0.1:5317"
POLL_TIMEOUT=180   # seconds to wait for each camera
POLL_INTERVAL=5    # seconds between polls
SEGMENT_WAIT=20    # seconds to wait for first segment

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[$(date +%H:%M:%S)]${NC} $*"; }
warn() { echo -e "${YELLOW}[$(date +%H:%M:%S)]${NC} $*"; }
fail() { echo -e "${RED}[$(date +%H:%M:%S)] FAIL${NC} $*"; }

# ── helpers ──────────────────────────────────────────────────────────────

# Returns 0 if the camera at $1 answers deviceInfo with HTTP 200 using blank pw.
camera_ready() {
    local ip="$1"
    local code
    code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u 'admin:' \
        "http://${ip}/NetSDK/System/deviceInfo" 2>/dev/null || echo "000")
    [ "$code" = "200" ]
}

# Poll $1 until camera_ready or $POLL_TIMEOUT expires. Returns 0 on success.
poll_camera() {
    local ip="$1"
    local label="${2:-$ip}"
    local elapsed=0
    log "Polling $label ($ip) for factory-state readiness (admin: blank)..."

    while [ $elapsed -lt $POLL_TIMEOUT ]; do
        if camera_ready "$ip"; then
            log "  $label is READY after ${elapsed}s"
            return 0
        fi
        sleep "$POLL_INTERVAL"
        elapsed=$((elapsed + POLL_INTERVAL))
        # Progress dot every 30s
        [ $((elapsed % 30)) -eq 0 ] && warn "  ... still waiting (${elapsed}s) ..."
    done

    fail "$label did not come back within ${POLL_TIMEOUT}s"
    return 1
}

# Enroll camera via API and return the deviceId on stdout.
enroll_camera() {
    local ip="$1"
    local name="${2:-Camera $ip}"

    log "Enrolling $name ($ip) via API..."
    local response
    response=$(curl -sS -m 45 -X POST "${API}/api/devices/enroll" \
        -H 'Content-Type: application/json' \
        -d "{
            \"ipAddress\": \"${ip}\",
            \"port\": 80,
            \"loginName\": \"admin\",
            \"password\": \"\",
            \"displayName\": \"${name}\",
            \"hardwareModel\": \"5523-W\",
            \"startContinuousRecord\": true
        }" 2>&1)

    local deviceId
    deviceId=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deviceId',''))" 2>/dev/null || echo "")
    local enrolled
    enrolled=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin).get('enrolled',''))" 2>/dev/null || echo "")

    if [ "$enrolled" = "True" ] && [ -n "$deviceId" ]; then
        log "  Enrolled. deviceId=$deviceId"
        echo "$deviceId"
        return 0
    fi

    warn "  Enrollment response: $(echo "$response" | head -c 300)"
    # Check for auth rejection (the stored old record may have wrong pw)
    if echo "$response" | grep -qi "Authentication rejected"; then
        fail "  Camera still rejecting auth — may not be fully factory-reset yet."
    fi
    return 1
}

# Start recording for a device. $1=deviceId
start_recording() {
    local deviceId="$1"
    local label="${2:-$deviceId}"

    log "Starting continuous recording for $label..."
    local response
    response=$(curl -sS -m 30 -X POST "${API}/api/recordings/start" \
        -H 'Content-Type: application/json' \
        -d "{\"deviceId\": \"${deviceId}\"}" 2>&1)

    local running
    running=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin).get('isRunning',''))" 2>/dev/null || echo "")
    local mode
    mode=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin).get('mode',''))" 2>/dev/null || echo "")

    if [ "$running" = "True" ]; then
        log "  Recording started: mode=$mode"
        return 0
    fi

    warn "  Recording start response: $(echo "$response" | head -c 200)"
    return 1
}

# Verify segments appear in output dir within SEGMENT_WAIT seconds.
verify_segments() {
    local ip="$1"
    local dir
    dir=$(ls -d /home/cody/.local/share/BossCamSuite/recordings/"$(echo "$ip" | tr '.' '_')"* 2>/dev/null | head -1)

    if [ -z "$dir" ]; then
        warn "  Cannot find output directory for $ip"
        return 1
    fi

    log "  Waiting up to ${SEGMENT_WAIT}s for segments in $dir..."
    local waited=0
    while [ $waited -lt $SEGMENT_WAIT ]; do
        local count
        count=$(find "$dir" -name "*.ts" -newer "$dir" -mmin -1 2>/dev/null | wc -l)
        if [ "$count" -gt 0 ]; then
            local latest latest_size
            latest=$(ls -t "$dir"/*.ts 2>/dev/null | head -1)
            latest_size=$(stat -c%s "$latest" 2>/dev/null || echo "?")
            log "  ✓ $count segment(s) found. Latest: $(basename "$latest") (${latest_size} bytes)"
            return 0
        fi
        sleep 2
        waited=$((waited + 2))
    done
    warn "  No segments appeared within ${SEGMENT_WAIT}s — recording may have failed silently."
    return 1
}

# ── main ─────────────────────────────────────────────────────────────────

log "============================================"
log " Enroll & Record: 5523-W Recovery Script"
log "============================================"
log ""
log "Waiting for cameras to come back from factory reset..."
log "Make sure you've held the reset button until reboot."
log ""

# Check BossCam is healthy
if ! curl -sS -m 3 "${API}/api/health" > /dev/null 2>&1; then
    fail "BossCam API is not reachable at ${API}. Is the service running?"
    exit 1
fi
log "BossCam API is healthy."

# ── Camera list: IP → display name ──────────────────────────────────────
declare -A CAMERAS=(
    ["10.0.0.29"]="5523-W-29"
    ["10.0.0.169"]="5523-W-169"
)

SUCCESS=0
FAILED=0

for IP in "${!CAMERAS[@]}"; do
    NAME="${CAMERAS[$IP]}"
    echo ""
    log "── Processing $NAME ($IP) ──"

    # 1. Poll
    if ! poll_camera "$IP" "$NAME"; then
        FAILED=$((FAILED + 1))
        continue
    fi

    # 2. Show device info
    log "Device info:"
    curl -sS -m 5 -u 'admin:' "http://${IP}/NetSDK/System/deviceInfo" 2>/dev/null | \
        python3 -c "
import sys,json
d=json.load(sys.stdin)
print(f'  Model: {d.get(\"model\",\"?\")}  Serial: {d.get(\"serialNumber\",\"?\")}  FW: {d.get(\"firmwareVersion\",\"?\")}')
" 2>/dev/null || warn "  (could not parse device info)"

    # 3. Enroll
    DEVICE_ID=$(enroll_camera "$IP" "$NAME") || {
        FAILED=$((FAILED + 1))
        continue
    }

    # 4. Start recording (enroll already sets continuousRecord=true, but
    #    the continuous-record policy reconciles on a cycle — fire it now
    #    so recording starts immediately.)
    start_recording "$DEVICE_ID" "$NAME" || warn "  Recording start may have been deferred to policy cycle"

    # 5. Verify segments
    verify_segments "$IP" || warn "  Segment check inconclusive"

    SUCCESS=$((SUCCESS + 1))
done

echo ""
log "============================================"
log " Done.  Success: $SUCCESS  Failed: $FAILED"
log "============================================"

if [ $SUCCESS -gt 0 ]; then
    log ""
    log "To verify all recordings:"
    log "  curl -sS ${API}/api/recordings | python3 -m json.tool"
    log ""
    log "Recording output directories:"
    for IP in "${!CAMERAS[@]}"; do
        ls -d /home/cody/.local/share/BossCamSuite/recordings/"$(echo "$IP" | tr '.' '_')"* 2>/dev/null && \
            echo "  $IP -> $(ls -t /home/cody/.local/share/BossCamSuite/recordings/$(echo $IP | tr '.' '_')*/*.ts 2>/dev/null | wc -l) segments"
    done
fi
