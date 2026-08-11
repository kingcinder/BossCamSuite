#!/usr/bin/env bash
# ── 5523w-interface4-keyprobe.sh — REST key probe: wirelessMode nested vs flat ──
#
# WHY THIS TOOL EXISTS:
# The camera's own media-info frames serialize the station config as
#   wireless=stationMode..wirelessApEssId=<ssid>..wirelessApPsk=<psk>..
#   wirelessStationDhcp=<true|false>
# (live captures, e.g. captures/eseecloud-mitm-20260808T053046Z), i.e. the WIRE
# plane names the mode key "wireless". The vendor NetSDK contract
# (assets/protocols/endpoint_catalog.json, Appendix I) documents the REST form
# as NetworkInterfaceWireless { "wirelessMode" ["none"|"accessPoint"|"stationMode"],
# "stationMode" { "wirelessStaMode", "wirelessApBssId", "wirelessApEssId",
# "wirelessApPsk" } } — i.e. the REST plane names the mode key "wirelessMode".
# SETTLED LIVE 2026-08-10 (10.0.0.29, blank admin; full log in the campaign
# ledger): the REST GET returns "wirelessMode" as the mode key, and a REST PUT
# using the wire-plane key "wireless" is REJECTED (HTTP non-200 / statusCode
# != 0) in both nested and flat form — the wire-plane key name does NOT transfer
# to the REST plane. What this tool now verifies on a live unit is the remaining
# question: does a REST PUT under "wirelessMode" round-trip on GET in the
# nested-stationMode form, the flat-sibling form, or both?
#
# SAFETY: every PUT writes the SAME mode value + ESSID + PSK + DHCP flag the
# camera already has (read back first via GET). Only the BODY SHAPE (nested
# stationMode vs flat siblings) changes, so the camera never changes networks,
# reboots, or drops — the experiment is a semantic no-op. The original
# serialization is restored at the end.
#
# Usage:
#   ./scripts/5523w-interface4-keyprobe.sh <ip>            # probe a LAN/AP IP directly
#   ./scripts/5523w-interface4-keyprobe.sh JAZ7C34780038910 # join AP by serial
#   ./scripts/5523w-interface4-keyprobe.sh IPCZ7C34780038910 # join AP by exact SSID
#   ./scripts/5523w-interface4-keyprobe.sh                 # auto: try .29/.169, else AP
#   DRY_RUN=1 ./scripts/5523w-interface4-keyprobe.sh <ip>  # print the plan only
#
# Env overrides:
#   STA_SSID / STA_PASS    our network (default Aegon / 812354444)
#   AP_PASS=...            camera-AP password (tried after "open")
#   AP_IP_CANDIDATES=...   AP-IP candidates (default anyka defaults)
#   CAM_MAC_PREFIX=...     camera OUI (default 9c:a3:a9)
#   SUBNET=...             LAN subnet (default 10.0.0)
#   ADMIN_PASS=...          admin password for the REST probe (default blank — factory
#                           state; set it when probing a camera whose password was
#                           already changed, e.g. the controlled-verify Plan A set_pass)
#   LEDGER_DIR=...          directory for the per-camera JSON ledger
#                           (default local-camera-recovery/ledger)
#   LEDGER_APPEND=0         do not append the campaign ledger line. The re-provision
#                           script (5523w-wifi-reprovision.sh) passes this when it owns
#                           the append itself, so a full re-provision / --keyprobe-only
#                           run records exactly ONE line; standalone runs append here.
#   DRY_RUN=1              print actions, do not touch WiFi or the camera
#
# Requires: nmcli (NetworkManager), curl, python3. Does NOT need root.
# Sourceable (main guarded) so a harness can stub http_get/http_put.

set -euo pipefail

