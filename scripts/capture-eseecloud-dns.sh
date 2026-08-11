#!/usr/bin/env bash
# ── capture-eseecloud-dns.sh ────────────────────────────────────────────
# DNS redirect approach: point the camera's DNS to our machine, run a fake
# EseeCloud server, reboot the camera, and capture the full protocol
# conversation at the application layer — no ARP spoofing required.
#
# Usage:
#   sudo ./scripts/capture-eseecloud-dns.sh 10.0.0.227
#   sudo ./scripts/capture-eseecloud-dns.sh 10.0.0.227 admin mypassword
#   sudo ./scripts/capture-eseecloud-dns.sh --no-restore 10.0.0.227
#
# Env vars:
#   CAMERA_USER           username                  (default admin)
#   CAMERA_PASS           password                  (default blank)
#   CAMERA_PORT           HTTP port                 (default 80)
#   CAMERA_CAPTURE_DIR    output directory          (default ./captures)
#   CAMERA_BOOT_WAIT      seconds to wait for boot  (default 90)
#   CAMERA_POST_BOOT      extra capture after boot  (default 45)
#   CAMERA_DNS_PORT       port for our DNS server   (default 5353)
#   CAMERA_RESTORE_DNS    1 to restore original DNS (default 1)
#
# Prerequisites:
#   - Python 3.7+ with asyncio
#   - root/sudo (for iptables and binding ports)
#
# Architecture:
#   1. Read camera's current DNS config via NetSDK REST (for restore later)
#   2. Set camera DNS to our IP (10.0.0.149) via PUT /NetSDK/Network/dns
#   3. Add iptables REDIRECT: camera DNS queries → our Python DNS server
#   4. Start eseecloud-dns-server.py (DNS interceptor + fake TCP servers)
#   5. Reboot the camera
#   6. Wait for EseeCloud check-in connections to our fake server
#   7. Capture all protocol data in hex dumps + binary log
#   8. Restore camera's original DNS config
#   9. Tear down iptables rules and server
#
# Output:
#   captures/eseecloud-dns-<ip>-<ts>/
#     ├── dns-queries.log            All DNS queries with actions
#     ├── eseecloud-connections.log  Connection events + hex dumps
#     ├── eseecloud-data.bin         Raw binary protocol data
#     └── metadata.txt               Capture session info

set -euo pipefail

# ── script dir (for locating the Python server) ───────────────────────
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

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
    echo "This script must be run as root (sudo) for iptables and port binding."
    echo ""
    echo "  sudo ./scripts/capture-eseecloud-dns.sh <camera-ip>"
    exit 1
fi

# ── args / env ───────────────────────────────────────────────────────────

RESTORE_DNS=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-restore) RESTORE_DNS=0; shift ;;
    *)            break ;;
  esac
done

IP="${1:-}"
USER="${CAMERA_USER:-admin}"
PASS="${CAMERA_PASS:-}"
PORT="${CAMERA_PORT:-80}"
CAPTURE_DIR="${CAMERA_CAPTURE_DIR:-./captures}"
BOOT_WAIT="${CAMERA_BOOT_WAIT:-90}"
POST_BOOT="${CAMERA_POST_BOOT:-45}"
DNS_PORT="${CAMERA_DNS_PORT:-5353}"

if [[ -z "$IP" ]]; then
  echo "Usage: $0 [--no-restore] <camera-ip> [username] [password]"
  echo ""
  echo "  --no-restore   Don't restore original DNS after capture"
  echo ""
  echo "Env vars: CAMERA_USER CAMERA_PASS CAMERA_PORT"
  echo "         CAMERA_CAPTURE_DIR CAMERA_BOOT_WAIT CAMERA_POST_BOOT"
  echo "         CAMERA_DNS_PORT CAMERA_RESTORE_DNS"
  exit 1
fi

