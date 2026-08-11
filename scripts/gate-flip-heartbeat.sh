#!/usr/bin/env bash
# ── gate-flip-heartbeat.sh — hourly 10-minute cloud-resurrection monitor ──
#
# Runs gate-flip-experiment.sh --no-early-abort for HEARTBEAT_DURATION seconds
# against each camera in CAMS (sequentially — parallel MITMs would collide on
# iptables/bettercap/port 19000) and appends a one-line verdict per camera to
# a rolling log. Purpose: catch any future cloud-session resurrection within
# an hour of it happening. If a camera starts dialing the cloud again, the
# rotate-mode fake server measures every reply variant against the /user gate
# and the verdict flips from "NO GATE FLIP" to "GATE FLIPPED".
#
# Requires root (ARP spoof + iptables + tcpdump) — the underlying MITM needs it.
#
# Usage:
#   sudo ./scripts/gate-flip-heartbeat.sh [duration_seconds] [cam...]
#     duration  default 600 (10 min)
#     cams      default 10.0.0.169 10.0.0.227
#   sudo ./scripts/gate-flip-heartbeat.sh --check
#     Run flips_log_prune_mark() ALONE — no experiment, no MITM, no banner.
#     Reconciles captures/nonce-flips.log with the current captures/ state on
#     demand (marks rows citing pruned pcaps, unmarks restored ones) instead
#     of waiting for the next hourly tick. Respects the single-instance lock
#     (skips if a heartbeat is mid-tick — a concurrent prune-mark could race
#     the tmp+mv rewrite). Still needs root to write captures/.
#   Env:
#     CAMS=...            camera list        (default 10.0.0.169 10.0.0.227)
#     HEARTBEAT_DURATION= duration seconds   (default 600)
#     HEARTBEAT_LOG=...   rolling log path   (default captures/gate-flip-heartbeat.log)
#     GATE_INTERVAL=...   gate poll seconds  (forwarded to the experiment)
#     PCAP_AUDIT=0        disable per-run pcap auditing (default 1)
#     NONCE_WATCH=0       disable the beacon-nonce watcher (default 1)
#     NONCE_STATE=...     beacon-nonce state file (default
#                         captures/gate-flip-heartbeat-nonce.state)
#     FLIPS_LOG=...       append-only machine-readable flip-event history
#                         (default captures/nonce-flips.log): one line per
#                         NONCE FLIP / MID-TICK NONCE FLIP event with ts,
#                         cam, old, new, kind, pcap path, chain (MIDTICK).
#                         When KEEP_DAYS prunes a session dir, the pruner
#                         suffixes the affected rows' pcap= token with
#                         (pruned) so the history states which source
#                         captures still exist
#     KEEP_RUNS=N         keep the N newest gate-flip-<ts> dirs (default 96 =
#                         ~2 days at 2 runs/hour; heartbeat-owned only)
#     KEEP_DAYS=N         prune eseecloud-mitm-* sessions older than N days
#                         (default 3; bounds the pcap footprint from ~3 MB/run)
#
#   Every tick also audits the run's capture.pcap (in the sibling
#   eseecloud-mitm-<ts> session dir) with the dead-cloud counters: outbound SYN
#   (camera-initiated TCP), DNS queries, WS frames to the fake cloud server
#   (:19000), camera UDP discovery broadcasts, and camera traffic to non-LAN
#   destinations — appended to the log line as a single quoted token
#   pcap="total=..,syn=..,dns=..,ws=..,beacons=..,nonlan=..". PCAP_AUDIT=0
#   disables the audit.
#
#   The beacon-nonce watcher (NONCE_WATCH) additionally extracts the camera's
#   40-hex session nonce from the UDP discovery beacons in the same pcap and
#   compares it against the last recorded value (state file, per camera). A
#   nonce flip is the earliest detectable cloud-session resurrection signal —
#   the HDS/1.0 beacon flips its nonce exactly when the camera's cloud session
#   state changes (proven 2026-08-09: .169 flipped 2ce8497...->8671353c at
#   12:01Z, coincident with the 12:01-15:02 dialing window), which precedes
#   any TCP dialing signature by construction. The verdict line gains
#   nonce="<hex>" (the LAST nonce of the capture) and
#   nonce_flip=<first|no|YES|MIDTICK|none|off|no-pcap>; a flip also emits a
#   ★★★ NONCE FLIP ★★★ line so it is greppable independently of the verdict.
#   MIDTICK means the beacon flipped DURING the 10-min capture window — the
#   line also carries nonce_chain="<old>,<new>" and a ★★★ MID-TICK NONCE FLIP ★★★
#   line, giving the immediate signal without waiting for the next tick.
#   NOTE: a NONCE FLIP / MID-TICK tick still logs verdict=NO GATE FLIP (the
#   UDP beacon is the EARLY warning, ahead of any TCP dialing) — automated
#   watchers must key on the ★★★ lines, not the verdict field.
#   Every flip event is ALSO appended to captures/nonce-flips.log (FLIPS_LOG)
#   as a stable machine-readable line — ts, cam, old→new nonce, kind, pcap
#   path, chain — independent of the rolling heartbeat log, so the fleet's
#   resurrection history survives log rotation and greps cleanly.
#
# Output:
#   captures/gate-flip-heartbeat.log   rolling verdict lines (one per camera)
#   captures/nonce-flips.log           append-only flip-event history (one
#                                      line per NONCE FLIP / MID-TICK event)
#   captures/gate-flip-heartbeat-nonce.state   per-camera last nonce (persists
#                                              across ticks; drives flip detect)
#   captures/gate-flip-<ts>/          per-run experiment + timeline (pruned)
#
# Install with scripts/install-gate-flip-heartbeat.sh (systemd timer, hourly).

