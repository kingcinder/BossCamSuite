#!/usr/bin/env bash
# ── capture-eseecloud-boot.sh — MITM capture EseeCloud boot-time check-in ──
#
# Reboots a camera with EseeCloud/P2P enabled while ARP-spoofing it through
# our machine so we can capture the boot-time cloud check-in protocol in a
# pcap file for analysis.  Afterwards, disables EseeCloud on the camera so
# it doesn't keep phoning home.
#
# Usage:
#   sudo ./scripts/capture-eseecloud-boot.sh 10.0.0.227
#   sudo ./scripts/capture-eseecloud-boot.sh 10.0.0.227 admin mypassword
#   sudo ./scripts/capture-eseecloud-boot.sh --no-disable 10.0.0.227
#
# Env vars:
#   CAMERA_USER           username                  (default admin)
#   CAMERA_PASS           password                  (default blank)
#   CAMERA_PORT           HTTP port                 (default 80)
#   CAMERA_CAPTURE_DIR    output directory          (default ./captures)
#   CAMERA_BOOT_WAIT      seconds to wait for boot  (default 90)
#   CAMERA_POST_BOOT      extra capture after boot  (default 30)
#   CAMERA_DISABLE_ESEE   1 to disable after capture (default 1)
#
# Prerequisites:
#   - bettercap  (for ARP spoofing)
#   - tcpdump    (for packet capture)
#   - root/sudo  (required for ARP spoofing + raw sockets)
#
# The pcap is saved to $CAMERA_CAPTURE_DIR/eseecloud-boot-<ip>-<ts>.pcap
# along with a metadata file listing timestamps and observed connections.

set -euo pipefail

# ── helpers ──────────────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
NC='\033[0m'

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }

log()   { printf "${CYAN}[%s]${NC} %s\n" "$(ts)" "$*"; }
pass()  { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(ts)" "$*"; }
fail()  { printf "${RED}[%s]  ✘ %s${NC}\n" "$(ts)" "$*"; }
warn()  { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(ts)" "$*"; }
info()  { printf "${MAGENTA}[%s]  ℹ %s${NC}\n" "$(ts)" "$*"; }

# ── privilege check ──────────────────────────────────────────────────────

if [[ $EUID -ne 0 ]]; then
    echo "This script must be run as root (sudo) for ARP spoofing + tcpdump."
    echo ""
    echo "  sudo ./scripts/capture-eseecloud-boot.sh <camera-ip>"
    exit 1
fi

# ── args / env ───────────────────────────────────────────────────────────

DISABLE_ESEE="${CAMERA_DISABLE_ESEE:-1}"

# Parse flags
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-disable) DISABLE_ESEE=0; shift ;;
    *)            break ;;
  esac
done

IP="${1:-}"
USER="${CAMERA_USER:-admin}"
PASS="${CAMERA_PASS:-}"
PORT="${CAMERA_PORT:-80}"
CAPTURE_DIR="${CAMERA_CAPTURE_DIR:-./captures}"
BOOT_WAIT="${CAMERA_BOOT_WAIT:-90}"
POST_BOOT="${CAMERA_POST_BOOT:-30}"

if [[ -z "$IP" ]]; then
  echo "Usage: $0 [--no-disable] <camera-ip> [username] [password]"
  echo ""
  echo "  --no-disable   Skip disabling EseeCloud after capture"
  echo ""
  echo "Env vars: CAMERA_USER CAMERA_PASS CAMERA_PORT"
  echo "         CAMERA_CAPTURE_DIR CAMERA_BOOT_WAIT CAMERA_POST_BOOT"
  echo "         CAMERA_DISABLE_ESEE"
  exit 1
fi