if [[ $# -ge 2 ]]; then USER="$2"; fi
if [[ $# -ge 3 ]]; then PASS="$3"; fi

BASE="http://${IP}:${PORT}"
AUTH="${USER}:${PASS}"
NOW_TS=$(date -u '+%Y%m%dT%H%M%SZ')
SESSION_DIR="${CAPTURE_DIR}/eseecloud-dns-${IP}-${NOW_TS}"

# Determine our IP
OUR_IP=$(ip -4 addr show scope global | awk '/inet /{print $2}' | cut -d/ -f1 | head -1)
if [[ -z "$OUR_IP" ]]; then
    fail "Cannot determine our IP address."
    exit 1
fi

# ── cleanup ──────────────────────────────────────────────────────────────

ORIGINAL_DNS=""
SERVER_PID=""

cleanup() {
    local exit_code=$?
    log "Cleaning up..."

    # Stop the Python server
    if [[ -n "${SERVER_PID:-}" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
        pass "DNS server stopped."
    fi

    # Remove iptables redirect rule
    iptables -t nat -D PREROUTING -s "$IP" -p udp --dport 53 -j REDIRECT --to-port "$DNS_PORT" 2>/dev/null || true
    info "iptables DNS redirect removed."

    # Restore original DNS config
    if [[ $RESTORE_DNS -eq 1 && -n "${ORIGINAL_DNS:-}" ]]; then
        log "Restoring original DNS config..."
        local restore_code
        restore_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -X PUT -u "$AUTH" \
            -H 'Content-Type: application/json' \
            -d "$ORIGINAL_DNS" \
            "${BASE}/NetSDK/Network/dns" 2>/dev/null || echo "000")
        if [[ "$restore_code" == "200" ]]; then
            pass "Original DNS restored."
        else
            warn "Could not restore original DNS (HTTP ${restore_code})."
            warn "  Original was: $ORIGINAL_DNS"
            warn "  Restore manually: curl -X PUT -u '$USER:$PASS' -H 'Content-Type: application/json' -d '$ORIGINAL_DNS' '${BASE}/NetSDK/Network/dns'"
        fi
    elif [[ -n "${ORIGINAL_DNS:-}" ]]; then
        warn "Original DNS NOT restored (--no-restore)."
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

# ── setup ────────────────────────────────────────────────────────────────

mkdir -p "$SESSION_DIR"

cat > "$SESSION_DIR/metadata.txt" <<EOMETA
camera_ip: ${IP}
camera_port: ${PORT}
camera_user: ${USER}
our_ip: ${OUR_IP}
dns_port: ${DNS_PORT}
capture_start: $(date -u '+%Y-%m-%dT%H:%M:%SZ')
approach: DNS redirect (no ARP spoofing)
EOMETA

# ── Phase 0: Pre-flight ──────────────────────────────────────────────────

log "═══════════════════════════════════════════"
log "  EseeCloud DNS Redirect Capture"
log "═══════════════════════════════════════════"
log ""
log "  Camera:  ${IP}:${PORT}"
log "  Our IP:  ${OUR_IP}"
log "  Session: ${SESSION_DIR}"
log ""

log "Phase 0: Pre-flight checks..."

# Check camera is reachable
if ! ping -c1 -W2 "$IP" >/dev/null 2>&1; then
    fail "Camera ${IP} is not pingable."
    exit 1
fi
pass "Camera is reachable via ping."

# Check HTTP API
code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u "$AUTH" \
    "${BASE}/NetSDK/System/deviceInfo" 2>/dev/null || echo "000")
if [[ "$code" == "200" ]]; then
    pass "Camera HTTP API is healthy (deviceInfo 200)."
else
    fail "Camera HTTP API returned ${code}. Cannot proceed."
    exit 1
fi

# Read current DNS config
log "Reading current DNS config..."
ORIGINAL_DNS=$(curl -sS -m 5 -u "$AUTH" \
    "${BASE}/NetSDK/Network/dns" 2>/dev/null || echo '{"preferredDns":"unknown"}')
pass "Current DNS: $(echo "$ORIGINAL_DNS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('preferredDns','?'))" 2>/dev/null || echo '?')"
echo "original_dns: ${ORIGINAL_DNS}" >> "$SESSION_DIR/metadata.txt"

# Record device identity
log "Recording camera identity..."
curl -sS -m 5 -u "$AUTH" "${BASE}/NetSDK/System/deviceInfo" 2>/dev/null | \
    python3 -c "
import sys,json
d=json.load(sys.stdin)
print(f\"  Model: {d.get('model','?')}\")
print(f\"  Serial: {d.get('serialNumber','?')}\")
print(f\"  FW: {d.get('firmwareVersion','?')}\")
" 2>/dev/null >> "$SESSION_DIR/metadata.txt" || true

# ── Phase 1: Redirect DNS ────────────────────────────────────────────────

log ""
log "${GREEN}━━━ Phase 1: Redirect camera DNS → our machine ━━━${NC}"
log ""

# 1a. Set camera DNS to our IP
log "Setting camera DNS to ${OUR_IP}..."
dns_payload="{\"preferredDns\":\"${OUR_IP}\",\"staticAlternateDns\":\"8.8.8.8\"}"
dns_code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -X PUT -u "$AUTH" \
    -H 'Content-Type: application/json' \
    -d "$dns_payload" \
    "${BASE}/NetSDK/Network/dns" 2>/dev/null || echo "000")

if [[ "$dns_code" == "200" ]]; then
    pass "Camera DNS set to ${OUR_IP}."
else
    fail "Failed to set camera DNS (HTTP ${dns_code})."
    exit 1
fi

# Verify
verify_dns=$(curl -sS -m 5 -u "$AUTH" \
    "${BASE}/NetSDK/Network/dns" 2>/dev/null | \
    python3 -c "import sys,json; print(json.load(sys.stdin).get('preferredDns',''))" 2>/dev/null)
if [[ "$verify_dns" == "$OUR_IP" ]]; then
    pass "Verified: camera DNS = ${OUR_IP}."
else
    warn "Verification returned '${verify_dns}' — continuing anyway."
fi

# 1b. Add iptables redirect: camera DNS traffic → our DNS interceptor
log "Adding iptables redirect: ${IP}:53 → :${DNS_PORT}..."
iptables -t nat -A PREROUTING -s "$IP" -p udp --dport 53 -j REDIRECT --to-port "$DNS_PORT" 2>/dev/null || {
    fail "Failed to add iptables rule."
    exit 1
}
pass "iptables redirect rule added."

# Verify iptables rule
iptables -t nat -L PREROUTING -n 2>/dev/null | grep -q "$IP" && \
    pass "iptables rule verified." || \
    warn "iptables rule may not be active."

# ── Phase 2: Start fake EseeCloud server ─────────────────────────────────

log ""
log "${GREEN}━━━ Phase 2: Start DNS interceptor + fake EseeCloud servers ━━━${NC}"
log ""

log "Starting eseecloud-dns-server.py..."
python3 "$SCRIPT_DIR/eseecloud-dns-server.py" \
    --our-ip "$OUR_IP" \
    --dns-port "$DNS_PORT" \
    --upstream 8.8.8.8 \
    --log-dir "$SESSION_DIR" \
    > "$SESSION_DIR/server.log" 2>&1 &
SERVER_PID=$!

sleep 2
if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    fail "DNS server failed to start. Check ${SESSION_DIR}/server.log"
    cat "$SESSION_DIR/server.log" 2>/dev/null
    exit 1
fi
pass "DNS interceptor + fake servers running (PID ${SERVER_PID})."

# ── Phase 3: Reboot camera ───────────────────────────────────────────────

log ""
log "${GREEN}━━━ Phase 3: Reboot camera ${IP} ━━━${NC}"
log ""

echo "reboot_sent: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"

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
        echo "reboot_path: ${path}" >> "$SESSION_DIR/metadata.txt"
        break
    fi
    warn "  ${path} → HTTP ${reb_code} — trying next path..."
done

if [[ $REBOOT_OK -eq 0 ]]; then
    fail "All reboot paths failed."
    fail "Check ${SESSION_DIR}/server.log for any connections."
    exit 1
fi

# ── Phase 4: Wait for boot + EseeCloud check-in ──────────────────────────

log ""
log "${GREEN}━━━ Phase 4: Waiting for boot + EseeCloud check-in ━━━${NC}"
log ""

log "Camera is rebooting. Watching for check-in connections..."
log "  Boot wait: ${BOOT_WAIT}s  |  Post-boot capture: ${POST_BOOT}s"
log ""

# Poll for camera to come back
CAMERA_BACK=0
waited=0
while [[ $waited -lt $BOOT_WAIT ]]; do
    sleep 2
    waited=$((waited + 2))

    if ping -c1 -W1 "$IP" >/dev/null 2>&1; then
        pass "Camera pingable after ${waited}s."
        CAMERA_BACK=1
        break
    fi

    if [[ $((waited % 10)) -eq 0 ]]; then
        info "  ... waiting (${waited}s) ..."
    fi

    # Check if any connections have already arrived
    if [[ -f "$SESSION_DIR/eseecloud-connections.log" ]]; then
        conns=$(grep -c "CONNECT" "$SESSION_DIR/eseecloud-connections.log" 2>/dev/null || echo "0")
        if [[ "$conns" -gt 0 ]]; then
            info "  ${conns} connection(s) already received!"
        fi
    fi
done

if [[ $CAMERA_BACK -eq 0 ]]; then
    fail "Camera did not come back within ${BOOT_WAIT}s."
    fail "Check ${SESSION_DIR}/eseecloud-connections.log"
    exit 1
fi

echo "camera_back_online: $(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$SESSION_DIR/metadata.txt"
echo "camera_back_after_seconds: ${waited}" >> "$SESSION_DIR/metadata.txt"

# Wait for HTTP API
log "Waiting for HTTP API..."
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

# Continue capturing for POST_BOOT more seconds
log "Continuing capture for ${POST_BOOT}s to catch EseeCloud check-in..."
log "  (EseeCloud typically phones home 10-30s after HTTP stack initialises)"
log ""

for ((i = 0; i < POST_BOOT; i += 5)); do
    sleep 5

    # Show connection count
    if [[ -f "$SESSION_DIR/eseecloud-connections.log" ]]; then
        conns=$(grep -c "CONNECT" "$SESSION_DIR/eseecloud-connections.log" 2>/dev/null || echo "0")
        data_lines=$(grep -c "DATA" "$SESSION_DIR/eseecloud-connections.log" 2>/dev/null || echo "0")
        if [[ "$conns" -gt 0 || "$data_lines" -gt 0 ]]; then
            info "  ${conns} connections, ${data_lines} data chunks received so far..."
        fi
    fi
done

# ── Phase 5: Summary ─────────────────────────────────────────────────────

log ""
log "${GREEN}━━━ Phase 5: Capture summary ━━━${NC}"
log ""

# Count connections and data
if [[ -f "$SESSION_DIR/eseecloud-connections.log" ]]; then
    total_conns=$(grep -c "CONNECT" "$SESSION_DIR/eseecloud-connections.log" 2>/dev/null || echo "0")
    total_data=$(grep -c "DATA" "$SESSION_DIR/eseecloud-connections.log" 2>/dev/null || echo "0")
    dns_redirects=$(grep -c "REDIRECT" "$SESSION_DIR/dns-queries.log" 2>/dev/null || echo "0")
    dns_forwarded=$(grep -c "FORWARD" "$SESSION_DIR/dns-queries.log" 2>/dev/null || echo "0")

    pass "Capture results:"
    log "  DNS queries redirected:  ${dns_redirects}"
    log "  DNS queries forwarded:   ${dns_forwarded}"
    log "  EseeCloud connections:   ${total_conns}"
    log "  Data chunks received:    ${total_data}"

    if [[ "$total_data" -gt 0 ]]; then
        bin_size=$(stat -c%s "$SESSION_DIR/eseecloud-data.bin" 2>/dev/null || echo "0")
        log "  Binary data captured:    $(numfmt --to=iec "${bin_size}" 2>/dev/null || echo "${bin_size} bytes")"
    fi
else
    warn "No EseeCloud connections captured."
    warn "The camera may not be using DNS to find EseeCloud (hardcoded IPs)."
    warn "Check ${SESSION_DIR}/dns-queries.log for DNS activity."
fi

# Show DNS activity
log ""
log "DNS query log (first 20 lines):"
head -20 "$SESSION_DIR/dns-queries.log" 2>/dev/null || echo "  (empty)"

# Show EseeCloud connections
log ""
log "EseeCloud connections:"
grep -E "CONNECT|DATA|DISCONNECT" "$SESSION_DIR/eseecloud-connections.log" 2>/dev/null | head -20 || echo "  (none)"

# ── done ─────────────────────────────────────────────────────────────────

log ""
log "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
log "${GREEN}✓ DNS redirect capture complete!${NC}"
log "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
log ""
log "Files:"
log "  DNS queries:    ${SESSION_DIR}/dns-queries.log"
log "  Connections:    ${SESSION_DIR}/eseecloud-connections.log"
log "  Binary data:    ${SESSION_DIR}/eseecloud-data.bin"
log "  Server log:     ${SESSION_DIR}/server.log"
log "  Metadata:       ${SESSION_DIR}/metadata.txt"
log ""
log "To parse the EseeCloud protocol:"
log "  python3 scripts/eseecloud-parser.py analyze \\"
log "    --camera-ip ${IP} \\"
log "    ${SESSION_DIR}/eseecloud-data.bin   # (if converted to pcap first)"
log ""
log "To forge from captured data:"
log "  python3 scripts/eseecloud-forge.py analyze \\"
log "    --camera-ip ${IP} --auto \\"
log "    (first convert the binary log for pcap analysis)"
log ""