set -u
set -o pipefail

CHECK=0
if [ "${1:-}" = "--check" ]; then
  CHECK=1
  shift
fi
DURATION="${1:-${HEARTBEAT_DURATION:-600}}"
if [ "$#" -gt 1 ]; then
  shift 1; CAMS="$*"
else
  CAMS="${CAMS:-10.0.0.169 10.0.0.227}"
fi
GATE_INTERVAL="${GATE_INTERVAL:-5}"
PCAP_AUDIT="${PCAP_AUDIT:-1}"
NONCE_WATCH="${NONCE_WATCH:-1}"
KEEP_RUNS="${KEEP_RUNS:-96}"
KEEP_DAYS="${KEEP_DAYS:-3}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
EXP_BIN="$HERE/gate-flip-experiment.sh"
# --check runs no experiment, so a missing/moved experiment script is fine.
[ "$CHECK" = "1" ] || [ -x "$EXP_BIN" ] || { echo "!! experiment script not found: $EXP_BIN"; exit 2; }
mkdir -p "$ROOT/captures"
LOG="${HEARTBEAT_LOG:-$ROOT/captures/gate-flip-heartbeat.log}"
# NOTE: NONCE_STATE must be assigned AFTER ROOT= above — under set -u a
# default referencing $ROOT before ROOT is set aborts the whole script.
NONCE_STATE="${NONCE_STATE:-$ROOT/captures/gate-flip-heartbeat-nonce.state}"
# NOTE: FLIPS_LOG also references $ROOT, so it must be assigned after ROOT=
FLIPS_LOG="${FLIPS_LOG:-$ROOT/captures/nonce-flips.log}"
LOCK="${HEARTBEAT_LOCK:-/tmp/gate-flip-heartbeat.lock}"

# ── single-instance guard ────────────────────────────────────────────────────
# The hourly timer must never stack on a slow previous run (each camera takes
# DURATION + setup/teardown; two overlapping MITMs would fight over the REDIRECT
# rules and the ws-server port). flock -n: skip this tick if another is active.
exec 9>"$LOCK"
if ! flock -n 9; then
  echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') another heartbeat still active — skipping this tick" >> "$LOG"
  exit 0