# ── config ────────────────────────────────────────────────────────────────────
STA_SSID="${STA_SSID:-Aegon}"
STA_PASS="${STA_PASS:-812354444}"
CAM_MAC_PREFIX="${CAM_MAC_PREFIX:-9c:a3:a9}"
SUBNET="${SUBNET:-10.0.0}"
AP_IP_CANDIDATES="${AP_IP_CANDIDATES:-192.168.1.1 192.168.0.1 10.10.10.1 192.168.2.1 172.16.0.1}"
# Factory anyka/Wansview AP passphrases to try after "open" fails. Override with AP_PASS.
AP_TRY_PASSWORDS="${AP_PASS:-} 12345678 1234567890 88888888 wifi1234"
AUTO_LAN_IPS="${AUTO_LAN_IPS:-10.0.0.29 10.0.0.169}"
# REST admin credentials: blank by default (factory state); ADMIN_PASS overrides it
# so the probe works against a camera whose password was already set (Plan A).
ADMIN_PASS="${ADMIN_PASS:-}"
# Per-camera campaign ledger: one JSONL line per run appended to
# $LEDGER_DIR/<serial>.jsonl ({ts, serial, ip, keyprobe_verdict, sta_dhcp, source,
# status}) so the whole recovery campaign is auditable without grepping logs.
LEDGER_DIR="${LEDGER_DIR:-local-camera-recovery/ledger}"
LEDGER_APPEND="${LEDGER_APPEND:-1}"

IFACE=""
WERE_ON_STA=0

# ── colors / logging (match sibling scripts) ──────────────────────────────────
RED=$'\e[0;31m'; GREEN=$'\e[0;32m'; YELLOW=$'\e[1;33m'; BLUE=$'\e[0;36m'; NC=$'\e[0m'
log()   { printf "${BLUE}[%s]${NC} %s\n" "$(date -u +%H:%M:%SZ)" "$*"; }
pass()  { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
warn()  { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
fail()  { printf "${RED}[%s]  ✘ %s${NC}\n" "$(date -u +%H:%M:%SZ)" "$*"; }
banner(){ printf "${BLUE}[%s]${NC} %s\n" "$(date -u +%H:%M:%SZ)" "$*"; }

usage_text() {
  # Header-only extraction (same pattern as 5523w-wifi-reprovision.sh): skip the
  # shebang, print the contiguous leading #-comment block, STOP at the first
  # non-comment line. PRINT-ONLY: usage() exits 0 for -h/--help; the file has no
  # error paths that need usage_text + exit 1, but keeping the split matches the
  # sibling scripts and lets the header keep growing without a magic line count.
  awk 'NR==1{next} /^#/{sub(/^# ?/, ""); print; next} {exit}' "$0"
}
usage() { usage_text; exit 0; }

# ── wifi helpers (same proven patterns as 5523w-wifi-reprovision.sh) ──────────
wifi_iface() {
  local d
  d=$(nmcli -t -f DEVICE,TYPE dev | awk -F: '$2=="wifi" {print $1; exit}' 2>/dev/null || true)
  echo "${d:-}"
}

current_ssid() { nmcli -t -f ACTIVE,SSID dev wifi 2>/dev/null | awk -F: '$1=="yes" {print $2; exit}' || true; }

on_sta() { [ "$(current_ssid)" = "$STA_SSID" ]; }

ap_ssid_for_serial() { # $1=serial -> camera AP SSID (JAZ7C34... -> IPCZ7C34..., IPC* passthrough)
  local s="$1"
  case "$s" in
    JA*)  echo "IPC${s#JA}" ;;
    IPC*) echo "$s" ;;
    *)    echo "IPC$s" ;;
  esac
}

scan_camera_aps() { # prints nmcli -t "SSID:BSSID:SIGNAL:SECURITY" for IPCZ7C34* APs
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
  python3 -c "$(cat <<'PYEOF'
import re, sys
for line in sys.stdin:
    line = line.rstrip('\n')
    f = re.split(r'(?<!\\):', line)
    f = [x.replace('\\:', ':').replace('\\\\', '\\') for x in f]
    if len(f) >= 4:
        print(f'{f[0]:<24} {f[1]:<20} sig={f[2]:<4} {f[3]}')
PYEOF
)"
}

# ── join / leave APs ──────────────────────────────────────────────────────────
restore_network() {
  if [ "${DRY_RUN:-0}" = "1" ]; then return 0; fi
  if ! on_sta; then
    warn "restoring connection to $STA_SSID..."
    nmcli dev wifi connect "$STA_SSID" password "$STA_PASS" >/dev/null 2>&1 \
      && pass "back on $STA_SSID" || warn "could not auto-rejoin $STA_SSID — connect manually"
  fi
}
trap restore_network EXIT

