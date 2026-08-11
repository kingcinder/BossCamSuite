#!/usr/bin/env bash
# ── 5523w-wifi-reprovision.sh — rejoin a factory-reset 5523-W to the LAN ─────
#
# WHY THIS TOOL EXISTS:
# A factory reset wipes the camera's WiFi station credentials along with the
# admin password, so the camera drops off the LAN entirely and instead
# broadcasts its OWN access point. Observed live 2026-08-09: SSID = "IPC" +
# serial-without-the-"JA" prefix (e.g. serial JAZ7C34780038910 -> AP
# IPCZ7C34780038910, WPA2, BSSID == the camera's own radio MAC 9c:a3:a9:*).
# The controlled-verify experiment's §5.2/§5.3 factory-state poll assumed the
# camera stayed on the LAN at its old IP — it cannot, so the experiment can
# never reach §5.3 by polling 10.0.0.x. This tool closes that gap:
#
#   1. Join the camera's own AP (SSID IPCZ7C34<serial>).
#   2. Verify factory state over the AP link: blank admin -> deviceInfo HTTP 200.
#   3. Write station-mode WiFi config via /NetSDK/Network/interface/4/wireless
#      (wirelessMode=stationMode, wirelessApEssId/wirelessApPsk = our network).
#   4. The camera switches to station mode and rejoins the LAN (new DHCP IP).
#   5. We rejoin our own network and re-discover the camera on the LAN by MAC.
#   6. Verify blank admin answers on the new LAN IP — ready for the
#      controlled-verify flow (set_pass.xml known password, gate check, MITM).
#
# Usage:
#   ./scripts/5523w-wifi-reprovision.sh --list                  # scan only (no disruption)
#   ./scripts/5523w-wifi-reprovision.sh                         # auto-pick the only camera AP
#   ./scripts/5523w-wifi-reprovision.sh JAZ7C34780038910        # by serial
#   ./scripts/5523w-wifi-reprovision.sh IPCZ7C34780038910       # by exact AP SSID
#   ./scripts/5523w-wifi-reprovision.sh --keyprobe-only <ip>    # run ONLY the folded REST key
#                         probe against a live LAN/AP IP — no AP join, no station write, no
#                         reset needed. Exercises the same keyprobe_truth_check path the
#                         re-provision runs after STEP 6, so the fold is testable end-to-end.
#                         DRY_RUN=1 prints the plan only. ADMIN_PASS=<pw> probes a camera
#                         whose admin password was already set (e.g. controlled-verify Plan A).
#
# Env overrides:
#   AP_PASS=...           camera-AP password (default: try open, then common factory defaults)
#   STA_SSID=...          our network SSID (default Aegon)
#   STA_PASS=...          our network password (default 812354444)
#   CAM_MAC_PREFIX=...    camera OUI to rediscover on the LAN (default 9c:a3:a9)
#   SUBNET=...            our LAN subnet to sweep (default 10.0.0)
#   AP_IP_CANDIDATES=...  space-separated AP-IP candidates to probe (default anyka defaults)
#   SETTLE=...            seconds to wait for the camera to switch to station mode (default 90)
#   ENDPOINT=...          NetSDK wireless write path (default /NetSDK/Network/interface/4/wireless)
#   STA_DHCP=1            camera should take DHCP on the station link (default).
#                          0 = pin a static station IP (STA_IP/STA_MASK/STA_GW).
#   STA_IP=10.0.0.29      static station IP used when STA_DHCP=0
#   STA_MASK=255.255.255.0  static station netmask used when STA_DHCP=0
#   STA_GW=10.0.0.1       static station gateway used when STA_DHCP=0
#   REPRO_KEYPROBE=1      run the REST key probe (5523w-interface4-keyprobe.sh) once
#                         against the re-provisioned camera and log its verdict, so
#                         every re-provision also records the REST key truth
#   KEYPROBE_SCRIPT=...   path to the keyprobe tool (default scripts/5523w-interface4-keyprobe.sh)
#   REPRO_OUT=...          file to write the new LAN IP to (machine-readable handoff for
#                          controlled-verify-experiment.sh §5.3b); also echoed on stdout
#   LEDGER_DIR=...          directory for the per-camera JSON ledger
#                           (default local-camera-recovery/ledger). Every re-provision
#                           AND every --keyprobe-only run appends ONE machine-readable
#                           JSONL line — {ts, serial, ip, keyprobe_verdict, sta_dhcp,
#                           source, status} — to <LEDGER_DIR>/<serial>.jsonl. The
#                           keyprobe subprocess is invoked with LEDGER_APPEND=0 so it
#                           never double-appends; this script owns the line.
#   DRY_RUN=1             print actions, do not touch WiFi or the camera
#
# The station-mode write tries, in order, until one returns HTTP 200 (path-major):
#   nested on $ENDPOINT, flat on $ENDPOINT, nested on /NetSDK/Network/interface/4,
#   flat on /NetSDK/Network/interface/4
# (some firmware variants reject the nested stationMode object).
#
# Requires: nmcli (NetworkManager), curl, python3. Does NOT need root.