fi

# ── manual-MITM collision guard ───────────────────────────────────────────────
# The flock only serializes heartbeat-vs-heartbeat. If an OPERATOR launched
# gate-flip-experiment.sh / eseecloud-mitm-capture.sh by hand (e.g. the full-
# hour run), running our MITM concurrently would collide on bettercap /
# iptables / port 19000. Skip the tick when any MITM is already active.
# --check uses no network (prune-mark only reads the log + stat's pcaps), so
# a running manual MITM does not collide with it and must not block it.
if [ "$CHECK" != "1" ] && pgrep -f 'eseecloud-mitm-capture' >/dev/null 2>&1; then
  echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') a manual MITM capture is running — skipping this tick" >> "$LOG"
  exit 0
fi
# (A crash-orphaned MITM from a killed tick causes exactly ONE skipped tick;
# it self-heals the following hour because the mitm's window is bounded.)

# ── pcap audit ───────────────────────────────────────────────────────────────
# After the experiment, audit the run's capture.pcap (kept in the sibling
# eseecloud-mitm-<ts> session dir) with the same dead-cloud counters used by
# the pcap-comparison analysis (§6 of the gate-flip report): outbound SYN
# (camera-initiated TCP), DNS queries, WS frames to the fake cloud server's
# :19000 registration port, camera UDP discovery broadcasts to
# 255.255.255.255, and camera traffic to non-LAN destinations. Emits one
# pcap="..." fragment for the log line; "audit=off" / "audit=no-pcap" when
# disabled or the pcap is missing.
#
# Parse contract for the log line: every fragment is a quoted single token
# (pcap="a,b,c") or a bare key=value token (nonce_flip=...), and dir= is the
# canonical anchor — harvesters must grep for "pcap=\"" / "nonce=\"" tokens,
# never rely on $NF field position (extra fragments are appended in order:
# ... dir=<ts> pcap="..." nonce="<hex>" [nonce_chain="<a,b>"] nonce_flip=<tag>).
session_pcap() {
  # $1 = gate-flip dir -> sibling eseecloud-mitm-<ts>/capture.pcap path
  local ts
  ts=$(basename "$1" | sed 's/^gate-flip-//')
  echo "$ROOT/captures/eseecloud-mitm-$ts/capture.pcap"
}

audit_pcap() {
  local cam="$1" gfd="$2" pcap
  local total syn dns ws beacons nonlan
  [ "$PCAP_AUDIT" = "0" ] && { echo "audit=off"; return 0; }
  pcap=$(session_pcap "$gfd")
  [ -f "$pcap" ] || { echo "audit=no-pcap"; return 0; }
  total=$(tcpdump -r "$pcap" -nn 2>/dev/null | wc -l | tr -d ' ')
  syn=$(tcpdump -r "$pcap" -nn 2>/dev/null \
    "tcp[tcpflags] & tcp-syn != 0 and tcp[tcpflags] & tcp-ack == 0 and src host $cam" \
    | wc -l | tr -d ' ')
  dns=$(tcpdump -r "$pcap" -nn 2>/dev/null 'udp port 53 or tcp port 53' | wc -l | tr -d ' ')
  ws=$(tcpdump -r "$pcap" -nn 2>/dev/null \
    "tcp and src host $cam and dst port 19000" | wc -l | tr -d ' ')
  beacons=$(tcpdump -r "$pcap" -nn 2>/dev/null \
    "udp and src host $cam and dst host 255.255.255.255" | wc -l | tr -d ' ')
  nonlan=$(tcpdump -r "$pcap" -nn 2>/dev/null \
    "src host $cam and not (dst net 10.0.0.0/8 or dst net 127.0.0.0/8 or dst net 224.0.0.0/4)" \
    | wc -l | tr -d ' ')
  echo "pcap=\"total=$total,syn=$syn,dns=$dns,ws=$ws,beacons=$beacons,nonlan=$nonlan\""
}