join_ap() { # $1=ssid $2=password-or-empty ; returns 0 when connected
  local ssid="$1" pw="${2:-}"
  if [ "${DRY_RUN:-0}" = "1" ]; then echo "  [dry] nmcli dev wifi connect $ssid${pw:+ password ****}"; return 0; fi
  # < /dev/null: without a password nmcli prompts interactively on stdin for a
  # secured AP — in a script that would HANG on an invisible prompt. Redirect
  # stdin so a WPA2 AP fails fast and the loop advances to the next candidate.
  if [ -n "$pw" ]; then
    nmcli dev wifi connect "$ssid" password "$pw" >/dev/null 2>&1 < /dev/null
  else
    nmcli dev wifi connect "$ssid" >/dev/null 2>&1 < /dev/null
  fi
}

# ── factory-state probe over any link ─────────────────────────────────────────
probe_ap_ip() { # $1=ip ; prints HTTP code for admin-auth deviceInfo (000 = no answer)
  local code
  if [ "${DRY_RUN:-0}" = "1" ]; then echo "000"; return 0; fi  # DRY_RUN never touches the wire
  code=$(curl -sS -o /dev/null -w '%{http_code}' -m 5 -u "admin:$ADMIN_PASS" \
         "http://$1/NetSDK/System/deviceInfo" 2>/dev/null || true)
  echo "${code:-000}"
}

find_ap_ip() { # prints the camera's AP IP that answers factory state (200), or ""
  local ip code
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

# ── HTTP helpers (the only curl call sites; harness stubs these) ──────────────
http_get() { # $1=ip $2=path ; prints "CODE<TAB>BODY" (CODE 000 = no answer)
  local out code body
  out=$(curl -sS -m 8 -u "admin:$ADMIN_PASS" -w $'\n%{http_code}' "http://$1$2" 2>/dev/null || true)
  code="${out##*$'\n'}"; body="${out%$'\n'*}"
  printf '%s\t%s' "${code:-000}" "$body"
}

http_put() { # $1=ip $2=path $3=body ; prints "CODE<TAB>BODY"
  local out code body
  out=$(curl -sS -m 10 -u "admin:$ADMIN_PASS" -X PUT -H 'Content-Type: application/json' -d "$3" \
        -w $'\n%{http_code}' "http://$1$2" 2>/dev/null || true)
  code="${out##*$'\n'}"; body="${out%$'\n'*}"
  printf '%s\t%s' "${code:-000}" "$body"
}

# ── per-camera JSON ledger (campaign audit trail) ─────────────────────────────
# Every standalone keyprobe run appends ONE machine-readable JSONL line to
# $LEDGER_DIR/<serial>.jsonl. Fields: ts, serial, ip, keyprobe_verdict, sta_dhcp,
# source, status. NOTE the sta_dhcp semantics on this firmware: wirelessStationDhcp
# is INVERTED from its name — true = static station addressing, false = DHCP/dynamic
# (2026-08-10 live probe evidence, protocol report §5.3b). Writes are best-effort:
# a failed append warns but never fails the probe.
ledger_append() { # $1=serial $2=ip $3=keyprobe_verdict $4=sta_dhcp("true"|"false"|"") $5=status $6=source
  local serial="$1" ip="$2" verdict="$3" dhcp="$4" status="$5" src="$6" file ts line
  if [ "$LEDGER_APPEND" != "1" ]; then return 0; fi
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

fetch_serial() { # $1=ip ; prints canonical serialNumber from deviceInfo, or ""
  local code body serial
  IFS=$'\t' read -r code body <<< "$(http_get "$1" "/NetSDK/System/deviceInfo")"
  [ "$code" = "200" ] || { echo ""; return 0; }
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
  # the re-provision path). Map Z7C* → JA+serial so EVERY ledger write lands
  # in ONE <serial>.jsonl per camera. Other forms pass through untouched.
  case "${serial:-}" in
    Z7C*) serial="JA${serial}" ;;
  esac
  echo "${serial:-}"
}

# ── payload builder: same values, chosen key spelling ─────────────────────────
wire_payload() { # $1=mode_key $2=nested(1) or flat(0) $3=ssid $4=psk $5=dhcp
  python3 - "$1" "$2" "$3" "$4" "$5" <<'PYEOF'
import json, sys
mk, nested, ssid, psk, dhcp = sys.argv[1], sys.argv[2] == "1", sys.argv[3], sys.argv[4], sys.argv[5] == "true"
station = {"wirelessApEssId": ssid, "wirelessApPsk": psk, "wirelessApBssId": "", "wirelessStationDhcp": dhcp}
if nested:
    body = {mk: "stationMode", "stationMode": station}
else:
    body = {mk: "stationMode", "wirelessApEssId": ssid, "wirelessApPsk": psk, "wirelessStationDhcp": dhcp}
print(json.dumps(body, separators=(",", ":")))
PYEOF
}

# ── GET-body classification: which mode key did the firmware serialize? ───────
classify() { # stdin: GET body ; prints "mode_key|mode_value|essid|psk|dhcp"
  # NOTE: must be python3 -c "$(cat <<'PYEOF')" — a bare `python3 - <<'PYEOF'`
  # consumes the DATA stdin as the script, so sys.stdin.read() would read the
  # script itself, not the piped body (same pitfall as render_ap_list).
  python3 -c "$(cat <<'PYEOF'
import json, sys
raw = sys.stdin.read().strip()
try:
    d = json.loads(raw)
except Exception:
    print("unparseable||||"); sys.exit(0)
if isinstance(d, list):
    d = next((x for x in d if isinstance(x, dict) and ("wireless" in x or "wirelessMode" in x)), None)
if not isinstance(d, dict):
    print("no-wireless-section||||"); sys.exit(0)
# unwrap the RPC-style envelope {"requestMethod":...,"statusCode":..., ...}
if "requestMethod" in d:
    d = {k: v for k, v in d.items() if k not in ("requestMethod", "requestURL", "requestQuery", "statusCode", "statusMessage")}
# unwrap the section-wrapped form {"id":4,"interfaceName":"wlan0","wireless":{...}}
if "wireless" in d and isinstance(d["wireless"], dict):
    d = d["wireless"]
mk = mv = essid = psk = dhcp = ""
if isinstance(d, dict):
    found = [k for k in ("wirelessMode", "wireless") if k in d and isinstance(d[k], str)]
    if found:
        mk = "+".join(found)  # note BOTH if the firmware echoes both keys
        mv = d[found[0]]
    st = d.get("stationMode") or {}
    if isinstance(st, dict):
        essid = st.get("wirelessApEssId", "")
        psk = st.get("wirelessApPsk", "")
        if "wirelessStationDhcp" in st:
            dhcp = str(st["wirelessStationDhcp"]).lower()
    if not essid and "wirelessApEssId" in d:
        essid = d.get("wirelessApEssId", "")
    if not psk and "wirelessApPsk" in d:
        psk = d.get("wirelessApPsk", "")
    if not dhcp and "wirelessStationDhcp" in d:
        dhcp = str(d.get("wirelessStationDhcp")).lower()
print("|".join(str(x or "") for x in (mk, mv, essid, psk, dhcp)))
PYEOF
)"
}