set -euo pipefail

# ── config ────────────────────────────────────────────────────────────────────
STA_SSID="${STA_SSID:-Aegon}"
STA_PASS="${STA_PASS:-812354444}"
CAM_MAC_PREFIX="${CAM_MAC_PREFIX:-9c:a3:a9}"
SUBNET="${SUBNET:-10.0.0}"
SETTLE="${SETTLE:-90}"
ENDPOINT="${ENDPOINT:-/NetSDK/Network/interface/4/wireless}"
REPRO_OUT="${REPRO_OUT:-}"
LEDGER_DIR="${LEDGER_DIR:-local-camera-recovery/ledger}"
STA_DHCP="${STA_DHCP:-1}"
STA_IP="${STA_IP:-10.0.0.29}"
STA_MASK="${STA_MASK:-255.255.255.0}"
STA_GW="${STA_GW:-10.0.0.1}"
REPRO_KEYPROBE="${REPRO_KEYPROBE:-1}"
KEYPROBE_SCRIPT="${KEYPROBE_SCRIPT:-scripts/5523w-interface4-keyprobe.sh}"
# wirelessStationDhcp is INVERTED from its name on this firmware (evidence on
# station_payload below): true = static station addressing, false = DHCP/dynamic.
DHCP_FLAG="false"; [ "$STA_DHCP" = "0" ] && DHCP_FLAG="true"
AP_IP_CANDIDATES="${AP_IP_CANDIDATES:-192.168.1.1 192.168.0.1 10.10.10.1 192.168.2.1 172.16.0.1}"
# Factory anyka/Wansview AP passphrases to try after "open" fails. Override with AP_PASS.
AP_TRY_PASSWORDS="${AP_PASS:-} 12345678 1234567890 88888888 wifi1234"

IFACE=""
WERE_ON_STA=0
AP_JOINED=""

# ── colors / logging (match sibling scripts) ──────────────────────────────────
RED=$'\e[0;31m'; GREEN=$'\e[0;32m'; YELLOW=$'\e[1;33m'; BLUE=$'\e[0;36m'; NC=$'\e[0m'
log()   { printf "${BLUE}[%s]${NC} %s\n" "$(date -u +%H:%M:%SZ)" "$*"; }
pass()  { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
warn()  { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
fail()  { printf "${RED}[%s]  ✘ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
banner(){ printf "${BLUE}[%s]${NC} %s\n" "$(date -u +%H:%M:%SZ)" "$*"; }

usage_text() {
  # Header-only extraction (same pattern as controlled-verify-experiment.sh): skip
  # the shebang, print the contiguous leading #-comment block, STOP at the first
  # non-comment line — so body comments never leak into --help and the header can
  # keep growing without a magic line count. PRINT-ONLY: callers decide the exit
  # code (usage() exits 0 for -h/--help; error paths call usage_text then exit 1).
  awk 'NR==1{next} /^#/{sub(/^# ?/, ""); print; next} {exit}' "$0"
}
usage() { usage_text; exit 0; }

# ── wifi helpers ──────────────────────────────────────────────────────────────
wifi_iface() {
  local d
  d=$(nmcli -t -f DEVICE,TYPE dev | awk -F: '$2=="wifi" {print $1; exit}' 2>/dev/null || true)
  echo "${d:-}"
}

current_ssid() { nmcli -t -f ACTIVE,SSID dev wifi 2>/dev/null | awk -F: '$1=="yes" {print $2; exit}' || true; }

on_sta() { [ "$(current_ssid)" = "$STA_SSID" ]; }

# Derive the camera AP SSID from its serial: "JA" prefix stripped, "IPC" added.
ap_ssid_for_serial() { # $1=serial -> camera AP SSID (JAZ7C34... -> IPCZ7C34..., IPC* passthrough)
  local s="$1"
  case "$s" in
    JA*)  echo "IPC${s#JA}" ;;
    IPC*) echo "$s" ;;
    *)    echo "IPC$s" ;;
  esac
}

scan_camera_aps() { # prints nmcli -t "SSID:BSSID:SIGNAL:SECURITY" for IPCZ7C34* APs
  # Two real bugs fixed here (observed live 2026-08-09):
  #  1. The grep anchored a trailing colon — '^IPCZ7C34:' — but the AP SSID is
  #     IPCZ7C34 + serial digits (e.g. IPCZ7C34780038910) with NO colon after
  #     IPCZ7C34, so the pattern NEVER matched. Use '^IPCZ7C34' (no colon).
  #  2. nmcli's scan list populates ASYNCHRONOUSLY after `rescan` — retry the
  #     list with a settle between passes so a slow scan can't cause a spurious
  #     "no AP visible" failure.
  local try out
  nmcli dev wifi rescan >/dev/null 2>&1 || true
  for try in 1 2 3 4; do
    out=$(nmcli -t -f SSID,BSSID,SIGNAL,SECURITY dev wifi list 2>/dev/null | grep -iE '^IPCZ7C34' || true)
    if [ -n "$out" ]; then printf '%s\n' "$out"; return 0; fi
    sleep 3
  done
  return 0
}