# ── beacon-nonce watcher ────────────────────────────────────────────────────
# The camera's HDS/1.0 UDP discovery beacon (src :8002/:18002 ->
# 255.255.255.255) carries a 40-hex `nonce=` that flips EXACTLY when the
# camera's cloud-session state changes — the earliest resurrection signal,
# visible at the UDP layer before any TCP dialing. Each tick extracts ALL
# distinct nonces from the run's capture.pcap and compares against the last
# recorded value (state file, per camera). A change between ticks emits
# nonce_flip=YES plus a greppable ★★★ NONCE FLIP ★★★ line; a change WITHIN the
# capture (the beacon flipped mid-tick — multiple distinct nonces in one
# pcap) emits nonce_flip=MIDTICK + nonce_chain="<old>,<new>" plus a ★★★
# MID-TICK NONCE FLIP ★★★ line — the immediate signal, no waiting for the
# next tick. The state file always advances to the LAST nonce in the capture.
# A camera that stops beaconing reports nonce_flip=none without touching its
# stored value (a quiet tick is not a flip).
beacon_nonce() {
  # $1 = pcap, $2 = cam -> echoes EVERY distinct 40-hex nonce in the pcap,
  # one per line, in order of first appearance (empty if none seen). A beacon
  # that flips mid-capture therefore yields TWO lines (old then new), which
  # nonce_watch reports as nonce_flip=MIDTICK.
  tcpdump -r "$1" -nn -A 2>/dev/null \
    "udp and src host $2 and dst host 255.255.255.255" \
    | grep -oE 'nonce=[0-9a-f]{40}' | sed 's/^nonce=//' | awk '!seen[$0]++'
}

nonce_watch() {
  # $1 = cam, $2 = gate-flip dir -> emits nonce=... nonce_flip=...
  # A capture may hold MORE than one distinct nonce when the beacon flipped
  # mid-tick (the 10-min window spans the flip). That is the immediate signal:
  # nonce_flip=MIDTICK with nonce_chain="<old>,<new>", reported NOW instead of
  # at the next hourly comparison. The state file advances to the LAST nonce
  # of the capture (the camera's current session state).
  local cam="$1" gfd="$2" pcap nonces first last count chain prev tag
  [ "$NONCE_WATCH" = "0" ] && { echo "nonce=off nonce_flip=off"; return 0; }
  pcap=$(session_pcap "$gfd")
  [ -f "$pcap" ] || { echo "nonce=no-pcap nonce_flip=no-pcap"; return 0; }
  nonces=$(beacon_nonce "$pcap" "$cam")
  if [ -z "$nonces" ]; then
    echo "nonce=none nonce_flip=none"
    return 0
  fi
  first=$(printf '%s\n' "$nonces" | head -1)
  last=$(printf '%s\n' "$nonces" | tail -1)
  count=$(printf '%s\n' "$nonces" | wc -l | tr -d ' ')
  chain=$(printf '%s\n' "$nonces" | paste -sd, -)
  prev=$(grep -E "^cam=$cam " "$NONCE_STATE" 2>/dev/null | tail -1 | awk '{print $2}')
  if [ "$count" -gt 1 ]; then
    tag="MIDTICK"
  elif [ -z "$prev" ]; then
    tag="first"
  elif [ "$first" = "$prev" ]; then
    tag="no"
  else
    tag="YES"
  fi
  # Persist the LAST nonce (replace this camera's line; keep the file
  # append-safe for readers)
  grep -v "^cam=$cam " "$NONCE_STATE" 2>/dev/null > "$NONCE_STATE.tmp"
  printf 'cam=%s %s\n' "$cam" "$last" >> "$NONCE_STATE.tmp"
  mv "$NONCE_STATE.tmp" "$NONCE_STATE"
  if [ "$tag" = "MIDTICK" ]; then
    echo "nonce=\"$last\" nonce_chain=\"$chain\" nonce_flip=MIDTICK"
  else
    echo "nonce=\"$last\" nonce_flip=$tag"
  fi
}