# ── the probe: GET both paths, PUT 2 wirelessMode forms, round-trip, restore ──
probe_camera() { # $1=ip
  local ip="$1" code body g4 g4w src mk mv essid psk dhcp
  local variant km nest label putbody putcode putsc newmk newmv newessid rt
  local serial="" verdict_text=""
  banner "═══ probing $ip (blank admin) ═══"
  if [ "${DRY_RUN:-0}" = "1" ]; then
    warn "DRY RUN — printing the probe plan for $ip only (no network touch)"
    for variant in "wirelessMode|1" "wirelessMode|0"; do
      km="${variant%|*}"; nest="${variant#*|}"
      label="key=$km ($([ "$nest" = "1" ] && echo nested-stationMode || echo flat-sibling))"
      echo "  [dry] PUT /NetSDK/Network/interface/4/wireless  $label  $(wire_payload "$km" "$nest" "$STA_SSID" "$STA_PASS" true)"
    done
    return 0
  fi
  code=$(probe_ap_ip "$ip")
  if [ "$code" != "200" ]; then
    fail "admin auth does not answer deviceInfo on $ip (HTTP $code) — skipping"
    ledger_append "unknown" "$ip" "admin auth failed (HTTP $code)" "" auth-failed keyprobe
    return 1
  fi
  pass "admin auth OK at $ip"
  serial=$(fetch_serial "$ip")
  [ -n "$serial" ] || warn "  could not read serialNumber from deviceInfo — ledger serial will be 'unknown'"

  # 1. GET both paths — the serialization question
  banner "-- GET /NetSDK/Network/interface/4 --"
  IFS=$'\t' read -r code body <<< "$(http_get "$ip" "/NetSDK/Network/interface/4")"
  printf '  HTTP %s\n  body: %.400s\n' "$code" "$body"
  g4="$body"
  banner "-- GET /NetSDK/Network/interface/4/wireless --"
  IFS=$'\t' read -r code body <<< "$(http_get "$ip" "/NetSDK/Network/interface/4/wireless")"
  printf '  HTTP %s\n  body: %.400s\n' "$code" "$body"
  g4w="$body"

  # 2. read current values (prefer /interface/4, fall back to /wireless)
  src="$g4"
  IFS='|' read -r mk mv essid psk dhcp <<< "$(printf '%s' "$src" | classify)"
  if [ -z "$essid" ]; then
    src="$g4w"
    IFS='|' read -r mk mv essid psk dhcp <<< "$(printf '%s' "$src" | classify)"
  fi
  log "  current: mode_key=${mk:-?} mode=${mv:-?} essid=${essid:-?} psk=${psk:+<set>} dhcp=${dhcp:-?}"
  [ -n "$essid" ] || essid="$STA_SSID"
  [ -n "$psk" ]   || psk="$STA_PASS"
  [ -n "$mv" ]    || mv="stationMode"
  [ -n "$dhcp" ]  || dhcp="true"

  # 3. PUT probes — same values, two wirelessMode body shapes (nested vs flat)
  banner "-- PUT round-trip probes (values unchanged, wirelessMode nested vs flat) --"
  local accepted="" rejected=""
  for variant in "wirelessMode|1" "wirelessMode|0"; do
    km="${variant%|*}"; nest="${variant#*|}"
    label="${km} ($([ "$nest" = "1" ] && echo nested-stationMode || echo flat-sibling))"
    putbody=$(wire_payload "$km" "$nest" "$essid" "$psk" "$dhcp")
    log "  PUT /NetSDK/Network/interface/4/wireless  key=$label"
    IFS=$'\t' read -r putcode body <<< "$(http_put "$ip" "/NetSDK/Network/interface/4/wireless" "$putbody")"
    putsc=$(printf '%s' "$body" | python3 -c 'import sys,re;m=re.search(r"\"statusCode\":(-?\d+)",sys.stdin.read());print(m.group(1) if m else "?")' 2>/dev/null || echo "?")
    printf '    -> HTTP %s  statusCode %s  body: %.200s\n' "$putcode" "$putsc" "$body"
    # round-trip: GET back and compare the mode key + values
    rt=""
    if [ "$putcode" = "200" ] && [ "$putsc" = "0" ]; then
      IFS=$'\t' read -r code body <<< "$(http_get "$ip" "/NetSDK/Network/interface/4/wireless")"
      IFS='|' read -r newmk newmv newessid _ _ <<< "$(printf '%s' "$body" | classify)"
      if [ "$newmk" = "$km" ] && [ "$newmv" = "$mv" ] && [ "$newessid" = "$essid" ]; then
        rt="round-trips"
      else
        rt="accepted but GET shows key=${newmk:-?}"
      fi
    else
      rt="rejected"
    fi
    case "$rt" in
      rejected) rejected="${rejected:+$rejected }$km($nest)" ;;
      *)        accepted="${accepted:+$accepted }$km($nest)=$rt" ;;
    esac
    printf '    => %s\n' "$rt"
  done

  # 4. restore the ORIGINAL key spelling (semantic no-op, same values).
  #    Try nested then flat: some firmware variants reject the nested
  #    stationMode object (the re-provision tool's own docs warn of this).
  banner "-- restore original serialization (mode_key=${mk:-wirelessMode}) --"
  local mkp="${mk%%+*}" ok_restore=0 nest
  for nest in 1 0; do
    putbody=$(wire_payload "${mkp:-wirelessMode}" "$nest" "$essid" "$psk" "$dhcp")
    IFS=$'\t' read -r putcode body <<< "$(http_put "$ip" "/NetSDK/Network/interface/4/wireless" "$putbody")"
    printf '  HTTP %s  (nest=%s)  body: %.200s\n' "$putcode" "$nest" "$body"
    if [ "$putcode" = "200" ]; then ok_restore=1; break; fi
  done
  [ "$ok_restore" = "1" ] || warn "restore PUT did not return 200 — camera may be left under a different key spelling (values unchanged)"

  # 5. verdict + campaign ledger
  banner "═══ KEY VERDICT $ip ═══"
  echo "  GET  mode key      : ${mk:-none-detected}"
  echo "  PUT accepted+ok    :${accepted:- none}"
  echo "  PUT rejected       :${rejected:- none}"
  if [ -n "$accepted" ] && [ -z "$rejected" ]; then
    verdict_text="all wirelessMode PUT forms accepted (nested + flat); GET canonical key = ${mk:-?}"
  elif [ -n "$accepted" ]; then
    verdict_text="REST plane accepts only:${accepted%% *} — the other wirelessMode form rejected"
  else
    verdict_text="ALL wirelessMode PUT forms rejected — firmware wants a different body shape"
  fi
  echo "  VERDICT            : $verdict_text"
  ledger_append "$serial" "$ip" "$verdict_text" "$dhcp" ok keyprobe
}