render_ap_list() { # stdin: nmcli -t lines; stdout: aligned SSID/BSSID/sig/sec (unescapes \:)
  # NOTE: must be python3 -c "$(cat <<'PYEOF')" — a bare `python3 - <<'PYEOF'`
  # consumes the DATA stdin as the script, so sys.stdin reads nothing. This form
  # keeps stdin as the nmcli pipe while the script comes from the substitution.
  python3 -c "$(cat <<'PYEOF'
import re, sys
for line in sys.stdin:
    line = line.rstrip('\n')
    # nmcli -t escapes ':' and '\' as '\:' / '\\' — split only on unescaped colons
    f = re.split(r'(?<!\\):', line)
    f = [x.replace('\\:', ':').replace('\\\\', '\\') for x in f]
    if len(f) >= 4:
        print(f'{f[0]:<24} {f[1]:<20} sig={f[2]:<4} {f[3]}')
PYEOF
)"
}

# ── join / leave APs ──────────────────────────────────────────────────────────
restore_network() {
  if [ "$DRY_RUN" = "1" ]; then return 0; fi
  if ! on_sta; then
    warn "restoring connection to $STA_SSID..."
    nmcli dev wifi connect "$STA_SSID" password "$STA_PASS" >/dev/null 2>&1 \
      && pass "back on $STA_SSID" || warn "could not auto-rejoin $STA_SSID — connect manually"
  fi
}
trap restore_network EXIT

join_ap() { # $1=ssid $2=password-or-empty ; returns 0 when connected
  local ssid="$1" pw="${2:-}"
  if [ "$DRY_RUN" = "1" ]; then echo "  [dry] nmcli dev wifi connect $ssid${pw:+ password ****}"; return 0; fi
  # < /dev/null: without a password nmcli prompts interactively on stdin for a
  # secured AP — in a script that would HANG on an invisible prompt. Redirect
  # stdin so a WPA2 AP fails fast and the loop advances to the next candidate.
  if [ -n "$pw" ]; then
    nmcli dev wifi connect "$ssid" password "$pw" >/dev/null 2>&1 < /dev/null
  else
    nmcli dev wifi connect "$ssid" >/dev/null 2>&1 < /dev/null
  fi
}

# ── factory-state probe over the AP link ──────────────────────────────────────
probe_ap_ip() { # $1=ip ; prints HTTP code for blank-admin deviceInfo (000 = no answer)
  # curl -w already prints 000 on failure — capturing it and echoing ONCE avoids
  # double-printing (000 + "|| echo 000") when something answers with a non-2xx.
  local code
  code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u 'admin:' \
         "http://$1/NetSDK/System/deviceInfo" 2>/dev/null || true)
  echo "${code:-000}"
}

find_ap_ip() { # prints the camera's AP IP that answers factory state (200), or ""
  local ip code
  # Prefer the gateway on the joined link, then the candidate list.
  if [ -n "$IFACE" ]; then
    ip=$(ip route show dev "$IFACE" 2>/dev/null | awk '/default/ {print $3; exit}') || true
    if [ -n "${ip:-}" ] && [ "$(probe_ap_ip "$ip")" = "200" ]; then echo "$ip"; return 0; fi
  fi
  for ip in $AP_IP_CANDIDATES; do
    code=$(probe_ap_ip "$ip")
    log "  probing AP IP $ip -> HTTP $code"
    if [ "$code" = "200" ]; then echo "$ip"; return 0; fi
  done
  echo ""
}

# ── station-mode write (the re-provision) ─────────────────────────────────────
# Evidence for the payload shape (live MITM media-info frames, e.g.
# eseecloud-mitm-20260808T053046Z capture.pcap): the camera serializes its own
# station config as  Interface=wlan0..wireless=stationMode..
# wirelessApEssId=<ssid>..wirelessApPsk=<psk>..wirelessStationDhcp=<true|false>
# — that frame format is WS key=value (mode key is "wireless" THERE, not
# "wirelessMode"), but the CREDENTIAL key names (wirelessApEssId/wirelessApPsk)
# and the mode VALUE (stationMode) transfer directly to the REST JSON.
# The vendor NetSDK REST contract (endpoint_catalog.json NetworkInterfaceWireless
# / NetworkInterfaceStationMode) documents the NESTED JSON form — wirelessMode +
# stationMode:{wirelessApEssId, wirelessApPsk, wirelessApBssId}. wirelessStationDhcp
# is NOT in that form (write-only wire-plane hint), but the firmware ACCEPTS it on
# PUT (HTTP 200/statusCode 0) and write-throughs it into the interface/4 lan block.
# 2026-08-10 live probe (10.0.0.29, blank admin): PUT wirelessStationDhcp:false
# flipped lan.addressingType static->dynamic + OnvifAutoAdapt false->true; PUT true
# did NOT flip back (one-shot write-through — addressingType/OnvifAutoAdapt are
# normalized/read-only; only lan.dhcp is reliably writable). pcap attribution
# across all sessions: .29/.169 emit true (static addressing), the third unit .227
# emits false (dynamic). So the flag is INVERTED from its name here: true = static
# station addressing, false = DHCP/dynamic. We therefore emit the flag matching
# STA_DHCP (default 1 = DHCP -> false) AND write the documented lan block
# explicitly (addressingType/dhcp/staticIP via read-modify-write) so the
# re-provisioned camera deterministically takes DHCP on rejoin instead of pinning
# a stale static NVRAM IP.
station_payload() { # $1=ssid $2=psk $3=dhcp-flag("true"|"false") ; nested NetworkInterfaceWireless JSON (catalog form)
  python3 - "$1" "$2" "$3" <<'PYEOF'
import json, sys
ssid, psk, dhcp = sys.argv[1], sys.argv[2], sys.argv[3] == "true"
print(json.dumps({
    "wirelessMode": "stationMode",
    "stationMode": {
        "wirelessApEssId": ssid,
        "wirelessApPsk": psk,
        "wirelessApBssId": "",
        "wirelessStationDhcp": dhcp,
    },
}, separators=(",", ":")))
PYEOF
}