# ── flip-event history log ──────────────────────────────────────────────────
# Appends ONE stable, machine-readable line to captures/nonce-flips.log per
# NONCE FLIP / MID-TICK NONCE FLIP event — independent of the rolling verdict
# log (which mixes banners/skips) and of the flip watcher's forensics blocks
# (which are multi-line and deduped). Greppable: "^.... flip cam=" for the
# fleet timeline, " cam=... " per camera, "old=" / "new=" for the transition,
# "pcap=" for the source capture. Rows are never rewritten — EXCEPT the
# pruner's flips_log_prune_mark() may suffix pcap= with (pruned) once
# KEEP_DAYS removes the cited capture (and removes the marker again if the
# pcap is ever restored), so the history always states what still exists.
flip_log() {
  # $1 = cam, $2 = gate-flip dir, $3 = pre-tick nonce (old), $4 = nwatch
  # fragment, $5 = kind (NONCE_FLIP | MID_TICK_FLIP), $6 = chain (MIDTICK only)
  local cam="$1" gfd="$2" old="$3" nwatch="$4" kind="$5" chain="${6:-}"
  local new pcap line
  new=$(printf '%s' "$nwatch" | sed -n 's/.*nonce="\([0-9a-f]\{40\}\)".*/\1/p')
  pcap=$(session_pcap "$gfd")
  # old is the pre-tick state value; for a MID-TICK flip on a camera with no
  # prior state, fall back to the chain's first (in-capture old) element.
  if [ -z "$old" ] && [ -n "$chain" ]; then
    old=$(printf '%s' "$chain" | cut -d, -f1)
  fi
  line="$(date -u '+%Y-%m-%dT%H:%M:%SZ') flip cam=$cam old=${old:-} new=$new kind=$kind pcap=$pcap"
  [ -n "$chain" ] && line="$line chain=$chain"
  if ! printf '%s\n' "$line" >> "$FLIPS_LOG" 2>/dev/null; then
    echo "!! cannot append $FLIPS_LOG" >&2
  fi
}