if [[ $# -ge 2 ]]; then USER="$2"; fi
if [[ $# -ge 3 ]]; then PASS="$3"; fi

BASE="http://${IP}:${PORT}"
AUTH="${USER}:${PASS}"
NOW_TS=$(date -u '+%Y%m%dT%H%M%SZ')
SESSION_DIR="${CAPTURE_DIR}/eseecloud-${IP}-${NOW_TS}"

# ── setup ────────────────────────────────────────────────────────────────

cleanup() {
    local exit_code=$?
    log "Cleaning up..."

    # Stop tcpdump
    if [[ -n "${TCPDUMP_PID:-}" ]] && kill -0 "$TCPDUMP_PID" 2>/dev/null; then
        kill "$TCPDUMP_PID" 2>/dev/null || true
        wait "$TCPDUMP_PID" 2>/dev/null || true
        pass "tcpdump stopped."
    fi

    # Stop bettercap
    if [[ -n "${BETTERCAP_PID:-}" ]] && kill -0 "$BETTERCAP_PID" 2>/dev/null; then
        kill "$BETTERCAP_PID" 2>/dev/null || true
        wait "$BETTERCAP_PID" 2>/dev/null || true
        pass "bettercap stopped."
    fi

    # Disable IP forwarding (restore)
    if [[ -f /proc/sys/net/ipv4/ip_forward ]]; then
        echo 0 > /proc/sys/net/ipv4/ip_forward 2>/dev/null || true
        info "IP forwarding disabled."
    fi

    # Remove iptables rules if we added any
    if [[ -n "${IPTABLES_RULES_ADDED:-}" ]]; then
        iptables -t nat -D PREROUTING -s "$IP" -p tcp --dport 80 -j REDIRECT --to-port 8080 2>/dev/null || true
        iptables -t nat -D PREROUTING -s "$IP" -p tcp --dport 443 -j REDIRECT --to-port 8080 2>/dev/null || true
        info "iptables redirect rules removed."
    fi

    # Write metadata
    {
        echo "capture_end: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
        echo "exit_code: $exit_code"
    } >> "$SESSION_DIR/metadata.txt"

    log "Capture session files in: ${SESSION_DIR}/"
    exit $exit_code
}

trap cleanup EXIT INT TERM

mkdir -p "$SESSION_DIR"
log "Capture session: ${SESSION_DIR}"

# Write initial metadata
cat > "$SESSION_DIR/metadata.txt" <<EOMETA
camera_ip: ${IP}
camera_port: ${PORT}
camera_user: ${USER}
capture_start: $(date -u '+%Y-%m-%dT%H:%M:%SZ')
gateway: $(ip route show default | awk '{print $3}')
our_ip: $(ip -4 addr show scope global | awk '/inet /{print $2}' | cut -d/ -f1 | head -1)
our_mac: $(ip link show | awk '/link\/ether/{print $2; exit}')
EOMETA

# ── pre-flight checks ────────────────────────────────────────────────────

GATEWAY=$(ip route show default | awk '{print $3}')
OUR_IP=$(ip -4 addr show scope global | awk '/inet /{print $2}' | cut -d/ -f1 | head -1)

log "Network topology:"
log "  Our IP:      ${OUR_IP}"
log "  Gateway:     ${GATEWAY}"
log "  Target cam:  ${IP}"
log ""

# Check camera is reachable
log "Pre-flight: checking camera ${IP}..."
if ! ping -c1 -W2 "$IP" >/dev/null 2>&1; then
    fail "Camera ${IP} is not pingable. Is it powered on and on the same network?"
    exit 1
fi
pass "Camera is reachable via ping."

# Check HTTP
code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u "$AUTH" \
    "${BASE}/NetSDK/System/deviceInfo" 2>/dev/null || echo "000")
if [[ "$code" == "200" ]]; then
    pass "Camera HTTP API is healthy (deviceInfo 200)."
else
    warn "Camera HTTP API returned ${code} — continuing anyway."
fi

# Dump current device info for the record
log "Recording camera identity..."
curl -sS -m 5 -u "$AUTH" "${BASE}/NetSDK/System/deviceInfo" 2>/dev/null | \
    python3 -c "
import sys,json
d=json.load(sys.stdin)
print(f\"  Model: {d.get('model','?')}\")
print(f\"  Serial: {d.get('serialNumber','?')}\")
print(f\"  FW: {d.get('firmwareVersion','?')}\")
print(f\"  HW: {d.get('hardwareVersion','?')}\")
" 2>/dev/null >> "$SESSION_DIR/metadata.txt" || warn "  Could not read deviceInfo"

# ── Phase 1: Enable MITM ─────────────────────────────────────────────────

log ""
log "${GREEN}━━━ Phase 1: Enable MITM (ARP spoof + packet capture) ━━━${NC}"
log ""

# 1a. Enable IP forwarding
log "Enabling IP forwarding..."
echo 1 > /proc/sys/net/ipv4/ip_forward
pass "IP forwarding enabled."

# 1b. Write bettercap caplet
CAPLET="${SESSION_DIR}/arp-spoof.cap"
cat > "$CAPLET" <<EOCAP
# ARP spoof camera -> gateway so camera's outbound traffic passes through us
set arp.spoof.targets ${IP}
set arp.spoof.internal true
arp.spoof on

# Give ARP spoofing a moment to poison caches
sleep 1

# Log what we see
events.ignore endpoint.new
events.ignore endpoint.lost
EOCAP

log "Starting bettercap for ARP spoofing (${IP} → gateway ${GATEWAY})..."
bettercap -no-colors -silent -caplet "$CAPLET" &
BETTERCAP_PID=$!
sleep 3

if ! kill -0 "$BETTERCAP_PID" 2>/dev/null; then
    fail "bettercap failed to start."
    exit 1
fi
pass "bettercap running (PID ${BETTERCAP_PID})."

# Verify ARP spoof is active — check that we have the camera in our ARP table
# pointing to our MAC, and the gateway in the camera's perspective.
log "Verifying ARP cache state..."
if arp -n "$IP" 2>/dev/null | grep -q "$IP"; then
    pass "Camera ${IP} is in our ARP table."
else
    warn "Camera ${IP} not yet in ARP table — may take a moment."
fi

# 1c. Start tcpdump capture
PCAP_FILE="${SESSION_DIR}/eseecloud-boot-${IP}-${NOW_TS}.pcap"
log "Starting tcpdump capture → ${PCAP_FILE}"
log "  Filter: host ${IP} (all protocols, all ports)"

tcpdump -i any -w "$PCAP_FILE" -s 0 \
    "host ${IP}" \
    > "${SESSION_DIR}/tcpdump.log" 2>&1 &
TCPDUMP_PID=$!
sleep 1

if ! kill -0 "$TCPDUMP_PID" 2>/dev/null; then
    fail "tcpdump failed to start."
    exit 1
fi
pass "tcpdump running (PID ${TCPDUMP_PID})."

# ── Phase 2: Reboot the camera ───────────────────────────────────────────

log ""
log "${GREEN}━━━ Phase 2: Reboot camera ${IP} ━━━${NC}"
log ""

# Capture a snapshot of current connections before reboot for comparison
log "Recording pre-reboot connection state..."
{
    echo "=== pre-reboot connections ($(date -u '+%Y-%m-%dT%H:%M:%SZ')) ==="
    ss -tnp 2>/dev/null | grep "$IP" || echo "  (no established connections to camera)"
    echo ""
    echo "=== pre-reboot ARP table ==="
    arp -n 2>/dev/null || true
} >> "$SESSION_DIR/metadata.txt"

# Reboot using live-proven reboot paths from rtsp-enable.sh
log "Sending reboot command..."
log "  Trying documented reboot endpoints in order..."

REBOOT_OK=0
declare -a reboot_paths=(
    "/NetSDK/System/operation/reboot:PUT"
    "/netsdk/Reboot:PUT"
    "/NetSDK/System/reboot:PUT"
    "/NetSDK/Factory?cmd=Reboot:GET"
)

for entry in "${reboot_paths[@]}"; do
    path="${entry%%:*}"
    method="${entry##*:}"
    reb_code=""

    if [[ "$method" == "PUT" ]]; then
        reb_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 10 -X PUT -u "$AUTH" \
            -H 'Content-Type: application/json' \
            -d '{"reboot":true}' \
            "${BASE}${path}" 2>/dev/null || echo "000")
    else
        reb_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 10 -u "$AUTH" \
            "${BASE}${path}" 2>/dev/null || echo "000")
    fi

    if [[ "$reb_code" == "200" || "$reb_code" == "202" ]]; then
        pass "  Reboot accepted via ${path} (HTTP ${reb_code})."
        REBOOT_OK=1
        break
    fi
    warn "  ${path} → HTTP ${reb_code} — trying next path..."
done

if [[ $REBOOT_OK -eq 0 ]]; then
    fail "All reboot paths failed. Is the camera still reachable?"
    fail "Capture file may still be useful — check ${PCAP_FILE}"
    exit 1
fi

# ── Phase 3: Wait for boot + capture check-in ────────────────────────────

log ""
log "${GREEN}━━━ Phase 3: Waiting for boot + EseeCloud check-in ━━━${NC}"
log ""

log "Marking reboot timestamp in capture..."
echo "reboot_sent: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"
echo "reboot_path: ${path}" >> "$SESSION_DIR/metadata.txt"

log "Camera is rebooting. Watching for it to come back..."
log "  Boot wait: ${BOOT_WAIT}s  |  Post-boot capture: ${POST_BOOT}s"

# Poll for camera to come back
CAMERA_BACK=0
waited=0
while [[ $waited -lt $BOOT_WAIT ]]; do
    sleep 2
    waited=$((waited + 2))

    if ping -c1 -W1 "$IP" >/dev/null 2>&1; then
        pass "Camera is pingable after ${waited}s — boot complete."
        CAMERA_BACK=1
        break
    fi

    # Progress every 10s
    if [[ $((waited % 10)) -eq 0 ]]; then
        info "  ... waiting for camera (${waited}s elapsed) ..."
    fi
done

if [[ $CAMERA_BACK -eq 0 ]]; then
    fail "Camera did not come back within ${BOOT_WAIT}s."
    fail "Capture file may still contain the shutdown sequence — check ${PCAP_FILE}"
    exit 1
fi

echo "camera_back_online: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"
echo "camera_back_after_seconds: ${waited}" >> "$SESSION_DIR/metadata.txt"

# Wait for HTTP API to come up (may lag behind ping)
log "Waiting for HTTP API to become available..."
api_waited=0
while [[ $api_waited -lt 30 ]]; do
    sleep 3
    api_waited=$((api_waited + 3))
    code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u "$AUTH" \
        "${BASE}/NetSDK/System/deviceInfo" 2>/dev/null || echo "000")
    if [[ "$code" == "200" ]]; then
        pass "HTTP API available after ${api_waited}s."
        break
    fi
done

echo "http_api_available: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"

# Continue capturing for POST_BOOT more seconds — the EseeCloud check-in
# typically happens within 10-30s after the HTTP stack comes up.
log "Continuing capture for ${POST_BOOT}s to catch EseeCloud check-in..."
log "  (EseeCloud typically phones home 10-30s after HTTP stack initialises)"
sleep "$POST_BOOT"

# Record post-boot connection state
{
    echo ""
    echo "=== post-boot connections ($(date -u '+%Y-%m-%dT%H:%M:%SZ')) ==="
    ss -tnp 2>/dev/null | grep "$IP" || echo "  (no established connections from camera)"
    echo ""
    echo "=== post-boot ARP table ==="
    arp -n 2>/dev/null || true
} >> "$SESSION_DIR/metadata.txt"

# ── Phase 4: Stop capture ────────────────────────────────────────────────

log ""
log "${GREEN}━━━ Phase 4: Stop capture ━━━${NC}"
log ""

# Stop tcpdump
if kill -0 "$TCPDUMP_PID" 2>/dev/null; then
    kill "$TCPDUMP_PID" 2>/dev/null || true
    wait "$TCPDUMP_PID" 2>/dev/null || true
    pass "tcpdump stopped."
fi
TCPDUMP_PID=""

# Print capture stats
PCAP_SIZE=$(stat -c%s "$PCAP_FILE" 2>/dev/null || echo "0")
PCAP_PACKETS=$(tcpdump -r "$PCAP_FILE" 2>/dev/null | wc -l || echo "?")
log "Capture file: ${PCAP_FILE}"
log "  Size:    $(numfmt --to=iec "${PCAP_SIZE}" 2>/dev/null || echo "${PCAP_SIZE} bytes")"
log "  Packets: ${PCAP_PACKETS}"

# Quick summary of observed protocols
log "Protocol breakdown:"
tcpdump -r "$PCAP_FILE" -nn 2>/dev/null | \
    awk '{for(i=1;i<=NF;i++) if($i~/^(TCP|UDP|ICMP|ARP|DNS|HTTP|TLS|SSL)/) {print $i; next}}' | \
    sort | uniq -c | sort -rn | head -10 >> "$SESSION_DIR/metadata.txt" || true

# Show unique destination IPs the camera talked to
log "Unique destinations camera contacted during boot:"
tcpdump -r "$PCAP_FILE" -nn 2>/dev/null | \
    awk '{for(i=1;i<=NF;i++) if($i ~ /^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+/) print $i}' | \
    grep -vE "^(${IP}|${OUR_IP}|${GATEWAY}|255\.|0\.0\.0\.0|127\.)" | \
    sort -u | while read -r dest; do
        info "  → ${dest}"
        echo "observed_destination: ${dest}" >> "$SESSION_DIR/metadata.txt"
    done

# Stop bettercap
if kill -0 "$BETTERCAP_PID" 2>/dev/null; then
    kill "$BETTERCAP_PID" 2>/dev/null || true
    wait "$BETTERCAP_PID" 2>/dev/null || true
    pass "bettercap stopped."
fi
BETTERCAP_PID=""

# Disable IP forwarding
echo 0 > /proc/sys/net/ipv4/ip_forward 2>/dev/null || true
info "IP forwarding disabled."

# ── Phase 5: Disable EseeCloud on camera ─────────────────────────────────

if [[ $DISABLE_ESEE -eq 1 ]]; then
    log ""
    log "${GREEN}━━━ Phase 5: Disable EseeCloud on camera ${IP} ━━━${NC}"
    log ""

    disable_eseecloud
else
    log ""
    warn "Skipping EseeCloud disable (--no-disable / CAMERA_DISABLE_ESEE=0)."
    warn "Camera ${IP} is still phoning home to EseeCloud!"
fi

# ── done ─────────────────────────────────────────────────────────────────

log ""
log "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
log "${GREEN}✓ Capture complete!${NC}"
log "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
log ""
log "Files:"
log "  PCAP:    ${PCAP_FILE}"
log "  Meta:    ${SESSION_DIR}/metadata.txt"
log "  TCP log: ${SESSION_DIR}/tcpdump.log"
log ""
log "To analyze the capture:"
log "  wireshark ${PCAP_FILE}"
log "  tcpdump -r ${PCAP_FILE} -nn -A | less"
log "  tcpdump -r ${PCAP_FILE} -nn 'tcp and not port 80 and not port 554'"
log ""

# ── disable_eseecloud ────────────────────────────────────────────────────

# Tries multiple strategies to disable EseeCloud/P2P cloud service on the
# camera. The exact endpoint varies by firmware generation; we try known
# paths and report which one worked.
disable_eseecloud() {
    log "Attempting to disable EseeCloud/P2P cloud service..."

    local disabled=0

    # Strategy A: NetSDK P2P config endpoint (most common on 5523-W)
    log "  Strategy A: PUT /NetSDK/Network/p2p to disable..."
    local p2p_code
    p2p_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 10 -X PUT -u "$AUTH" \
        -H 'Content-Type: application/json' \
        -d '{"enabled":false,"service":"EseeCloud"}' \
        "${BASE}/NetSDK/Network/p2p" 2>/dev/null || echo "000")

    if [[ "$p2p_code" == "200" ]]; then
        pass "  EseeCloud disabled via /NetSDK/Network/p2p (HTTP 200)."
        disabled=1
    else
        warn "  /NetSDK/Network/p2p → HTTP ${p2p_code}"
    fi

    # Strategy B: PUT EseeCloud settings with explicit disable
    if [[ $disabled -eq 0 ]]; then
        log "  Strategy B: PUT /NetSDK/Network/EseeCloud..."
        local esee_code
        esee_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 10 -X PUT -u "$AUTH" \
            -H 'Content-Type: application/json' \
            -d '{"enable":false,"alive":false}' \
            "${BASE}/NetSDK/Network/EseeCloud" 2>/dev/null || echo "000")

        if [[ "$esee_code" == "200" ]]; then
            pass "  EseeCloud disabled via /NetSDK/Network/EseeCloud (HTTP 200)."
            disabled=1
        else
            warn "  /NetSDK/Network/EseeCloud → HTTP ${esee_code}"
        fi
    fi

    # Strategy C: Some firmware uses a service management endpoint
    if [[ $disabled -eq 0 ]]; then
        log "  Strategy C: PUT /NetSDK/System/service to disable cloud..."
        local svc_code
        svc_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 10 -X PUT -u "$AUTH" \
            -H 'Content-Type: application/json' \
            -d '{"cloudService":false,"p2pEnabled":false}' \
            "${BASE}/NetSDK/System/service" 2>/dev/null || echo "000")

        if [[ "$svc_code" == "200" ]]; then
            pass "  EseeCloud disabled via /NetSDK/System/service (HTTP 200)."
            disabled=1
        else
            warn "  /NetSDK/System/service → HTTP ${svc_code}"
        fi
    fi

    # Strategy D: Try the NetSDK alarm/cloud endpoint
    if [[ $disabled -eq 0 ]]; then
        log "  Strategy D: PUT /NetSDK/Alarm/cloud to disable..."
        local alarm_code
        alarm_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 10 -X PUT -u "$AUTH" \
            -H 'Content-Type: application/json' \
            -d '{"enable":false,"platform":"EseeCloud"}' \
            "${BASE}/NetSDK/Alarm/cloud" 2>/dev/null || echo "000")

        if [[ "$alarm_code" == "200" ]]; then
            pass "  EseeCloud disabled via /NetSDK/Alarm/cloud (HTTP 200)."
            disabled=1
        else
            warn "  /NetSDK/Alarm/cloud → HTTP ${alarm_code}"
        fi
    fi

    if [[ $disabled -eq 1 ]]; then
        pass "EseeCloud has been disabled on camera ${IP}."
        echo "eseecloud_disabled: true" >> "$SESSION_DIR/metadata.txt"
        echo "eseecloud_disabled_at: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"
    else
        warn "Could not disable EseeCloud through any known endpoint."
        warn "You may need to disable it manually via the camera's web UI or mobile app."
        warn "Known approaches that were tried:"
        warn "  - /NetSDK/Network/p2p"
        warn "  - /NetSDK/Network/EseeCloud"
        warn "  - /NetSDK/System/service"
        warn "  - /NetSDK/Alarm/cloud"
        echo "eseecloud_disabled: false" >> "$SESSION_DIR/metadata.txt"
        echo "eseecloud_disable_tried: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"
    fi
}