station_payload_flat() { # $1=ssid $2=psk $3=dhcp-flag ; flat sibling-key variant (matches the camera's own key names)
  python3 - "$1" "$2" "$3" <<'PYEOF'
import json, sys
ssid, psk, dhcp = sys.argv[1], sys.argv[2], sys.argv[3] == "true"
print(json.dumps({
    "wirelessMode": "stationMode",
    "wirelessApEssId": ssid,
    "wirelessApPsk": psk,
    "wirelessStationDhcp": dhcp,
}, separators=(",", ":")))
PYEOF
}

write_lan_addressing() { # $1=ap-ip ; set interface/4 lan block per STA_DHCP (read-modify-write)
  # The wireless-flag write-through is one-shot/unreliable, so set the DOCUMENTED
  # lan fields directly (catalog NetworkInterfaceLan: addressingType/dhcp/staticIP).
  # NOTE (2026-08-10 live evidence): this runs right after the wireless PUT, which
  # triggers the camera to switch to station mode — the lan PUT can race the radio
  # leaving the AP link. It is best-effort redundancy: the flag write-through on the
  # wireless PUT is the effective lever for DHCP. A failed/000 lan PUT warns but does
  # NOT fail the re-provision (STEP 5/6 rediscovery is what matters). Also, the
  # firmware normalizes addressingType/OnvifAutoAdapt on its own, so a 200 alone is
  # not proof of intent — we verify the lan block afterwards and warn if normalized.
  local ip="$1" body newbody resp code v want ok i
  if [ "$DRY_RUN" = "1" ]; then
    log "  [dry] PUT $ip/NetSDK/Network/interface/4  lan=$([ "$STA_DHCP" = "1" ] && echo 'dynamic (DHCP)' || echo "static ($STA_IP)")"
    return 0
  fi
  want="$([ "$STA_DHCP" = "1" ] && echo 'dynamic True' || echo "static False")"
  ok=0
  for i in 1 2; do
    body=$(curl -sS -m 6 -u 'admin:' "http://$ip/NetSDK/Network/interface/4" 2>/dev/null || true)
    if [ -z "$body" ]; then
      warn "  could not read interface/4 for the lan write (attempt $i) — flag write-through still applies"
      sleep 3; continue
    fi
    newbody=$(STA_DHCP="$STA_DHCP" STA_IP="$STA_IP" STA_MASK="$STA_MASK" STA_GW="$STA_GW" \
      python3 -c '
import json, os, sys
d = json.loads(sys.stdin.read())
lan = d.get("lan", {})
if os.environ["STA_DHCP"] == "1":
    lan["addressingType"] = "dynamic"; lan["dhcp"] = True
else:
    lan["addressingType"] = "static"; lan["dhcp"] = False
    lan["staticIP"] = os.environ["STA_IP"]
    lan["staticNetmask"] = os.environ["STA_MASK"]
    lan["staticGateway"] = os.environ["STA_GW"]
d["lan"] = lan
print(json.dumps(d, separators=(",", ":")))
' <<< "$body" 2>/dev/null || true)
    if [ -z "$newbody" ]; then
      warn "  lan modification failed — skipping explicit lan write"
      return 0
    fi
    resp=$(curl -sS -m 10 -u 'admin:' -X PUT -H 'Content-Type: application/json' \
           -d "$newbody" -w '\n%{http_code}' "http://$ip/NetSDK/Network/interface/4" 2>/dev/null || true)
    code="${resp##*$'\n'}"; resp="${resp%$'\n'*}"
    if [ "$code" != "200" ]; then
      warn "  lan write HTTP $code (${resp:0:120}) on attempt $i — retrying once"
      sleep 3; continue
    fi
    sleep 2
    v=$(curl -sS -m 6 -u 'admin:' "http://$ip/NetSDK/Network/interface/4" 2>/dev/null | \
        python3 -c 'import json,sys; d=json.load(sys.stdin).get("lan",{}); print(d.get("addressingType","?"), d.get("dhcp","?"))' 2>/dev/null || true)
    if [ -z "$v" ]; then
      warn "  lan write HTTP 200 but verify GET raced the switch — flag write-through still applies"
      ok=1; break
    elif [ "$v" = "$want" ]; then
      pass "  lan addressing set to $want on interface/4"
      ok=1; break
    else
      warn "  lan write accepted but firmware normalized addressing (got '$v', wanted '$want') — addressingType/OnvifAutoAdapt are read-only-normalized on this firmware"
      ok=1; break
    fi
  done
  [ "$ok" = "1" ] || warn "  lan write did not land (AP link raced the station-mode switch) — flag write-through still applies"
}

write_station_mode() { # $1=ap-ip $2=ssid $3=psk ; tries 4 combinations until HTTP 200
  local body code resp path variant
  if [ "$DRY_RUN" = "1" ]; then
    echo "  [dry] PUT $ENDPOINT  $(station_payload "$2" "$3" "$DHCP_FLAG")"
    echo "  [dry] PUT $ENDPOINT  $(station_payload_flat "$2" "$3" "$DHCP_FLAG")  (flat fallback)"
    return 0
  fi
  for path in "$ENDPOINT" "/NetSDK/Network/interface/4"; do
    for variant in station_payload station_payload_flat; do
      body=$("$variant" "$2" "$3" "$DHCP_FLAG")
      log "  PUT $path ($variant) on $1 (ssid=$2)"
      # ONE curl per attempt: the first successful PUT makes the camera leave AP mode,
      # so a second status-PUT could race the radio switching away. Capture code+body together.
      resp=$(curl -sS -m 10 -u 'admin:' -X PUT -H 'Content-Type: application/json' \
             -d "$body" -w '\n%{http_code}' "http://$1$path" 2>/dev/null || true)
      code="${resp##*$'\n'}"; resp="${resp%$'\n'*}"
      log "  -> HTTP $code  body=${resp:0:200}"
      if [ "$code" = "200" ]; then return 0; fi
    done
  done
  return 1
}

# ── per-camera JSON ledger (campaign audit trail) ─────────────────────────────
# Every re-provision and every --keyprobe-only run appends ONE machine-readable
# JSONL line to $LEDGER_DIR/<serial>.jsonl ({ts, serial, ip, keyprobe_verdict,
# sta_dhcp, source, status}) so the whole recovery campaign is auditable without
# grepping logs. sta_dhcp is the wirelessStationDhcp WIRE flag this script wrote —
# INVERTED on this firmware: true = static station addressing, false = DHCP/dynamic
# (2026-08-10 live evidence, protocol report §5.3b); the keyprobe's own append is
# suppressed (LEDGER_APPEND=0) so each run records exactly one line. Writes are
# best-effort: a failed append warns but never fails the run.
ledger_append() { # $1=serial $2=ip $3=keyprobe_verdict $4=sta_dhcp("true"|"false"|"") $5=status $6=source
  local serial="$1" ip="$2" verdict="$3" dhcp="$4" status="$5" src="$6" file ts line
  [ -n "$serial" ] || serial="unknown"
  [ -n "$verdict" ] || verdict="n/a"
  [ -n "$status" ] || status="ok"
  file="$LEDGER_DIR/$serial.jsonl"
  ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  # Build the JSON with python3 so arbitrary verdict text is safely escaped.
  line=$(python3 - "$ts" "$serial" "$ip" "$verdict" "$dhcp" "$status" "$src" <<'PYEOF'
import json, sys
ts, serial, ip, verdict, dhcp, status, src = sys.argv[1:8]
row = {"ts": ts, "serial": serial, "ip": ip, "keyprobe_verdict": verdict,
       "sta_dhcp": (dhcp.lower() == "true") if dhcp else None,
       "source": src, "status": status}
print(json.dumps(row, separators=(",", ":")))
PYEOF
) || { warn "  ledger: could not build JSON — skipping append"; return 0; }
  if mkdir -p "$LEDGER_DIR" 2>/dev/null && printf '%s\n' "$line" >> "$file" 2>/dev/null; then
    log "  ledger: $file"
  else
    warn "  ledger: could not append $file"
  fi
}

fetch_serial() { # $1=ip $2=auth(-u arg, e.g. 'admin:' or 'admin:pass') ; prints canonical serialNumber, or ""
  local body serial
  body=$(curl -sS -m 5 -u "$2" "http://$1/NetSDK/System/deviceInfo" 2>/dev/null || true)
  [ -n "$body" ] || { echo ""; return 0; }
  serial=$(printf '%s' "$body" | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for k in ("serialNumber", "serial", "sn", "deviceSerial"):
    v = d.get(k)
    if v:
        print(v); sys.exit(0)
' 2>/dev/null || true)
  # NORMALIZE to the campaign-canonical form: deviceInfo serialNumber on these
  # 5523-W units returns the JA-less serial (Z7C34780038910) while the rest of
  # the campaign keys by JAZ7C34... (AP SSID IPCZ7C34..., eseeid derivation,
  # the full re-provision path). Map Z7C* → JA+serial so EVERY ledger write
  # lands in ONE <serial>.jsonl per camera. Other forms pass through untouched.
  case "${serial:-}" in
    Z7C*) serial="JA${serial}" ;;
  esac
  echo "${serial:-}"
}

# ── REST key probe (folded-in keyprobe) ─────────────────────────────────────
keyprobe_truth_check() { # $1=ip ; run the REST key probe once and log its verdict
  # Folds scripts/5523w-interface4-keyprobe.sh into the re-provision so EVERY
  # future re-provision also records the REST key truth (GET mode key + PUT
  # round-trip verdict). Runs the tool as a subprocess against the re-provisioned
  # LAN IP; its PUTs write IDENTICAL values (read-back-first), so it is a semantic
  # no-op on the camera. A missing/failed probe is a WARNING, never a re-provision
  # failure — the re-provision itself already succeeded (STEP 6 blank-admin 200).
  # Globals KEYPROBE_VERDICT / KEYPROBE_DHCP feed the campaign ledger appended by
  # this script (the subprocess itself is invoked with LEDGER_APPEND=0 so each run
  # records exactly ONE line).
  local ip="$1" out kp_rc
  KEYPROBE_VERDICT="n/a"
  KEYPROBE_DHCP=""
  if [ "$DRY_RUN" = "1" ]; then
    log "  [dry] keyprobe against $ip"
    return 0
  fi
  if [ "${REPRO_KEYPROBE:-1}" != "1" ]; then
    log "  key probe disabled (REPRO_KEYPROBE=0)"
    return 0
  fi
  if [ ! -x "$KEYPROBE_SCRIPT" ]; then
    warn "  KEYPROBE_SCRIPT '$KEYPROBE_SCRIPT' not executable — skipping key probe"
    return 0
  fi
  log "  running REST key probe: $KEYPROBE_SCRIPT $ip"
  # Capture the exit code explicitly (NOT `|| true`): a keyprobe that crashes mid-run
  # would otherwise be silently logged as done with zero verdict lines. The `&&…||…`
  # form keeps set -e active while recording rc for the warning below.
  # ADMIN_PASS passthrough: the re-provision targets the FACTORY camera (blank admin),
  # but --keyprobe-only can target a camera whose password was already set (e.g.
  # controlled-verify Plan A after set_pass), so the caller's ADMIN_PASS env reaches
  # the keyprobe tool (default blank — backward compatible, matches the controlled-
  # verify fold's ADMIN_PASS="$KNOWN_PASS" injection). LEDGER_APPEND=0 stops the
  # keyprobe from writing its own ledger line — this script owns the append.
  out=$(LEDGER_APPEND=0 ADMIN_PASS="${ADMIN_PASS:-}" "$KEYPROBE_SCRIPT" "$ip" 2>&1) && kp_rc=0 || kp_rc=$?
  if [ "$kp_rc" != "0" ]; then
    warn "  key probe exited $kp_rc — ${out:0:120} (REST key truth not recorded)"
    return 0
  fi
  if [ -z "$out" ]; then
    warn "  key probe produced no output — REST key truth not recorded"
    return 0
  fi
  # Extract the VERDICT text + current sta_dhcp wire flag from the probe output for
  # the campaign ledger (loose match on the 'VERDICT ... :' prefix — spacing varies).
  KEYPROBE_VERDICT=$(printf '%s\n' "$out" | sed -n 's/.*VERDICT[[:space:]]*:[[:space:]]*//p' | head -1)
  [ -n "$KEYPROBE_VERDICT" ] || KEYPROBE_VERDICT="n/a"
  # `|| true` is REQUIRED here: under set -o pipefail a grep with no match exits 1,
  # which would fail the assignment and trip set -e — killing the whole re-provision
  # right after a successful probe (classify can emit dhcp=? for an empty flag).
  KEYPROBE_DHCP=$(printf '%s\n' "$out" | grep -oE 'dhcp=(true|false)' | head -1 | cut -d= -f2 || true)
  printf '%s\n' "$out" | grep -E 'GET  mode key|PUT accepted|PUT rejected|VERDICT' \
    | sed 's/^/  [keyprobe] /' || true
  log "  key probe done — REST key verdict above"
}

# ── LAN rediscovery by MAC ────────────────────────────────────────────────────
ping_sweep() { # fill the ARP cache for our subnet
  if [ "$DRY_RUN" = "1" ]; then return 0; fi
  log "  ARP sweep of $SUBNET.0/24..."
  seq 1 254 | xargs -P 24 -I{} ping -c1 -W1 "$SUBNET.{}" >/dev/null 2>&1 || true
  sleep 2
}

find_cam_on_lan() { # prints the camera's new LAN IP(es) matching CAM_MAC_PREFIX, or ""
  ping_sweep
  ip neigh show 2>/dev/null \
    | grep -iE "lladdr $CAM_MAC_PREFIX" \
    | awk '{print $1}' | sort -u | tr '\n' ' ' || true
}

# ── main ──────────────────────────────────────────────────────────────────────
DRY_RUN="${DRY_RUN:-0}"
MODE="${1:-auto}"
case "$MODE" in
  -h|--help|help) usage ;;