# ── flip-history pcap reconciliation (pruner companion) ─────────────────────
# nonce-flips.log cites the run's capture.pcap by absolute path. When the
# KEEP_DAYS prune removes an eseecloud-mitm-<ts> session dir, those citations
# go stale — the history would claim a source capture exists when it does not.
# flips_log_prune_mark() reconciles the history with reality: for each UNIQUE
# pcap path referenced (checked once — dedupe by path), every row citing a
# missing capture gets its pcap= token suffixed with "(pruned)", so consumers
# can tell at a glance which source captures still exist. A row whose pcap has
# been restored (file present again) has the marker removed. Idempotent:
# re-running with nothing changed leaves the file byte-identical.
flips_log_prune_mark() {
  [ -f "$FLIPS_LOG" ] || return 0
  local tmp line pcap bare marker owner mode
  declare -A _pcap_ok=()
  tmp="$FLIPS_LOG.prune.tmp"
  : > "$tmp" 2>/dev/null || { echo "!! cannot create $tmp (root-owned captures/?)" >&2; return 1; }
  # Preserve the existing file's ownership and mode across the tmp+mv swap:
  # mv replaces the inode, so without this the rewrite would silently apply
  # the running user's owner and the shell's umask (root-owned 640 -> e.g.
  # cody-owned 664). Snapshot the target's numeric owner:group + mode and
  # stamp them onto the tmp BEFORE the mv. chown may fail for a non-root
  # caller on a foreign-owned file — degrade gracefully, the mv still runs.
  owner=$(stat -c '%u:%g' "$FLIPS_LOG" 2>/dev/null || true)
  mode=$(stat -c '%a' "$FLIPS_LOG" 2>/dev/null || true)
  while IFS= read -r line; do
    [ -n "$line" ] || { printf '\n' >> "$tmp"; continue; }
    pcap=$(printf '%s\n' "$line" | sed -n 's/.*pcap=\([^ ]*\).*/\1/p' | head -1)
    if [ -n "$pcap" ]; then
      # Strip any existing (pruned) marker to recover the true path.
      case "$pcap" in
        *"(pruned)") bare="${pcap%(pruned)}" ; marker=1 ;;
        *)           bare="$pcap"            ; marker=0 ;;
      esac
      # Existence checked ONCE per unique path (dedupe by pcap path).
      if [ -z "${_pcap_ok[$bare]+x}" ]; then
        if [ -f "$bare" ]; then _pcap_ok["$bare"]=1; else _pcap_ok["$bare"]=0; fi
      fi
      if [ "${_pcap_ok[$bare]}" = "0" ] && [ "$marker" = "0" ]; then
        # Capture gone and row unmarked — append the marker to the pcap token.
        line=$(printf '%s\n' "$line" | sed 's|pcap=[^ ]*|&(pruned)|')
      elif [ "${_pcap_ok[$bare]}" = "1" ] && [ "$marker" = "1" ]; then
        # Capture restored — remove a stale marker.
        line=$(printf '%s\n' "$line" | sed 's|\(pcap=[^ ]*\)(pruned)|\1|')
      fi
    fi
    printf '%s\n' "$line" >> "$tmp"
  done < "$FLIPS_LOG"
  if cmp -s "$FLIPS_LOG" "$tmp"; then
    rm -f "$tmp"
  else
    # Attribute copy is best-effort: a non-root caller on a foreign-owned file
    # can't chown, but the mv must still complete. Surface the failure loudly
    # (matching the tmp-creation error style) so ownership drift is visible.
    if [ -n "$owner" ]; then
      chown "$owner" "$tmp" 2>/dev/null || echo "!! cannot chown $tmp to $owner (ownership may drift)" >&2
    fi
    if [ -n "$mode" ]; then
      chmod "$mode" "$tmp" 2>/dev/null || echo "!! cannot chmod $tmp to $mode (mode may drift)" >&2
    fi
    mv "$tmp" "$FLIPS_LOG"
  fi
}

# ── standalone reconcile (--check) ───────────────────────────────────────────
# Runs the pruner companion alone — no experiment, no MITM, no verdict banner.
# Lets an operator reconcile nonce-flips.log with the current captures/ state
# on demand (e.g. after manually pruning session dirs, or after re-copying a
# restored pcap), instead of waiting for the next hourly tick. The single-
# instance lock is still held (flock above), so a concurrent live heartbeat's
# own end-of-tick prune-mark cannot race this tmp+mv rewrite.
if [ "$CHECK" = "1" ]; then
  before=$(grep -c '(pruned)' "$FLIPS_LOG" 2>/dev/null || true); before=${before:-0}
  flips_log_prune_mark
  rc=$?
  after=$(grep -c '(pruned)' "$FLIPS_LOG" 2>/dev/null || true); after=${after:-0}
  total=$(grep -c '^.*flip' "$FLIPS_LOG" 2>/dev/null || true); total=${total:-0}
  echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') check: nonce-flips.log reconciled with captures/ — $total flip row(s), $after marked (pruned), net marked delta $((after - before))"
  exit "$rc"
fi