# ── main ──────────────────────────────────────────────────────────────────────
main() {
  local MODE="${1:-auto}"
  case "$MODE" in
    -h|--help|help) usage ;;
  esac
  command -v nmcli >/dev/null || { fail "nmcli (NetworkManager) required"; exit 1; }
  command -v curl  >/dev/null || { fail "curl required"; exit 1; }
  command -v python3 >/dev/null || { fail "python3 required"; exit 1; }

  IFACE=$(wifi_iface)
  log "wifi iface: ${IFACE:-none}"
  WERE_ON_STA=0; on_sta && WERE_ON_STA=1

  if [ "${DRY_RUN:-0}" = "1" ]; then
    warn "DRY RUN — no WiFi or camera writes will be made"
    if [[ ! "$MODE" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      warn "DRY RUN — print-only: pass an explicit IP to see the PUT plan (auto/AP modes are not dry-run-able)"
      exit 0
    fi
  fi

  # direct IP mode — exit after the probe; do NOT fall through to AP scanning
  # (observed live 2026-08-10: after a successful probe the script continued into
  # the AP-join section and scanned for a nonsense 'IPC<ip>' AP).
  if [[ "$MODE" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    probe_camera "$MODE" || return $?
    return 0
  fi

  # auto mode: try known LAN IPs first (camera may already be on the LAN)
  if [ "$MODE" = "auto" ]; then
    for ip in $AUTO_LAN_IPS; do
      log "auto: probing $ip on the LAN..."
      if [ "$(probe_ap_ip "$ip")" = "200" ]; then
        pass "found camera at $ip on the LAN — probing directly (no AP join needed)"
        probe_camera "$ip" || return $?
      fi
    done
    log "no camera answering blank-admin on the LAN — falling back to camera AP"
  fi

  # AP mode: locate + join the camera's own AP
  local TARGET="" AP_SSID APS COUNT
  case "$MODE" in
    auto) TARGET="" ;;
    IPCZ7C34*) TARGET="$MODE" ;;
    *) TARGET=$(ap_ssid_for_serial "$MODE") ;;
  esac
  [ -z "$TARGET" ] || log "target AP   : $TARGET"

  banner "── locate the camera's own AP ──"
  APS=$(scan_camera_aps)
  if [ -z "$APS" ]; then
    fail "no IPCZ7C34* AP visible — power-cycle the camera into AP mode (factory state) or pass its LAN IP"
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

  banner "── join camera AP $AP_SSID ──"
  local JOINED=0 pw prev_pw="<sentinel>" APIP
  for pw in "" $AP_TRY_PASSWORDS; do
    [ -n "$pw" ] || pw=""
    [ "$pw" = "$prev_pw" ] && continue
    prev_pw="$pw"
    log "  trying AP join${pw:+ (password ${pw:0:4}…)}"
    if join_ap "$AP_SSID" "$pw"; then JOINED=1; break; fi
    [ "${DRY_RUN:-0}" = "1" ] && break
  done
  if [ "${DRY_RUN:-0}" = "1" ]; then warn "dry run stops before joining the camera AP"; exit 0; fi
  [ "$JOINED" = "1" ] || { fail "could not join $AP_SSID (tried open + factory defaults)"; \
    warn "set AP_PASS=... if the AP uses a non-default passphrase"; exit 1; }
  pass "joined $AP_SSID"

  APIP=""
  for try in 1 2 3 4 5; do
    APIP=$(find_ap_ip)
    [ -n "$APIP" ] && break
    log "  no AP IP answering yet (attempt $try/5) — camera may still be booting"
    sleep 5
  done
  [ -n "$APIP" ] || { fail "no factory state reachable over the AP link"; exit 1; }
  pass "factory state at $APIP — probing keys"
  probe_camera "$APIP" || return $?
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
fi