esac

command -v nmcli >/dev/null || { fail "nmcli (NetworkManager) required"; exit 1; }
command -v curl  >/dev/null || { fail "curl required"; exit 1; }
command -v python3 >/dev/null || { fail "python3 required"; exit 1; }

IFACE=$(wifi_iface)
log "wifi iface: ${IFACE:-none}"
WERE_ON_STA=0; on_sta && WERE_ON_STA=1

# ── --list mode: scan only, never touch the network ──────────────────────────
if [ "$MODE" = "--list" ] || [ "$MODE" = "list" ]; then
  echo "── camera APs visible (IPCZ7C34*): ──"
  scan_camera_aps | render_ap_list
  exit 0
fi

# ── --keyprobe-only mode: run the folded REST key probe against a live IP ─────
# Exercises the SAME keyprobe_truth_check() the re-provision runs after STEP 6,
# but against an already-reachable camera — so the fold is testable end-to-end
# with no factory reset, no AP join, and no station-mode write. The keyprobe
# PUTs identical values (read-back-first), so it is a semantic no-op. Dispatches
# BEFORE the banner/STEP 1 AP scanning (and even before the DRY_RUN early exits
# in join_ap/write_station_mode), so DRY_RUN=1 --keyprobe-only <ip> prints the
# probe plan and exits without touching WiFi or the camera.
if [ "$MODE" = "--keyprobe-only" ] || [ "$MODE" = "keyprobe-only" ]; then
  KP_IP="${2:-}"
  if [ -z "$KP_IP" ]; then
    fail "--keyprobe-only requires a live camera IP (got none)"
    usage_text
    exit 1
  fi
  case "$KP_IP" in
    -h|--help|help) usage ;;
  esac
  log "keyprobe-only mode against $KP_IP (no AP join / station write)"
  keyprobe_truth_check "$KP_IP"
  # DRY_RUN contract: the probe plan was printed above; do NOT touch the camera
  # (fetch_serial curls deviceInfo) or the ledger (file write) in dry mode.
  if [ "$DRY_RUN" != "1" ]; then
    KP_SERIAL=$(fetch_serial "$KP_IP" "admin:${ADMIN_PASS:-}")
    [ -n "$KP_SERIAL" ] || KP_SERIAL="unknown"
    ledger_append "$KP_SERIAL" "$KP_IP" "$KEYPROBE_VERDICT" "$KEYPROBE_DHCP" ok reprovision
  fi
  exit 0
