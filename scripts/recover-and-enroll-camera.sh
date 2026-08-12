#!/usr/bin/env bash
# ── recover-and-enroll-camera.sh — factory-reset camera → LAN → BossCamSuite ──
#
# THE PROPER RECOVERY PROCEDURE, in one command. A factory reset wipes the
# camera's WiFi station credentials AND the admin password, so the camera drops
# off the LAN and instead broadcasts its OWN access point (SSID "IPC" + serial
# without the "JA" prefix, e.g. JAZ7C34781634738 -> AP IPCZ7C34781634738).
#
# This script chains the two proven halves:
#   1. 5523w-wifi-reprovision.sh  — join the camera's AP, verify factory state
#      (blank admin), write station-mode WiFi config, camera rejoins the LAN,
#      rediscover by MAC, verify blank admin on the new LAN IP.
#   2. Suite enrollment            — POST /api/devices/enroll against the local
#      BossCam service (blank admin), start continuous recording, verify a live
#      snapshot, and append to the recovery ledger.
#
# Usage:
#   ./scripts/recover-and-enroll-camera.sh --list                 # scan only
#   ./scripts/recover-and-enroll-camera.sh                        # auto: recover the ONLY visible camera AP
#   ./scripts/recover-and-enroll-camera.sh JAZ7C34781634738       # by serial
#   ./scripts/recover-and-enroll-camera.sh IPCZ7C34781634738      # by exact AP SSID
#   ./scripts/recover-and-enroll-camera.sh --enroll-only 10.0.0.169  # camera already on LAN; enroll only
#   ./scripts/recover-and-enroll-camera.sh --dry-run JAZ7C34781634738  # print the plan, change nothing
#
# Env overrides (passed through to the reprovision phase):
#   STA_SSID / STA_PASS          our WiFi network the camera should join (default Aegon/812354444)
#   CAM_MAC_PREFIX               camera OUI for LAN rediscovery (default 9c:a3:a9)
#   SUBNET                       LAN subnet to sweep (default 10.0.0)
#   AP_PASS                      camera-AP password (default: try open + factory defaults)
#   RECORD=0                     do NOT start continuous recording after enroll (default 1)
#   API=http://127.0.0.1:5317    BossCamSuite API base URL
#   REPRO_SCRIPT=scripts/5523w-wifi-reprovision.sh
#   LEDGER_DIR=local-camera-recovery/ledger
#   DRY_RUN=1                    print actions, change nothing
#
# Requires: nmcli, curl, python3. Does NOT need root.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API="${API:-http://127.0.0.1:5317}"
REPRO_SCRIPT="${REPRO_SCRIPT:-$ROOT/scripts/5523w-wifi-reprovision.sh}"
LEDGER_DIR="${LEDGER_DIR:-$ROOT/local-camera-recovery/ledger}"
RECORD="${RECORD:-1}"
DRY_RUN="${DRY_RUN:-0}"
STA_SSID="${STA_SSID:-Aegon}"
STA_PASS="${STA_PASS:-812354444}"
REPRO_OUT="$(mktemp /tmp/bosscam-recover-XXXXXX.ip)"
trap 'rm -f "$REPRO_OUT"' EXIT