# ── per-camera run + verdict extraction ──────────────────────────────────────
# The experiment writes captures/gate-flip-<ts>/{gate-timeline.log,experiment.log}.
# After each camera's run the NEWEST gate-flip dir belongs to that run; extract
# the verdict + poll counts + observed variants and append one compact line.
run_camera() {
  local cam="$1" newest verdict gated open noresp variants mid_chain prev_nonce
  "$EXP_BIN" "$DURATION" "$cam" >/dev/null 2>&1
  # Only timestamped session dirs (gate-flip-20...): the glob MUST NOT match
  # our own rolling log (gate-flip-heartbeat.log) or any stray gate-flip-*.log
  # file, or the verdict would be extracted from (and the pruner could delete)
  # the wrong target.
  newest=$(ls -dt "$ROOT/captures"/gate-flip-20* 2>/dev/null | head -1)
  if [ -z "$newest" ] || [ ! -f "$newest/experiment.log" ]; then
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') cam=$cam verdict=NO-OUTPUT gated=0 open=0 noresp=0 duration=$DURATION"
    return 0
  fi
  verdict=$(grep -oE 'VERDICT: [^ ]+( .*)?' "$newest/experiment.log" | tail -1 | tr -s ' ' | sed 's/ ★★★$//')
  gated=$(grep -c ' GATED$' "$newest/gate-timeline.log" 2>/dev/null || true); gated=${gated:-0}
  open=$(grep -c ' OPEN$' "$newest/gate-timeline.log" 2>/dev/null || true); open=${open:-0}
  noresp=$(grep -c ' NO-RESP$' "$newest/gate-timeline.log" 2>/dev/null || true); noresp=${noresp:-0}
  variants=$(grep 'variants observed:' "$newest/experiment.log" | tail -1 | sed -E 's/^.*variants observed:/variants observed:/')
  audit=$(audit_pcap "$cam" "$newest")
  # Snapshot the pre-tick nonce BEFORE nonce_watch persists the new value, so
  # a flip event can be logged with old→new.
  prev_nonce=$(grep -E "^cam=$cam " "$NONCE_STATE" 2>/dev/null | tail -1 | awk '{print $2}')
  nwatch=$(nonce_watch "$cam" "$newest")
  if [ "$open" -gt 0 ]; then
    echo "★★★ $cam GATE OPEN — CLOUD RESURRECTION DETECTED (see $newest) ★★★"
  fi
  case "$nwatch" in
    *nonce_flip=YES*)
      echo "★★★ $cam NONCE FLIP — CLOUD-SESSION STATE CHANGED (UDP beacon, see $newest) ★★★"
      flip_log "$cam" "$newest" "$prev_nonce" "$nwatch" NONCE_FLIP "" ;;
    *nonce_flip=MIDTICK*)
      mid_chain=$(printf '%s' "$nwatch" | sed -n 's/.*nonce_chain="\([^"]*\)".*/\1/p')
      echo "★★★ $cam MID-TICK NONCE FLIP — beacon changed during capture ($mid_chain, see $newest) ★★★"
      flip_log "$cam" "$newest" "$prev_nonce" "$nwatch" MID_TICK_FLIP "$mid_chain" ;;
  esac
  echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') cam=$cam verdict=\"${verdict:-?}\" gated=$gated open=$open noresp=$noresp duration=$DURATION variants=\"${variants:-none}\" dir=$newest $audit $nwatch"
}

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ═══ heartbeat tick: ${DURATION}s/camera on [$CAMS] ═══"
for cam in $CAMS; do
  run_camera "$cam"
done | tee -a "$LOG"

# ── prune heartbeat-owned dirs ───────────────────────────────────────────────
# gate-flip-<ts> dirs are tiny (timeline + experiment log) but accumulate at
# 2/hour; keep only the newest KEEP_RUNS. The mitm session dirs carry the ~3 MB
# pcaps — prune those older than KEEP_DAYS so the monitor's disk footprint stays
# bounded without touching recent evidence (manual-run sessions from today are
# never pruned).
ls -dt "$ROOT/captures"/gate-flip-20* 2>/dev/null | tail -n +$((KEEP_RUNS + 1)) \
  | xargs -r rm -rf 2>/dev/null
find "$ROOT/captures" -maxdepth 1 -type d -name 'eseecloud-mitm-*' -mtime +"$KEEP_DAYS" \
  -exec rm -rf {} + 2>/dev/null
# Reconcile the flip-event history with the pruned captures: rows citing a
# pcap that KEEP_DAYS just removed get pcap=…(pruned), so the history stays
# honest about which source captures still exist (and a restored pcap loses a
# stale marker). Idempotent — a tick with nothing pruned leaves the file alone.
flips_log_prune_mark
exit 0