fi

banner "╔══════════════════════════════════════════════════════════╗"
banner "║  5523-W WiFi Re-Provision                                ║"
banner "║  camera AP -> station mode on $STA_SSID                     ║"
banner "╚══════════════════════════════════════════════════════════╝"
log "STA network : $STA_SSID"
log "camera OUI  : $CAM_MAC_PREFIX"
[ "$DRY_RUN" = "1" ] && warn "DRY RUN — no WiFi or camera writes will be made"

# ── 1. locate the camera AP ───────────────────────────────────────────────────
TARGET=""
if [ "$MODE" != "auto" ]; then
  case "$MODE" in
    IPCZ7C34*) TARGET="$MODE" ;;
    *) TARGET=$(ap_ssid_for_serial "$MODE") ;;
  esac
  log "target AP   : $TARGET"
fi

banner "── STEP 1/6: locate the camera's own AP ──"
APS=$(scan_camera_aps)
if [ -z "$APS" ]; then
  fail "no IPCZ7C34* AP visible — is the camera freshly reset and powered?"
  echo "  hint: ./scripts/5523w-wifi-reprovision.sh --list"
  exit 1
fi
if [ -n "$TARGET" ]; then
  AP_SSID=$(printf '%s\n' "$APS" | awk -F: -v t="$TARGET" '$1==t {print $1; exit}')
  [ -n "$AP_SSID" ] || { fail "target AP $TARGET not found in scan"; exit 1; }