RED=$'\e[0;31m'; GREEN=$'\e[0;32m'; YELLOW=$'\e[1;33m'; BLUE=$'\e[0;36m'; NC=$'\e[0m'
log()   { printf "${BLUE}[%s]${NC} %s\n" "$(date -u +%H:%M:%SZ)" "$*"; }
pass()  { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
warn()  { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
fail()  { printf "${RED}[%s]  ✘ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
banner(){ printf "${BLUE}[%s]${NC} %s\n" "$(date -u +%H:%M:%SZ)" "$*"; }

command -v nmcli  >/dev/null || { fail "nmcli required"; exit 1; }
command -v curl   >/dev/null || { fail "curl required";  exit 1; }
command -v python3 >/dev/null || { fail "python3 required"; exit 1; }
[ -x "$REPRO_SCRIPT" ] || { fail "reprovision tool not executable: $REPRO_SCRIPT"; exit 1; }

# ── helpers ────────────────────────────────────────────────────────────────
serial_from_ap() { # $1=AP SSID (IPCZ7C34...) -> JAZ7C34... (canonical serial)
  case "$1" in
    IPC*) echo "JA${1#IPC}" ;;
    *)    echo "$1" ;;
  esac
}

enroll_and_record() { # $1=ip $2=serial ; POST enroll, optionally start recording
  local ip="$1" serial="$2" name
  name="5523-W-${serial#JA}"
  log "enrolling $name at $ip (blank admin) via $API ..."
  local resp device_id enrolled
  if [ "$DRY_RUN" = "1" ]; then
    log "  [dry] POST $API/api/devices/enroll {ip:$ip, loginName:admin, password:'', model:5523-W}"
    echo ""
    return 0
  fi
  resp=$(curl -sS -m 45 -X POST "$API/api/devices/enroll" \
    -H 'Content-Type: application/json' \
    -d "{\"ipAddress\":\"$ip\",\"port\":80,\"loginName\":\"admin\",\"password\":\"\",\"displayName\":\"$name\",\"hardwareModel\":\"5523-W\",\"startContinuousRecord\":$([ "$RECORD" = "1" ] && echo true || echo false)}" 2>&1 || true)
  device_id=$(printf '%s' "$resp" | python3 -c 'import sys,json
try: print(json.load(sys.stdin).get("deviceId",""))
except Exception: print("")' 2>/dev/null || true)
  enrolled=$(printf '%s' "$resp" | python3 -c 'import sys,json
try: print("true" if json.load(sys.stdin).get("enrolled") else "false")
except Exception: print("false")' 2>/dev/null || true)
  if [ "$enrolled" = "true" ] && [ -n "$device_id" ]; then
    pass "enrolled: deviceId=$device_id"
    # Also fire the recording-start endpoint explicitly (policy reconciles on a
    # cycle; starting now removes the wait).
    if [ "$RECORD" = "1" ]; then
      local rec
      rec=$(curl -sS -m 30 -X POST "$API/api/recordings/start" \
        -H 'Content-Type: application/json' -d "{\"deviceId\":\"$device_id\"}" 2>/dev/null || true)
      printf '%s' "$rec" | python3 -c 'import sys,json
try:
    d=json.load(sys.stdin)
    print("  recording:", "running" if d.get("isRunning") else ("deferred: "+str(d.get("mode",""))) )
except Exception: pass' 2>/dev/null || true
    fi
    return 0
  fi
  warn "  enrollment response: $(printf '%s' "$resp" | head -c 300)"
  return 1
}

verify_live() { # $1=ip ; snapshot + live-manifest sanity
  local ip="$1" snap
  [ "$DRY_RUN" = "1" ] && { log "  [dry] verify snapshot + live-manifest at $ip"; return 0; }
  snap=$(mktemp /tmp/bosscam-recover-snap-XXXXXX.jpg 2>/dev/null || echo /tmp/bosscam-recover-snap.jpg)
  local code
  code=$(curl -sS -o "$snap" -w '%{http_code}' -m 8 -u 'admin:' \
    "http://$ip/NetSDK/Video/encode/channel/101/snapShot" 2>/dev/null || true)
  if [ "$code" = "200" ] && file "$snap" 2>/dev/null | grep -qi JPEG; then
    pass "live snapshot OK ($(stat -c%s "$snap" 2>/dev/null || echo '?') bytes)"
  else
    warn "snapshot returned HTTP $code — video may need a moment after the WiFi switch"
  fi
  rm -f "$snap"
}

ledger_append() { # $1=serial $2=ip $3=status $4=source
  local serial="$1" ip="$2" status="$3" src="$4" file line ts
  [ -n "$serial" ] || serial="unknown"
  file="$LEDGER_DIR/$serial.jsonl"
  ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  line=$(python3 - "$ts" "$serial" "$ip" "$status" "$src" <<'PYEOF'
import json, sys
ts, serial, ip, status, src = sys.argv[1:6]
print(json.dumps({"ts": ts, "serial": serial, "ip": ip, "status": status,
                  "source": src, "op": "recover-and-enroll"}, separators=(",", ":")))
PYEOF
) || line=""
  if [ -n "$line" ] && mkdir -p "$LEDGER_DIR" 2>/dev/null && printf '%s\n' "$line" >> "$file" 2>/dev/null; then
    log "  ledger: $file"
  else
    warn "  ledger: could not append $file"
  fi
}

# ── mode dispatch ───────────────────────────────────────────────────────────
MODE="${1:-auto}"
case "$MODE" in
  -h|--help|help)
    sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
    exit 0 ;;
esac

# --list: scan only, never touch the network
if [ "$MODE" = "--list" ] || [ "$MODE" = "list" ]; then
  echo "── camera APs visible (IPCZ7C34*): ──"
  bash "$REPRO_SCRIPT" --list 2>/dev/null || nmcli dev wifi rescan >/dev/null 2>&1; sleep 3
  nmcli -t -f SSID,BSSID,SIGNAL,SECURITY dev wifi list 2>/dev/null | grep -iE '^IPCZ7C34' || echo "(none visible)"
  exit 0
fi

# --enroll-only <ip>: camera is already back on the LAN; skip the AP phase.
if [ "$MODE" = "--enroll-only" ] || [ "$MODE" = "enroll-only" ]; then
  LAN_IP="${2:-}"
  [ -n "$LAN_IP" ] || { fail "--enroll-only requires a camera IP"; exit 1; }
  SERIAL="${3:-}"
  [ -n "$SERIAL" ] || {
    SERIAL=$(curl -sS -m 5 -u 'admin:' "http://$LAN_IP/NetSDK/System/deviceInfo" 2>/dev/null \
      | python3 -c 'import sys,json
try:
    d=json.load(sys.stdin); s=d.get("serialNumber") or d.get("serial") or ""
    print("JA"+s if s.startswith("Z7C") else s)
except Exception: print("")' 2>/dev/null || true)
  }
  banner "── enroll-only: $LAN_IP (serial ${SERIAL:-unknown}) ──"
  if enroll_and_record "$LAN_IP" "$SERIAL"; then
    verify_live "$LAN_IP"
    ledger_append "$SERIAL" "$LAN_IP" enrolled enroll-only
    pass "done — camera is on the network AND in BossCamSuite"
    exit 0
  fi
  ledger_append "$SERIAL" "$LAN_IP" failed enroll-only
  exit 1
fi

# ── full pipeline ────────────────────────────────────────────────────────────
TARGET=""
if [ "$MODE" != "auto" ]; then
  TARGET="$MODE"
  log "target: $TARGET"
fi

banner "╔══════════════════════════════════════════════════════════╗"
banner "║  Camera Recovery: AP hotspot → LAN → BossCamSuite       ║"
banner "║  STA network: $STA_SSID                                     ║"
banner "╚══════════════════════════════════════════════════════════╝"
[ "$DRY_RUN" = "1" ] && warn "DRY RUN — no WiFi or camera writes will be made"

# Phase 1: AP -> LAN (delegate to the proven reprovision tool). REPRO_OUT
# receives the new LAN IP as a machine-readable handoff.
log "phase 1/2: reprovision camera to $STA_SSID (this takes a few minutes)..."
if [ "$DRY_RUN" = "1" ]; then
  log "  [dry] REPRO_OUT=$REPRO_OUT bash $REPRO_SCRIPT ${TARGET:-auto}"
  log "  [dry] (would then enroll the rediscovered LAN IP)"
  echo ""
  exit 0
fi
if ! REPRO_OUT="$REPRO_OUT" bash "$REPRO_SCRIPT" ${TARGET:+$TARGET}; then
  fail "reprovision phase failed — see the output above"
  exit 1
fi
LAN_IP="$(cat "$REPRO_OUT" 2>/dev/null | head -1 || true)"
if [ -z "$LAN_IP" ]; then
  fail "reprovision completed but no LAN IP was handed back — run --enroll-only <ip> once it is on the network"
  exit 1
fi
pass "camera is back on the network at $LAN_IP"

# Phase 2: enroll into BossCamSuite.
# Prefer the camera's own reported serial (works in auto mode where TARGET is
# empty); fall back to the serial derived from an explicit AP/SSID argument.
SERIAL=$(curl -sS -m 5 -u 'admin:' "http://$LAN_IP/NetSDK/System/deviceInfo" 2>/dev/null \
  | python3 -c 'import sys,json
try:
    d=json.load(sys.stdin); s=d.get("serialNumber") or d.get("serial") or ""
    print("JA"+s if s.startswith("Z7C") else s)
except Exception: print("")' 2>/dev/null || true)
[ -n "$SERIAL" ] || SERIAL="$(serial_from_ap "${TARGET:-}" 2>/dev/null || true)"
[ -n "$SERIAL" ] || SERIAL="unknown"
banner "── phase 2/2: enroll $LAN_IP (serial ${SERIAL}) into BossCamSuite ──"
if enroll_and_record "$LAN_IP" "$SERIAL"; then
  verify_live "$LAN_IP"
  ledger_append "$SERIAL" "$LAN_IP" enrolled recover-and-enroll
  pass "DONE — camera $SERIAL is on the network AND in BossCamSuite at $LAN_IP"
  exit 0
fi
ledger_append "$SERIAL" "$LAN_IP" failed recover-and-enroll
exit 1