else
  COUNT=$(printf '%s\n' "$APS" | wc -l)
  [ "$COUNT" = "1" ] || { fail "found $COUNT camera APs — pass the serial/SSID to pick one"; exit 1; }
  AP_SSID=$(printf '%s\n' "$APS" | head -1 | cut -d: -f1)
fi
log "using AP    : $AP_SSID"
printf '%s\n' "$APS" | render_ap_list

# ── 2. join the AP ────────────────────────────────────────────────────────────
banner "── STEP 2/6: join camera AP $AP_SSID ──"
JOINED=0
# Try open first, then AP_PASS, then the factory default list.
prev_pw="<sentinel>"   # never equals "" — keeps the FIRST open-AP attempt from being deduped
for pw in "" $AP_TRY_PASSWORDS; do
  [ -n "$pw" ] || pw=""
  [ "$pw" = "$prev_pw" ] && continue   # dedupe (AP_PASS unset yields a leading empty)
  prev_pw="$pw"
  log "  trying AP join${pw:+ (password ${pw:0:4}…)}"
  if join_ap "$AP_SSID" "$pw"; then JOINED=1; break; fi
  [ "$DRY_RUN" = "1" ] && break
done
if [ "$DRY_RUN" = "1" ]; then
  warn "dry run stops before joining the camera AP"
  exit 0
fi
[ "$JOINED" = "1" ] || { fail "could not join $AP_SSID (tried open + factory defaults)"; 
  warn "set AP_PASS=... if the AP uses a non-default passphrase"; restore_network; exit 1; }
AP_JOINED="$AP_SSID"
pass "joined $AP_SSID (was on $STA_SSID before: $([ "$WERE_ON_STA" = 1 ] && echo yes || echo no))"

# ── 3. verify factory state over the AP link ──────────────────────────────────
banner "── STEP 3/6: verify factory state (blank admin) ──"
APIP=""
for try in 1 2 3 4 5; do
  APIP=$(find_ap_ip)
  [ -n "$APIP" ] && break
  log "  no AP IP answering yet (attempt $try/5) — camera may still be booting"
  sleep 5
done
[ -n "$APIP" ] || { fail "no factory state reachable over the AP link"; restore_network; exit 1; }
DEV=$(curl -sS -m 5 -u 'admin:' "http://$APIP/NetSDK/System/deviceInfo" 2>/dev/null | head -c 300 || true)
pass "factory state confirmed at $APIP — blank admin → deviceInfo 200"
log "  deviceInfo: ${DEV:0:200}"

# ── 4. write station-mode WiFi config ─────────────────────────────────────────
banner "── STEP 4/6: write station-mode config ($STA_SSID) ──"
if write_station_mode "$APIP" "$STA_SSID" "$STA_PASS"; then
  pass "station-mode config written — camera should switch to $STA_SSID"
  write_lan_addressing "$APIP"
else
  fail "station-mode write did not return HTTP 200 — inspect the PUT response above"
  restore_network; exit 1
fi

# ── 5. camera switches; we rejoin our own network ─────────────────────────────
banner "── STEP 5/6: wait for camera switch + rejoin $STA_SSID ──"
log "  camera is switching to station mode (settle window ${SETTLE}s)"
log "  reconnecting to $STA_SSID..."
for i in $(seq 1 $(( (SETTLE + 4) / 5 ))); do
  if on_sta; then pass "back on $STA_SSID after ~$((i * 5))s"; break; fi
  nmcli dev wifi connect "$STA_SSID" password "$STA_PASS" >/dev/null 2>&1 || true
  sleep 5
done
on_sta || { warn "not on $STA_SSID yet after ${SETTLE}s — continuing discovery anyway"; }

# ── 6. rediscover the camera on the LAN by MAC ────────────────────────────────
banner "── STEP 6/6: rediscover camera on LAN by MAC ($CAM_MAC_PREFIX) ──"
# Stale pre-reset ARP entries for the camera's old IP can linger in `ip neigh`;
# only an IP that ANSWERS blank-admin HTTP 200 counts as re-provisioned.
LAN_IP=""
for i in $(seq 1 12); do
  for ip in $(find_cam_on_lan); do
    code=$(probe_ap_ip "$ip")
    printf "  %s  -> blank-admin deviceInfo HTTP %s\n" "$ip" "$code"
    if [ "$code" = "200" ]; then LAN_IP="$ip"; break; fi
  done
  [ -n "$LAN_IP" ] && break
  log "  camera not answering factory state on LAN yet (attempt $i/12) — still booting after the switch?"
  sleep 10
done
if [ -z "$LAN_IP" ]; then
  fail "camera MAC $CAM_MAC_PREFIX not answering blank-admin on $SUBNET.0/24"
  echo "  hint: check your router's DHCP leases — the camera's station-mode DHCP may have"
  echo "  landed on a DIFFERENT subnet than $SUBNET.0/24; re-run with SUBNET=<actual>."
  exit 1
fi

banner "═══ RE-PROVISIONED ═══"
log "camera $AP_SSID is back on $STA_SSID at: $LAN_IP"
# Machine-readable handoff for controlled-verify-experiment.sh §5.3b: write the
# new LAN IP to REPRO_OUT if requested (single line, nothing else).
if [ -n "$REPRO_OUT" ]; then
  printf '%s\n' "$LAN_IP" > "$REPRO_OUT" && log "  wrote new LAN IP to $REPRO_OUT"
fi
keyprobe_truth_check "$LAN_IP"
# Campaign ledger: serial derives from the camera's own AP SSID (IPC + serial-without-
# "JA"), and sta_dhcp is the wire flag this run WROTE (DHCP_FLAG: false = DHCP wanted,
# true = static — the firmware-inverted semantics documented above).
SERIAL="JA${AP_SSID#IPC}"
ledger_append "$SERIAL" "$LAN_IP" "$KEYPROBE_VERDICT" "$DHCP_FLAG" ok reprovision
log "blank admin still works (password not yet set). Next: run the controlled-verify"
log "flow against the NEW IP, e.g.:"
echo
echo "    sudo CAM_IP=$LAN_IP ./scripts/controlled-verify-experiment.sh"
echo
