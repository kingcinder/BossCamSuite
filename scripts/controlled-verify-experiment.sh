#!/usr/bin/env bash
# ── controlled-verify-experiment.sh — end-to-end automation of the 2026-08-09
#    controlled verify-formula experiment (docs/reports/2026-08-09-controlled-
#    verify-experiment-protocol.md: §5 protocol steps, §6 post-reset decision
#    tree, §7 before/after diff matrix). The ONLY manual step is the physical
#    factory reset of the target camera (10–15 s reset-button hold).
#
# Requires: root (relaunches the MITM capture, which needs ARP spoof +
# iptables), curl, python3, and local-camera-recovery/tools/scan_verify_pairs.py
# (used as the §7 confirmatory cross-check).
#
# Usage (full run):
#   sudo ./scripts/controlled-verify-experiment.sh
#
# Usage (offline §7 evaluation of an EXISTING session — no camera, no root):
#   ./scripts/controlled-verify-experiment.sh --eval captures/eseecloud-mitm-<ts>Z
#
# Env overrides (all optional):
#   CAM_IP=10.0.0.169        target camera (protocol §2: reset .169, keep .29 control)
#   CAM_ESEEID=4781620744    baseline eseeid; post-reset value is compared against it
#   KNOWN_PASS=BossCamCtrl2026!   the controlled known password (protocol §4.4)
#   MITM_DURATION=360        MITM capture window (protocol §5.7)
#   POLL_TIMEOUT=180         factory-state poll budget (protocol §5.3)
#   RESET_RETRIES=2          re-prompt limit when the reset does not take
#   ARCHIVE_DIR=captures/archive-controlled-baseline   §5.1 read-only baseline copy
#   AUTO_ADMIN=0             MITM must NOT add accounts (we own set_pass.xml)
#   P2P_FORGE_IP=...         MITM public P2P IP the forged reply points the camera at
#   STUN_FORGE_IP=...        MITM public STUN IP (must differ from P2P_FORGE_IP)
#   WS_LITE_MONITOR=1        LITE-aware grant + LITE_DELTA/FULL_ESCALATION logging (default on)
#   WS_LITE_CADENCE=0x1E     counter advance granted to LITE 0x00 keepalives (default 0x1E, non-natural)
#   LITE_BASELINE_DURATION=90   pre-reset LITE observation window (default 90s; §5.1b)
#   RESET_29_AUTHORIZED=1    §9 guardrail: only set to reset the CONTROL camera 10.0.0.29
#
# §5.3b WiFi re-provision (camera drops off the LAN after the reset):
#   A factory reset wipes the 5523-W's WiFi credentials, so the camera leaves
#   the LAN (HTTP 000) and becomes its own AP. When poll_factory sees sustained
#   HTTP 000, it AUTO-INVOKES scripts/5523w-wifi-reprovision.sh, which joins the
#   camera AP, writes station-mode WiFi config, and re-discovers the camera on
#   the LAN by MAC — then the experiment continues against the NEW CAM_IP.
#   REPRO_SERIAL=...           camera serial/AP-SSID (default: derived from CAM_ESEEID;
#                              empty string "" → tool auto-picks the only visible camera AP)
#   REPRO_AP_PASS=...          camera-AP passphrase (default: empty → tool tries open + factory defaults)
#   REPRO_STA_SSID=...         WiFi the camera rejoins (default Aegon)
#   REPRO_STA_PASS=...         our WiFi password (default 812354444)
#   REPRO_CAM_MAC_PREFIX=...   camera OUI for LAN rediscovery (default 9c:a3:a9)
#   REPRO_SUBNET=...           LAN subnet to sweep (default 10.0.0)
#   REPRO_MAX_ATTEMPTS=4       re-provision attempts (camera AP can take 30-60s+ to boot)
#   REPRO_WAIT=30              seconds between re-provision attempts
#   REPRO_DISABLED=0           set 1 to skip the auto re-provision (poll only)
#   ZERO_BEFORE_REPROVISION=2  consecutive HTTP 000 polls before declaring vanished
# §5.8 REST key probe (post-reset key truth):
#   VERIFY_KEYPROBE=1       run the interface-4 REST key probe once against the post-reset
#                           camera after the §5.7 MITM relaunch and log its verdict, so
#                           the run records the key truth (GET mode key + PUT round-trip)
#                           alongside the LITE-grant verdict and the §7 diff matrix. The
#                           probe is delegated to $REPRO_SCRIPT --keyprobe-only — the SAME
#                           single code path the re-provision itself uses after STEP 6 and
#                           the standalone --keyprobe-only runs — with ADMIN_PASS=$KNOWN_PASS
#                           (Plan A) / blank (Plan B), so every keyprobe run in the campaign
#                           shares one implementation (verdict formatting + the campaign
#                           ledger append at local-camera-recovery/ledger/<serial>.jsonl).
#   KEYPROBE_SCRIPT=...     path to the keyprobe tool, passed through to the re-provision
#                           wrapper (default $SCRIPT_DIR/5523w-interface4-keyprobe.sh)
# DONE ledger assertion (the §5.8 line MUST land):
#   LEDGER_ASSERT=1       DONE renders scripts/ledger-report.sh --json and asserts the
#                         post-reset keyprobe line exists in the campaign ledger — written
#                         during THIS run with a REAL verdict (not "n/a" = keyprobe refused/
#                         auth failed, not stale). A missing line FAILS the run loudly; set 0
#                         to skip. Gated on the same conditions as the keyprobe itself.

set -u
set -o pipefail

# ── Config ────────────────────────────────────────────────────────────────────
CAM_IP="${CAM_IP:-10.0.0.169}"
CAM_ESEEID="${CAM_ESEEID:-4781620744}"
KNOWN_PASS="${KNOWN_PASS:-BossCamCtrl2026!}"
MITM_DURATION="${MITM_DURATION:-360}"
POLL_TIMEOUT="${POLL_TIMEOUT:-180}"
RESET_RETRIES="${RESET_RETRIES:-2}"
ARCHIVE_DIR="${ARCHIVE_DIR:-captures/archive-controlled-baseline}"
AUTO_ADMIN="${AUTO_ADMIN:-0}"
P2P_FORGE_IP="${P2P_FORGE_IP:-129.153.101.14}"
STUN_FORGE_IP="${STUN_FORGE_IP:-14.17.121.21}"
# LITE-aware grant: the ws-server grants LITE 0x00 keepalives counter + WS_LITE_CADENCE
# (default 0x1E — deliberately NON-natural, the camera's own cadence is +0x14, so
# adoption is distinguishable from the camera ignoring us) and logs each observed
# LITE delta (LITE_DELTA) + any FULL 0x11 escalation (FULL_ESCALATION). The post-
# reset §5.7 run therefore answers BOTH the §7 verify question and the LITE-grant
# question (adopted vs ignored) in one pass.
WS_LITE_MONITOR="${WS_LITE_MONITOR:-1}"
WS_LITE_CADENCE="${WS_LITE_CADENCE:-0x1E}"
# §5.1b pre-reset LITE baseline window: how long we OBSERVE the camera's LITE
# cadence (monitor-only — grant cadence disabled so the camera keeps its own
# +0x14 natural cadence and the gate cannot flip) BEFORE the §5.2 reset, so a
# sticky pre-existing LITE_ADOPTED from an earlier run cannot be mistaken for
# a post-reset outcome. The DONE summary cross-references lite-baseline.json
# against lite-verdict.json for the attribution verdict.
LITE_BASELINE_DURATION="${LITE_BASELINE_DURATION:-90}"
# §5.3b WiFi re-provision config (see header block above).
# Serial derives from the eseeid: observed convention eseeid 4780038910 ->
# serial JAZ7C34780038910 (leading "4" stripped) -> AP SSID IPCZ7C34780038910.
REPRO_SERIAL="${REPRO_SERIAL:-JAZ7C34${CAM_ESEEID#4}}"
REPRO_AP_PASS="${REPRO_AP_PASS:-}"
REPRO_STA_SSID="${REPRO_STA_SSID:-Aegon}"
REPRO_STA_PASS="${REPRO_STA_PASS:-812354444}"
REPRO_CAM_MAC_PREFIX="${REPRO_CAM_MAC_PREFIX:-9c:a3:a9}"
REPRO_SUBNET="${REPRO_SUBNET:-10.0.0}"
REPRO_MAX_ATTEMPTS="${REPRO_MAX_ATTEMPTS:-4}"
REPRO_WAIT="${REPRO_WAIT:-30}"
REPRO_DISABLED="${REPRO_DISABLED:-0}"
ZERO_BEFORE_REPROVISION="${ZERO_BEFORE_REPROVISION:-2}"
# §5.8 REST key probe: after the post-reset MITM relaunch, DELEGATE to
# $REPRO_SCRIPT --keyprobe-only (the re-provision script's single keyprobe code
# path — same one used after STEP 6 and by standalone --keyprobe-only runs) so the
# run records the REST key truth in the same pass as the LITE verdict and §7
# matrix. The probe is a semantic no-op (identical values read back first). A
# missing/failed probe is a WARNING, never a run failure. Plan A already ran §5.5
# set_pass, so ADMIN_PASS=$KNOWN_PASS reaches the wrapper (Plan B stays blank).
VERIFY_KEYPROBE="${VERIFY_KEYPROBE:-1}"
# DONE ledger assertion (the §5.8 post-reset keyprobe line MUST land):
#   LEDGER_ASSERT=1       DONE renders scripts/ledger-report.sh --json and asserts the
#                         post-reset keyprobe line exists in the campaign ledger — written
#                         during THIS run with a REAL verdict (not "n/a", not stale).
#                         A missing/stale/n-a line FAILS the run loudly (die) so a run that
#                         lost its key-truth deliverable can never read as success; set 0 to skip.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SCAN_TOOL="$PROJECT_ROOT/local-camera-recovery/tools/scan_verify_pairs.py"
MITM_SCRIPT="$SCRIPT_DIR/eseecloud-mitm-capture.sh"
REPRO_SCRIPT="$SCRIPT_DIR/5523w-wifi-reprovision.sh"
KEYPROBE_SCRIPT="${KEYPROBE_SCRIPT:-$SCRIPT_DIR/5523w-interface4-keyprobe.sh}"
LEDGER_REPORT_SCRIPT="$SCRIPT_DIR/ledger-report.sh"
LEDGER_DIR="${LEDGER_DIR:-$PROJECT_ROOT/local-camera-recovery/ledger}"

# Full runs write under captures/ (root-owned; we run as root). Eval mode
# must NOT touch captures/ — the MITM sessions there were created via sudo, so
# a non-root eval would hit a permission error. Eval uses /tmp instead.
RUN_DIR="$PROJECT_ROOT/captures/controlled-verify-$(date -u +%Y%m%dT%H%M%SZ)"
LOG="$RUN_DIR/run.log"
EVAL_RUN_DIR="/tmp/controlled-verify-eval-$(date -u +%Y%m%dT%H%M%SZ)"
# Run identity instant (UTC ISO) derived from the RUN_DIR timestamp — the DONE
# ledger assertion compares ledger line timestamps against it to prove the
# post-reset keyprobe line was written BY THIS RUN, not a stale prior one.
RUN_TS="$(basename "$RUN_DIR")"; RUN_TS="${RUN_TS#controlled-verify-}"
RUN_START_ISO=$(printf '%s-%s-%sT%s:%s:%sZ' \
  "${RUN_TS:0:4}" "${RUN_TS:4:2}" "${RUN_TS:6:2}" \
  "${RUN_TS:9:2}" "${RUN_TS:11:2}" "${RUN_TS:13:2}")

# ── Helpers ───────────────────────────────────────────────────────────────────
log()  { printf '%s  %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$LOG"; }
die()  { log "!! $*"; exit 1; }
sep()  { printf '\n%s\n' "$1" | tee -a "$LOG"; }

usage() {
  # Header-only extraction: skip the shebang (line 1), print the contiguous
  # leading #-comment block, and STOP at the first non-comment line — so body
  # comments (section banners, inline explanations) never leak into --help.
  awk 'NR==1{next} /^#/{sub(/^# ?/, ""); print; next} {exit}' "$0"
  exit 0
}

http_code() { # $1=ip $2=user[:pass] $3=urlpath  -> prints ONE HTTP code token (000 on failure)
  # curl -w already prints 000 on a failed attempt; capturing it and echoing ONCE
  # avoids double-printing (000 + "|| echo 000") which would break vanish detection.
  local code
  code=$(curl -sS -o /dev/null -w '%{http_code}' -m 8 -u "$2" "http://$1$3" 2>/dev/null || true)
  echo "${code:-000}"
}

# ── LITE-grant verdict (the §5.7 bonus question) ────────────────────────────
# The post-reset MITM relaunch runs with the LITE-aware grant (--lite-cadence
# 0x1E). The ws-server logs every observed LITE 0x00 counter delta (LITE_DELTA,
# keyed by pconv since each 20s re-dial is a fresh TCP connection) and flags a
# FULL 0x11 after any grant (FULL_ESCALATION). Reading the connections log
# classifies the LITE experiment outcome for the SAME run:
#   LITE_ADOPTED    — a LITE delta equals our granted 0x1E cadence (camera took the grant)
#   LITE_IGNORED    — deltas stayed at natural +0x14 (LITE form is not grantable)
#   LITE_ESCALATION — a FULL 0x11 arrived after a grant (adoption precondition met)
#   LITE_UNKNOWN    — no LITE frames seen this run
# Defined in the helpers section so BOTH the --eval mode (which dispatches
# before the full-run function definitions) and the full run can call it.
report_lite_verdict() { # $1=session-dir ; writes $RUN_DIR/lite-verdict.json
  local sess="$1" lite_lines esc_lines verdict hint
  lite_lines=$(grep -c "LITE_DELTA" "$sess/eseecloud-connections.log" 2>/dev/null || true)
  esc_lines=$(grep -c "FULL_ESCALATION" "$sess/eseecloud-connections.log" 2>/dev/null || true)
  lite_lines=${lite_lines:-0}; esc_lines=${esc_lines:-0}
  # Precedence: ADOPTED answers the §5.7 question directly (adopted vs ignored),
  # so it wins even when the camera ALSO escalated to FULL (that bonus fact is
  # appended to the hint). ESCALATION is only the headline when no LITE deltas
  # classify the grant outcome at all.
  if grep -q "ADOPTED — equals our granted lite-cadence" "$sess/eseecloud-connections.log" 2>/dev/null; then
    verdict="LITE_ADOPTED"
    hint="LITE delta == our granted 0x1E cadence — the camera ADOPTED the LITE grant"
    [ "$esc_lines" -gt 0 ] && hint="$hint ; ALSO escalated to FULL 0x11 after a grant (adoption precondition met)"
  elif grep -q "natural +0x14 cadence" "$sess/eseecloud-connections.log" 2>/dev/null; then
    verdict="LITE_IGNORED"
    hint="LITE deltas stayed at natural +0x14 — the LITE form is NOT grantable; only the reset/FULL path unlocks"
    [ "$esc_lines" -gt 0 ] && hint="$hint ; note: a FULL 0x11 escalation was ALSO seen after a grant"
  elif [ "$esc_lines" -gt 0 ]; then
    verdict="LITE_ESCALATION"
    hint="FULL 0x11 registration after our grant — camera believes it has a valid cloud session (adoption precondition met); no LITE deltas to classify the grant outcome"
  elif [ "$lite_lines" -gt 0 ]; then
    verdict="LITE_UNKNOWN"
    hint="LITE deltas logged but neither adopted nor natural markers matched — inspect lite-verdict.json"
  else
    verdict="LITE_UNKNOWN"
    hint="no LITE 0x00 deltas or FULL escalations logged (camera may have gone straight to FULL, or the gate flipped instantly)"
  fi
  log "  LITE-grant verdict: $verdict — $hint"
  # Single write path: python dumps to stdout, the redirect creates the file.
  python3 - <<PYEOF > "$RUN_DIR/lite-verdict.json" 2>/dev/null || true
import json, sys
json.dump({"verdict": "$verdict", "hint": "$hint", "lite_delta_lines": $lite_lines, "full_escalations": $esc_lines}, sys.stdout, indent=2)
PYEOF
  echo "  lite-grant detail: $RUN_DIR/lite-verdict.json"
}

# ── §5.1b Pre-reset LITE baseline (monitor-only observation) ──────────────────
# Before the §5.2 physical reset, run a SHORT MITM window with the LITE grant
# DISABLED (lite_cadence=0 -> ws-server legacy cadence, which the camera never
# adopts — it keeps its own +0x14 natural advance and the /user/*.xml gate
# cannot flip). The ws-server still logs every LITE 0x00 frame (LITE_DELTA) so
# this window records what the camera's LITE cadence ACTUALLY is pre-reset:
#   * deltas +0x14            -> CLEAN (no pre-existing adoption)
#   * deltas +0x1e            -> ADOPTED (a PRIOR run's 0x1E grant is sticky
#                                in the long-booted camera — a post-reset
#                                LITE_ADOPTED would NOT be attributable)
#   * no LITE frames          -> NO_TRAFFIC (unproven — cannot attribute)
# The result is written to $RUN_DIR/lite-baseline.json and cross-referenced
# against the post-MITM lite-verdict.json in the DONE summary.
lite_baseline_check() { # $1=ip ; writes $RUN_DIR/lite-baseline.json
  local ip="$1" sess verdict hint delta_lines
  if [ "$WS_LITE_MONITOR" != "1" ]; then
    log "  ⏭ LITE baseline skipped (WS_LITE_MONITOR=0 — ws-server logs no LITE_DELTA lines)"
    return 0
  fi
  sep "═══ §5.1b LITE BASELINE (pre-reset, monitor-only) ═══"
  log "  observing $ip LITE cadence for ${LITE_BASELINE_DURATION}s with NO adoptable grant"
  log "  (lite_cadence=0 + next_counter=plus1 — camera adopts neither; gate cannot flip)"
  sess=$(run_mitm "$ip" "$LITE_BASELINE_DURATION" 0 "" plus1)
  log "  baseline session: ${sess:-none}"
  if [ -z "$sess" ] || [ ! -f "$sess/eseecloud-connections.log" ]; then
    verdict="LITE_BASELINE_UNKNOWN"
    hint="no baseline session/log produced — cannot record pre-reset LITE state"
    delta_lines=0
  else
    delta_lines=$(grep -c "LITE_DELTA" "$sess/eseecloud-connections.log" 2>/dev/null || true)
    delta_lines=${delta_lines:-0}
    if [ "$delta_lines" -eq 0 ]; then
      verdict="LITE_BASELINE_NO_TRAFFIC"
      hint="no LITE 0x00 frames in pre-reset window (camera not dialing LITE while locked, or window too short)"
    elif grep -qE "delta=\+0x1e([^0-9a-f]|$)" "$sess/eseecloud-connections.log" 2>/dev/null; then
      verdict="LITE_BASELINE_ADOPTED"
      hint="pre-reset LITE deltas show +0x1e — a PRIOR run's LITE grant is sticky in the camera; post-reset LITE_ADOPTED is NOT attributable to the reset run"
    elif grep -qE "delta=\+0x14([^0-9a-f]|$)" "$sess/eseecloud-connections.log" 2>/dev/null; then
      verdict="LITE_BASELINE_CLEAN"
      hint="pre-reset LITE deltas at natural +0x14 — no pre-existing adoption; post-reset LITE_ADOPTED IS attributable"
    else
      verdict="LITE_BASELINE_OTHER"
      hint="pre-reset LITE deltas neither +0x14 nor +0x1e — inspect the baseline session log"
    fi
  fi
  log "  LITE baseline (pre-reset): $verdict — $hint"
  python3 - <<PYEOF > "$RUN_DIR/lite-baseline.json" 2>/dev/null || true
import json, sys
json.dump({"verdict": "$verdict", "hint": "$hint", "session": "$sess", "lite_delta_lines": $delta_lines}, sys.stdout, indent=2)
PYEOF
  echo "  lite-baseline detail: $RUN_DIR/lite-baseline.json"
}

# ── Per-LITE-delta cadence table (offline review aid) ─────────────────────────
# Renders every LITE_DELTA line from the session's connections log as an
# aligned table (timestamp, pconv, counter range, delta, interval, note) so a
# session review shows the cadence PROGRESSION — first-seen, natural +0x14
# steps, adopted +0x1e steps — not just the verdict bucket. Handles both log
# shapes the ws-server emits (see eseecloud-ws-server.py _lite_delta_check):
#   <iso> LITE_DELTA pconv=... counter=... first-seen (no prior LITE frame...)
#   <iso> LITE_DELTA pconv=... c0->ctr delta=+0xNN over X.Xs <note>
# Writes $RUN_DIR/lite-delta-table.txt and echoes it to the console. Unparsable
# LITE_DELTA lines are kept as RAW rows so nothing is silently dropped.
report_lite_delta_table() { # $1=session-dir
  local sess="$1" f
  f="$sess/eseecloud-connections.log"
  if [ ! -f "$f" ]; then
    log "  ⏭ no connections log ($f) — LITE delta table skipped"
    return 0
  fi
  python3 - "$f" "$RUN_DIR/lite-delta-table.txt" <<'PYEOF' || return 0
import re, sys
path, out = sys.argv[1], sys.argv[2]
delta_pat = re.compile(r'^(\S+) LITE_DELTA pconv=([0-9a-f]+) ([0-9a-f]+)->([0-9a-f]+) delta=\+0x([0-9a-f]+) over ([\d.]+)s(.*)$')
first_pat = re.compile(r'^(\S+) LITE_DELTA pconv=([0-9a-f]+) counter=([0-9a-f]+) first-seen(.*)$')
rows = []
for line in open(path, errors='replace'):
    line = line.strip()
    if 'LITE_DELTA' not in line:
        continue  # only LITE_DELTA lines belong in this table
    m = delta_pat.match(line)
    if m:
        ts, pconv, c0, c1, d, iv, note = m.groups()
        note = re.sub(r'\s+port=\d+\s+src=\S+$', '', note).strip()
        rows.append((ts, pconv, f'{c0}->{c1}', f'+0x{d}', f'{iv}s', note))
        continue
    m = first_pat.match(line)
    if m:
        ts, pconv, c, note = m.groups()
        note = re.sub(r'\s+port=\d+\s+src=\S+$', '', note).strip()
        rows.append((ts, pconv, f'counter={c}', '—', '—', note))
        continue
    rows.append((line[:19], '?', '?', '?', '?', 'RAW: ' + line))
if not rows:
    print(f'  (no LITE_DELTA lines in {path})')
    sys.exit(0)
lines = [f'  LITE cadence progression ({len(rows)} deltas):']
hdr = '  {:<20} {:<10} {:<22} {:<7} {:<7} note'.format('time (UTC)', 'pconv', 'c0->ctr', 'delta', 'over')
lines.append(hdr)
lines.append('  ' + '-' * (len(hdr) - 2))
for ts, pconv, c, d, iv, note in rows:
    lines.append('  {:<20} {:<10} {:<22} {:<7} {:<7} {}'.format(ts[:19], pconv, c, d, iv, note))
open(out, 'w').write('\n'.join(lines) + '\n')
print('\n'.join(lines))
PYEOF
  # Only echo the detail line if python actually wrote the table (the no-rows
  # path exits 0 without writing, so the file check keeps the echo honest). The
  # if/elif keeps the function's exit status 0 on every path — a review aid must
  # never turn a successful eval into a non-zero return.
  if [ -f "$RUN_DIR/lite-delta-table.txt" ]; then
    echo "  lite-delta detail: $RUN_DIR/lite-delta-table.txt"
  else
    :
  fi
}

# ── §5.8 Post-reset REST key truth probe ─────────────────────────────────────
# DELEGATES to $REPRO_SCRIPT --keyprobe-only — the single keyprobe code path shared
# by the re-provision (STEP 6), standalone --keyprobe-only runs, and this §5.8 step —
# so every keyprobe run in the campaign uses one implementation. After the §5.7 MITM
# relaunch, the wrapper runs the interface-4 REST key probe once against the post-reset
# camera so this run records the key truth (GET mode key + PUT round-trip verdict) in
# the SAME pass as the LITE-grant verdict and the §7 verify-pair diff matrix. The probe
# writes IDENTICAL values (read-back-first), so it is a semantic no-op; a missing/failed
# probe is a WARNING, never a run failure — the §7 verdict and LITE verdict are already
# recorded by this point. Plan A has run §5.5 set_pass, so ADMIN_PASS=$KNOWN_PASS is
# passed to the wrapper; Plan B keeps the factory blank admin. The wrapper exits 0 even
# when its internal probe fails (it warns itself), so THIS function's failure signal is
# "no [keyprobe] verdict line in the wrapper output" — not the exit code.
keyprobe_truth_check() { # $1=ip
  local ip="$1" out kp_rc admin_pass=""
  if [ "${PLAN:-}" = "A" ]; then admin_pass="$KNOWN_PASS"; fi
  if [ "${VERIFY_KEYPROBE:-1}" != "1" ]; then
    log "  key probe disabled (VERIFY_KEYPROBE=0)"
    return 0
  fi
  if [ ! -x "$REPRO_SCRIPT" ]; then
    log "  ⚠ REPRO_SCRIPT '$REPRO_SCRIPT' not executable — skipping key probe"
    return 0
  fi
  log "  running REST key probe via: $REPRO_SCRIPT --keyprobe-only $ip (admin:${admin_pass:+<set>})"
  # Delegate to the re-provision script's --keyprobe-only mode. Pass ADMIN_PASS
  # (KNOWN_PASS for Plan A, blank for Plan B), the ABSOLUTE KEYPROBE_SCRIPT (the
  # re-provision's own default is CWD-relative, which would break under a different
  # working dir), and an absolute LEDGER_DIR so the campaign ledger append lands in
  # the right place regardless of CWD. STA_SSID/STA_PASS mirror the §5.3b full
  # re-provision invocation so the wrapper's `trap restore_network EXIT` is a
  # correct no-op when we're already on the re-provision network (without them, a
  # subprocess exit could trigger an unexpected nmcli WiFi connect). The wrapper
  # runs the keyprobe subprocess with LEDGER_APPEND=0 and owns the ledger line
  # itself (source: reprovision).
  out=$(ADMIN_PASS="$admin_pass" KEYPROBE_SCRIPT="$KEYPROBE_SCRIPT" \
        LEDGER_DIR="$LEDGER_DIR" \
        STA_SSID="$REPRO_STA_SSID" STA_PASS="$REPRO_STA_PASS" \
        "$REPRO_SCRIPT" --keyprobe-only "$ip" 2>&1) && kp_rc=0 || kp_rc=$?
  if [ "$kp_rc" != "0" ]; then
    log "  ⚠ key probe wrapper exited $kp_rc — ${out:0:120} (REST key truth not recorded)"
    return 0
  fi
  if [ -z "$out" ]; then
    log "  ⚠ key probe wrapper produced no output — REST key truth not recorded"
    return 0
  fi
  # The wrapper already prints [keyprobe]-prefixed verdict lines (and warns itself
  # on internal probe failure) — relay them. Absent any verdict line, the wrapper
  # could not record the key truth (missing keyprobe, crash, or camera auth fail).
  if ! printf '%s\n' "$out" | grep -q '\[keyprobe\]'; then
    log "  ⚠ no key probe verdict lines in wrapper output — REST key truth not recorded"
    return 0
  fi
  printf '%s\n' "$out" | grep '\[keyprobe\]' | sed 's/^/  /' || true
  log "  key probe done — REST key verdict above"
}

# ── DONE ledger assertion (the post-reset keyprobe line MUST exist) ──────────
# The §5.8 keyprobe is deliberately non-fatal mid-run (a probe hiccup must not
# abort the §7 matrix), but the key truth is one of this run's THREE deliverables
# (verify pairs, LITE verdict, key truth) — so the DONE summary asserts it before
# declaring success. Render the campaign ledger via scripts/ledger-report.sh
# --json and require a line for THIS run's target camera that
#   (a) was written DURING this run (last_ts >= RUN_START_ISO — proves it is
#       the post-reset line, not a stale prior run's),
#   (b) carries a REAL verdict — not "n/a" (keyprobe refused / auth failed),
#       not "?" (key never recorded), not a JSONL parse error.
# The post-reset serial can legitimately differ from REPRO_SERIAL if the eseeid
# changed (§9), so a fresh ip-matched line (latest_ip == CAM_IP) is accepted too.
# Any failure ⇒ die loudly with the full ledger state printed. Note: a §5.3b
# re-provision keyprobe line written EARLIER in the same run (factory-state
# blank admin, real verdict, same serial) also satisfies the assertion — the
# key truth was still recorded post-reset; the ✅ does not distinguish which
# step wrote the line. Gated on the same conditions as keyprobe_truth_check
# (VERIFY_KEYPROBE=1 + executable wrapper) plus LEDGER_ASSERT=0 to opt out.
assert_post_reset_ledger_line() {
  local out rc ledger_file
  if [ "${LEDGER_ASSERT:-1}" != "1" ]; then
    log "  ⏭ ledger assertion skipped (LEDGER_ASSERT=0)"
    return 0
  fi
  if [ "${VERIFY_KEYPROBE:-1}" != "1" ]; then
    log "  ⏭ ledger assertion skipped (VERIFY_KEYPROBE=0 — keyprobe never ran)"
    return 0
  fi
  if [ ! -x "$REPRO_SCRIPT" ]; then
    log "  ⏭ ledger assertion skipped (REPRO_SCRIPT not executable — keyprobe never ran)"
    return 0
  fi
  if [ ! -x "$LEDGER_REPORT_SCRIPT" ]; then
    die "ledger assertion impossible: ledger-report.sh missing/not executable ($LEDGER_REPORT_SCRIPT)"
  fi
  log "  asserting post-reset keyprobe ledger line — serial=$REPRO_SERIAL ip=$CAM_IP since=$RUN_START_ISO"
  out=$(LEDGER_DIR="$LEDGER_DIR" "$LEDGER_REPORT_SCRIPT" --json 2>&1)
  rc=$?
  if [ "$rc" != "0" ]; then
    log "  !! ledger-report --json exited $rc — $(printf '%s' "$out" | head -1)"
    die "post-reset keyprobe line NOT verifiable — campaign ledger unreadable ($RUN_DIR)"
  fi
  ledger_file="$RUN_DIR/ledger-snapshot.json"
  printf '%s' "$out" > "$ledger_file"
  out=$(python3 - "$REPRO_SERIAL" "$CAM_IP" "$RUN_START_ISO" "$ledger_file" 2>&1 <<'PYEOF'
import json, sys
expected, cam_ip, since, path = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
ledger = json.load(open(path))

def ok(v):
    return v not in ("n/a", "?") and "JSONL parse error" not in str(v)

target = next((c for c in ledger if c["serial"] == expected), None)
picks = []
if target is not None:
    picks.append((target["serial"], target["last_ts"], target["latest_ip"],
                  str(target["verdict"]), "serial-match"))
for c in ledger:
    if c["serial"] != expected and c["latest_ip"] == cam_ip:
        picks.append((c["serial"], c["last_ts"], c["latest_ip"],
                      str(c["verdict"]), "ip-match (serial changed post-reset?)"))
# Freshest line first: ANY fresh real-verdict line (serial- OR ip-match) proves
# the key truth was recorded post-reset, so the newest one is the best evidence.
picks.sort(key=lambda p: p[1], reverse=True)
for serial, ts, ip, verdict, how in picks:
    if ts >= since and ok(verdict):
        print(f"  ✅ post-reset keyprobe ledger line present — {serial} {ts} ip={ip} ({how})")
        print(f"     verdict: {verdict}")
        sys.exit(0)
print("  !! post-reset keyprobe ledger line MISSING or non-verdict (this run):")
for serial, ts, ip, verdict, how in picks:
    print(f"     {serial:24s} last_ts={ts} ip={ip} verdict={verdict}  ({how})")
print("     all ledger entries (reference):")
for c in ledger:
    print(f"     {c['serial']:24s} last_ts={c['last_ts']} ip={c['latest_ip']} verdict={c['verdict']}")
sys.exit(1)
PYEOF
  )
  rc=$?
  printf '%s\n' "$out" | while read -r l; do log "  $l"; done
  if [ "$rc" != "0" ]; then
    die "post-reset keyprobe line NOT recorded — the run's key-truth deliverable is missing (see ledger detail above; run dir $RUN_DIR). Fix the §5.8 keyprobe path and re-run the experiment."
  fi
}

# ── Mode dispatch ─────────────────────────────────────────────────────────────
MODE="full"
case "${1:-}" in
  -h|--help) usage ;;
  --eval)
    [ "$#" -ge 2 ] || { echo "usage: $0 --eval <session-dir>"; exit 1; }
    MODE="eval"; EVAL_SESSION="${2%/}"
    ;;
esac

if [ "$MODE" = "eval" ]; then
  # Eval mode must exit BEFORE the full-run mkdir below: captures/ is root-
  # owned (MITM sessions were created via sudo), so a non-root eval would die
  # on permission denied. Eval writes to /tmp instead.
  RUN_DIR="$EVAL_RUN_DIR"
  LOG="$RUN_DIR/run.log"
  mkdir -p "$RUN_DIR" || die "cannot create $RUN_DIR"
  : > "$LOG"
  sep "═══ OFFLINE §7 EVALUATION — $EVAL_SESSION ═══"
  # A deviation verdict (row C/D) is a legitimate experiment OUTCOME, not an
  # error — log it and still print the summary (do not die).
  python3 "$PROJECT_ROOT/local-camera-recovery/tools/eval_verify_matrix.py" \
    "$EVAL_SESSION/eseecloud-connections.log" "$KNOWN_PASS" "$RUN_DIR/verdict.json" \
    || log "  ⚠ verdict is a deviation (row C/D or PARTIAL) — see $RUN_DIR/verdict.json"
  python3 - <<PYEOF
import json
v = json.load(open("$RUN_DIR/verdict.json"))
print("  verdict:", v["verdict"])
print("  pairs  :", len(v["pairs"]))
print("  full detail in $RUN_DIR/verdict.json")
PYEOF
  # §5.7 bonus: LITE-grant verdict for the evaluated session (offline, no camera).
  report_lite_verdict "$EVAL_SESSION"
  # Cadence progression table so the review shows each LITE delta, not just the bucket.
  report_lite_delta_table "$EVAL_SESSION"
  exit 0
fi

mkdir -p "$RUN_DIR" || die "cannot create $RUN_DIR"
: > "$LOG"

# ── Full run ──────────────────────────────────────────────────────────────────
if [ "$(id -u)" -ne 0 ]; then
  echo "Full run requires root (MITM relaunch needs ARP spoof + iptables)." >&2
  echo "Re-run as:  sudo $0" >&2
  exit 1
fi
[ -x "$MITM_SCRIPT" ]    || die "MITM script missing: $MITM_SCRIPT"
[ -f "$SCAN_TOOL" ]      || die "scan tool missing: $SCAN_TOOL"
command -v curl >/dev/null || die "curl required"
command -v python3 >/dev/null || die "python3 required"

# §9 guardrail: 10.0.0.29 is the CONTROL camera — never reset it unless the
# .169 experiment succeeded or the operator explicitly authorizes it.
if [ "$CAM_IP" = "10.0.0.29" ] && [ "${RESET_29_AUTHORIZED:-0}" != "1" ]; then
  echo "!! §9 guardrail: do not reset 10.0.0.29 (the control camera) unless the .169" >&2
  echo "   experiment succeeded or you explicitly authorize it. Set" >&2
  echo "   RESET_29_AUTHORIZED=1 to proceed with CAM_IP=$CAM_IP." >&2
  exit 1
fi

sep "═══ CONTROLLED VERIFY EXPERIMENT — READY-FOR-RUN ($CAM_IP) ═══"
log "  known password : $KNOWN_PASS"
log "  baseline eseeid: $CAM_ESEEID (compare post-reset)"
log "  run dir        : $RUN_DIR"

# ── §5.1 Archive baseline (read-only copy of the §3 session dirs) ────────────
sep "═══ §5.1 ARCHIVE BASELINE ═══"
mkdir -p "$ARCHIVE_DIR" 2>/dev/null || true
for s in "20260808T232711Z" "20260809T002245Z" "20260808T050802Z"; do
  src="$PROJECT_ROOT/captures/eseecloud-mitm-$s"
  if [ -d "$src" ] && [ ! -e "$ARCHIVE_DIR/eseecloud-mitm-$s" ]; then
    cp -a "$src" "$ARCHIVE_DIR/" 2>/dev/null && log "  archived $s" || log "  !! could not archive $s"
  else
    log "  baseline $s already archived or absent ($src)"
  fi
done

# ── §5.2 Physical reset (the ONE manual step) ────────────────────────────────
reset_prompt() {
  echo
  echo "▸▸ MANUAL STEP: hold the RESET button on $CAM_IP for 10–15 s"
  echo "   until the camera reboots (status LED cycles). Do NOT run any"
  echo "   enroll/recovery script — this experiment owns the password."
  echo "   After the reset the camera leaves the WiFi and becomes its own AP;"
  echo "   §5.3b will auto-invoke scripts/5523w-wifi-reprovision.sh to bring it"
  echo "   back on $REPRO_STA_SSID (this laptop's WiFi drops briefly)."
  if [ -t 0 ]; then
    read -r -p "   Press Enter when the reset is done (or 's' to skip this attempt)… " ans
    [ "${ans:-}" = "s" ] && return 1
  else
    log "  (non-interactive stdin — waiting ${POLL_TIMEOUT}s for the reset to land)"
    sleep "$POLL_TIMEOUT"
  fi
  return 0
}

# ── §5.3b WiFi re-provision (camera drops off the LAN after a factory reset) ──
# A factory reset wipes the 5523-W's WiFi station credentials along with the
# admin password, so the camera leaves the LAN (HTTP 000) and becomes its own
# AP (SSID IPCZ7C34<serial>). poll_factory() used to poll the OLD IP forever —
# it can never come back there. This step detects the vanish and AUTO-INVOKES
# scripts/5523w-wifi-reprovision.sh, which joins the camera AP, verifies factory
# state over the AP link, writes station-mode WiFi config, and re-discovers the
# camera on the LAN by MAC. On success CAM_IP is updated to the NEW LAN IP and
# every downstream step (§5.4 gate, §5.5 set_pass, §5.7 MITM) targets it.
#
# The re-provision tool may take a while: the camera AP must appear in the wifi
# scan (it boots for ~30-60s after the reset), then join + write + rejoin.
reprovision_camera() { # $1=ip-that-vanished ; returns 0 on success (CAM_IP updated)
  local old_ip="$1" attempt new_ip code out repro_out
  sep "═══ §5.3b WIFI RE-PROVISION (camera left the LAN after reset) ═══"
  log "  $old_ip is unreachable (HTTP 000 sustained) — factory reset wiped its WiFi station config"
  log "  invoking $REPRO_SCRIPT (serial→AP $REPRO_SERIAL, rejoin $REPRO_STA_SSID)"
  attempt=0
  while [ "$attempt" -lt "$REPRO_MAX_ATTEMPTS" ]; do
    attempt=$((attempt + 1))
    log "  re-provision attempt $attempt/$REPRO_MAX_ATTEMPTS…"
    if [ ! -x "$REPRO_SCRIPT" ]; then
      log "  ⚠ re-provision tool missing/not executable: $REPRO_SCRIPT"
      return 1
    fi
    # Machine-readable handoff: the tool writes the new LAN IP to $REPRO_OUT if
    # the env var is set. Fall back to parsing its "…at: <ip>" banner line.
    repro_out="$RUN_DIR/reprovision-${attempt}-ip.txt"
    rm -f "$repro_out"
    # Serial arg first; if the derived serial is wrong for this unit, the tool's
    # auto-pick mode (no arg → the only visible IPCZ7C34* AP) recovers the case.
    if [ -n "$REPRO_SERIAL" ]; then
      REPRO_OUT="$repro_out" STA_SSID="$REPRO_STA_SSID" STA_PASS="$REPRO_STA_PASS" \
        CAM_MAC_PREFIX="$REPRO_CAM_MAC_PREFIX" SUBNET="$REPRO_SUBNET" \
        AP_PASS="$REPRO_AP_PASS" "$REPRO_SCRIPT" "$REPRO_SERIAL" 2>&1 | tee -a "$LOG" || true
    else
      REPRO_OUT="$repro_out" STA_SSID="$REPRO_STA_SSID" STA_PASS="$REPRO_STA_PASS" \
        CAM_MAC_PREFIX="$REPRO_CAM_MAC_PREFIX" SUBNET="$REPRO_SUBNET" \
        AP_PASS="$REPRO_AP_PASS" "$REPRO_SCRIPT" 2>&1 | tee -a "$LOG" || true
    fi
    new_ip=$(head -1 "$repro_out" 2>/dev/null || true)
    if [ -z "$new_ip" ]; then
      # Fallback: parse the banner line the tool always prints on success. Scope
      # the grep to the LAST 50 log lines so a stale "at: <ip>" from an earlier
      # step (e.g. a prior repro attempt) cannot misfire.
      out=$(tail -50 "$LOG" | grep -oE 'at: [0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' | tail -1 | awk '{print $2}')
      new_ip="$out"
    fi
    if [ -n "$new_ip" ] && [ "$(http_code "$new_ip" 'admin:' "/NetSDK/System/deviceInfo")" = "200" ]; then
      log "  ✅ re-provisioned: $old_ip → $new_ip (blank admin → HTTP 200)"
      CAM_IP="$new_ip"
      log "  ⚠ CAM_IP updated to $new_ip — all downstream steps (§5.4/§5.5/§5.7) target the new LAN IP"
      return 0
    fi
    log "  re-provision attempt $attempt did not yield a factory-state IP on the LAN (yet)"
    if [ "$attempt" -lt "$REPRO_MAX_ATTEMPTS" ]; then
      log "  waiting ${REPRO_WAIT}s for the camera AP / station-mode switch…"
      sleep "$REPRO_WAIT"
    fi
  done
  log "  ⚠ re-provision did not complete after $REPRO_MAX_ATTEMPTS attempts"
  return 1
}

# ── §5.3 Poll for factory state (blank admin → deviceInfo 200) ───────────────
# Returns: 0 = factory state confirmed (or camera re-provisioned, CAM_IP updated)
#          1 = not yet (keeps polling / gives up after the budget)
#          2 = camera VANISHED (HTTP 000 sustained) — §5.3b re-provision triggered
poll_factory() { # $1=ip
  local ip="$1" elapsed=0 started code zeroes=0 repro_tried=0
  started=$(date +%s)
  log "  polling $ip for factory state (blank admin → deviceInfo 200, budget ${POLL_TIMEOUT}s)"
  while [ $(( $(date +%s) - started )) -lt "$POLL_TIMEOUT" ]; do
    code=$(http_code "$ip" 'admin:' "/NetSDK/System/deviceInfo")
    if [ "$code" = "200" ]; then
      log "  ✅ factory state confirmed (blank admin → HTTP 200)"
      return 0
    fi
    if [ "$code" = "000" ]; then
      zeroes=$((zeroes + 1))
      log "  … deviceInfo=000 (unreachable, consecutive $zeroes)"
      # Trigger §5.3b ONCE per poll: a failed re-provision must not re-run every
      # 5s for the rest of the budget (the camera may just be slow to boot the AP).
      if [ "$zeroes" -ge "$ZERO_BEFORE_REPROVISION" ] && [ "$repro_tried" = "0" ] \
         && [ "$REPRO_DISABLED" != "1" ]; then
        repro_tried=1
        log "  ⚠ camera vanished from the LAN — a factory reset wipes WiFi, so it is now its own AP"
        if reprovision_camera "$ip"; then return 0; fi
        log "  ⚠ re-provision did not complete — continuing to poll $ip (operator may intervene)"
      fi
    else
      zeroes=0
      log "  … deviceInfo=$code (blank admin)"
    fi
    sleep 5
  done
  log "  ⚠ factory state not seen within ${POLL_TIMEOUT}s (last code=$code)"
  return 1
}

# ── Gate check (§5.4 / §6) ────────────────────────────────────────────────────
gate_state() { # prints OPEN | CLOSED | ERROR:<code>
  local ip="$1" code body
  body=$(curl -sS -m 8 "http://$ip/user/user_list.xml" 2>/dev/null)
  code=$?
  if [ "$code" -ne 0 ] || [ -z "$body" ]; then
    echo "ERROR:curl=$code"; return
  fi
  case "$body" in
    *"check in falied"*|*"check_in_falied"*) echo "CLOSED" ;;
    *"<user"*|*"ret=\"ok\""*|*"user_list"*) echo "OPEN" ;;
    *) echo "ERROR:unknown-body" ;;
  esac
}

# ── set_pass.xml shapes (§5.5 / §4.1) ────────────────────────────────────────
set_pass_query() { # $1=ip $2=current $3=new  -> prints body
  curl -sS -m 10 -G "http://$1/user/set_pass.xml" \
    --data-urlencode "username=admin" \
    --data-urlencode "password=$2" \
    --data-urlencode "content=$3" 2>/dev/null
}
set_pass_post() { # $1=ip $2=new  -> prints body
  curl -sS -m 10 -X POST "http://$1/user/set_pass.xml" \
    --data-urlencode "username=admin" \
    --data-urlencode "newPassword=$2" 2>/dev/null
}

# ── §5.6 Verify write ────────────────────────────────────────────────────────
verify_write() { # $1=ip $2=new
  local known blank
  known=$(http_code "$1" "admin:$2" "/NetSDK/System/deviceInfo")
  blank=$(http_code "$1" 'admin:' "/NetSDK/System/deviceInfo")
  log "  verify: known-password → HTTP $known (expect 200), blank → HTTP $blank (expect 401)"
  [ "$known" = "200" ] && [ "$blank" = "401" ]
}

# ── §5.7 Relaunch the MITM (run-8 config: plain :80 + TLS :9900 forge) ───────
run_mitm() { # $1=ip [$2=duration] [$3=lite_cadence] [$4=lite_monitor] [$5=next_counter_mode]
  local started newest dur="${2:-$MITM_DURATION}" lite_cad="${3:-$WS_LITE_CADENCE}" lite_mon="${4:-$WS_LITE_MONITOR}" ncm="${5:-cadence}"
  log "  relaunching MITM on $1 only (${dur}s window, AUTO_ADMIN=0, lite_cadence=$lite_cad, next_counter=$ncm)"
  started=$(date +%s)
  # pipefail makes a failed MITM script propagate instead of being masked by tee.
  AUTO_ADMIN=0 P2P_FORGE_IP="$P2P_FORGE_IP" STUN_FORGE_IP="$STUN_FORGE_IP" \
    WS_LITE_MONITOR="$lite_mon" WS_LITE_CADENCE="$lite_cad" WS_NEXT_COUNTER="$ncm" \
    "$MITM_SCRIPT" "$dur" "$1" 2>&1 | tee -a "$LOG" || true
  # Newest session dir with mtime >= our start — a stale pre-run dir cannot be
  # picked up if the MITM fails to create a session.
  newest=$(find "$PROJECT_ROOT/captures" -maxdepth 1 -type d -name 'eseecloud-mitm-*' \
    -newermt "@$started" -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2-)
  echo "$newest"
}

# ── §5.8 + §7 Post-state extraction & diff-matrix evaluation ─────────────────
eval_session() { # $1=session-dir
  local sess="$1"
  log "  evaluating post-state pairs in $sess"
  python3 "$PROJECT_ROOT/local-camera-recovery/tools/eval_verify_matrix.py" \
    "$sess/eseecloud-connections.log" "$KNOWN_PASS" "$RUN_DIR/verdict.json" \
    || log "  ⚠ matrix evaluation reported a deviation (see $RUN_DIR/verdict.json)"
  # §7 confirmatory cross-check via the established scanner (reuse).
  python3 "$SCAN_TOOL" "$sess" 2>&1 | grep -E 'RESULT|ALL PAIRS|ERROR' | while read -r l; do log "  scan: $l"; done
  # §5.7 bonus: the LITE-grant verdict (adopted vs ignored) from the same run.
  report_lite_verdict "$sess"
  # Cadence progression table (same review as --eval mode).
  report_lite_delta_table "$sess"
  echo "  §7 verdict + pairs written to $RUN_DIR/verdict.json"
}

# ══ MAIN FLOW ═══════════════════════════════════════════════════════════════
# §5.1b pre-reset LITE baseline: record the camera's LITE cadence BEFORE the
# §5.2 reset so a sticky pre-existing LITE_ADOPTED (from an earlier run) cannot
# be mistaken for a post-reset outcome. DONE cross-references the baseline.
lite_baseline_check "$CAM_IP"

sep "═══ §5.2 PHYSICAL RESET ═══"
attempt=1
while [ "$attempt" -le "$RESET_RETRIES" ]; do
  if ! reset_prompt; then
    log "  reset attempt $attempt skipped by operator — retrying prompt"
  fi
  if poll_factory "$CAM_IP"; then break; fi
  log "  reset attempt $attempt did not take"
  attempt=$((attempt + 1))
done
if [ "$attempt" -gt "$RESET_RETRIES" ]; then
  die "factory state not reached after $RESET_RETRIES attempts (incl. §5.3b re-provision if the camera vanished) — re-check the reset and re-run (report says: re-poll / re-reset up to 2 tries, else abort)."
fi

# §9 risk: capture post-reset eseeid and compare to baseline.
sep "═══ POST-RESET ESEED CHECK (§9) ═══"
POST_ESEEID=$(curl -sS -m 8 -u 'admin:' "http://$CAM_IP/NetSDK/System/deviceInfo" 2>/dev/null \
  | grep -oE '[0-9]{10}' | head -1)
if [ -n "$POST_ESEEID" ]; then
  log "  post-reset eseeid: $POST_ESEEID (baseline $CAM_ESEEID)"
  if [ "$POST_ESEEID" != "$CAM_ESEEID" ]; then
    log "  ⚠ eseeid changed post-reset — eseeid-derived salt families lose their baseline constant;"
    log "    new eseeid is observable in every post_v2 URL, so the §7 matrix remains fully testable."
  fi
else
  log "  ⚠ could not read post-reset eseeid from deviceInfo"
fi

# §5.4 Gate check — decision point A (§6 tree).
sep "═══ §5.4 GATE CHECK ═══"
GATE=$(gate_state "$CAM_IP")
log "  /user/user_list.xml gate: $GATE"
PLAN="A"
case "$GATE" in
  OPEN) log "  → gate OPEN: proceeding with Plan A (set_pass known password)" ;;
  CLOSED)
    log "  → gate CLOSED ('check in falied'): per §6, try set_pass anyway — the gate may only gate reads."
    ;;
  ERROR:*)
    log "  → gate state unknown ($GATE): proceeding with Plan A attempt; record the outcome."
    ;;
esac

# §5.5 Set known password — Plan A. Query shape first; POST shape fallback.
sep "═══ §5.5 SET KNOWN PASSWORD (Plan A) ═══"
SP_QUERY=$(set_pass_query "$CAM_IP" "" "$KNOWN_PASS")
log "  GET  set_pass.xml?username=admin&password=&content=… → $SP_QUERY"
SP_RESP="$(printf '%s' "$SP_QUERY" | grep -oE 'ret="[^"]*"' | head -1)"
SP_MSG="$(printf '%s' "$SP_QUERY" | grep -oE 'mesg="[^"]*"' | head -1)"
case "$SP_RESP" in
  *ok*) log "  ✅ set_pass returned ret=\"ok\" → verify write" ;;
  *)
    log "  set_pass GET shape did not return ret=\"ok\" (resp=$SP_RESP msg=$SP_MSG) — trying POST shape"
    SP_POST=$(set_pass_post "$CAM_IP" "$KNOWN_PASS")
    log "  POST set_pass.xml (username=admin&newPassword=…) → $SP_POST"
    SP_RESP="$(printf '%s' "$SP_POST" | grep -oE 'ret="[^"]*"' | head -1)"
    case "$SP_RESP" in
      *ok*) log "  ✅ set_pass POST returned ret=\"ok\" → verify write" ;;
      *)
        case "$SP_MSG$SP_POST" in
          *"check in falied"*)
            sep "═══ PLAN B (factory-state MITM, password NOT set) ═══"
            log "  §6: set_pass blocked by the gate in both shapes ('check in falied')."
            log "  Plan B: run the MITM in factory state — default admin is blank (or a known constant);"
            log "  captured pairs are computed with that KNOWN default → diff still works."
            log "  ⚠ FATAL ASSUMPTION: a factory camera with no cloud binding may never post_v2 —"
            log "    if 0 pairs result, STOP and re-plan (documented in §6)."
            PLAN="B"
            ;;
          *)
            die "set_pass blocked with a NON-gate message ($SP_RESP $SP_MSG) — capture the exact error, STOP, document; do not guess (§6)."
            ;;
        esac
        ;;
    esac
    ;;
esac

# §5.6 Verify write — only meaningful in Plan A.
if [ "$PLAN" = "A" ]; then
  sep "═══ §5.6 VERIFY WRITE ═══"
  if verify_write "$CAM_IP" "$KNOWN_PASS"; then
    log "  ✅ known password accepted, blank rejected — write verified"
  else
    log "  ⚠ write verification did not match expectations — record and continue (gate may be re-gating)"
  fi
fi

# §5.7 Relaunch the MITM (the /message/ chain rides plain :80).
sep "═══ §5.7 MITM RELAUNCH ($MITM_DURATION s) ═══"
log "  ▶ WATCH the MITM output below for the trigger line (first check-in / NONCE_FORGED / MESSAGE_POST)."
log "    At that moment, power-cycle $CAM_IP to attempt a boot-time FULL 0x11 (bonus objective only)."
NEW_SESSION=$(run_mitm "$CAM_IP")  log "  newest session dir: $NEW_SESSION"

# §5.8: record the REST key truth (GET mode key + PUT round-trip verdict) from the
# post-reset camera in the same pass as the §7 matrix + LITE verdict below. Delegated
# to $REPRO_SCRIPT --keyprobe-only (ADMIN_PASS=$KNOWN_PASS for Plan A, blank for Plan
# B) — the single keyprobe code path. Non-fatal by design.
keyprobe_truth_check "$CAM_IP"

# §5.8 / §7 Extract post-state pairs and evaluate the diff matrix.
sep "═══ §5.8+§7 POST-STATE PAIR EXTRACTION & DIFF MATRIX ═══"
if [ -n "$NEW_SESSION" ] && [ -f "$NEW_SESSION/eseecloud-connections.log" ]; then
  eval_session "$NEW_SESSION"
  # §7 row D (0 pairs) applies to BOTH plans: power-cycle at trigger, re-run
  # once; if still 0, restore state and re-plan.
  if python3 -c "import json,sys; print('D —' in json.load(open('$RUN_DIR/verdict.json')).get('verdict',''))" 2>/dev/null | grep -q True; then
    log "  ⚠ §7 row D: 0 new pairs — power-cycle $CAM_IP at the trigger line and re-run the MITM once;"
    log "    if still 0, restore state and re-plan (document; do not burn repeated windows)."
  fi
  if [ "$PLAN" = "B" ]; then
    log "  ⚠ Plan B executed: §7 verdict in $RUN_DIR/verdict.json; if 0 pairs, follow §6 FATAL ASSUMPTION (STOP, re-plan)."
  fi
else
  die "no session log produced by the MITM run — check captures/ manually"
fi

sep "═══ DONE ═══"
log "  final CAM_IP: $CAM_IP (updated by §5.3b re-provision if the camera vanished)"
log "  gate observed: $GATE · plan executed: $PLAN · eseeid: ${POST_ESEEID:-unread}"
# §5.1b attribution: the pre-reset LITE baseline vs the post-MITM verdict. If
# the camera was ALREADY at +0x1e before the reset, a post-reset LITE_ADOPTED
# is pre-existing, not a reset-run outcome.
BASE_LITE=$(python3 -c "import json; print(json.load(open('$RUN_DIR/lite-baseline.json'))['verdict'])" 2>/dev/null || echo unrecorded)
POST_LITE=$(python3 -c "import json; print(json.load(open('$RUN_DIR/lite-verdict.json'))['verdict'])" 2>/dev/null || echo unread)
log "  LITE baseline (pre-reset): $BASE_LITE · LITE-grant verdict (post): $POST_LITE"
case "$BASE_LITE:$POST_LITE" in
  LITE_BASELINE_ADOPTED:LITE_ADOPTED)
    log "  ⚠ ATTRIBUTION: LITE_ADOPTED is PRE-EXISTING (baseline already +0x1e) — NOT a post-reset outcome." ;;
  LITE_BASELINE_CLEAN:LITE_ADOPTED)
    log "  ✅ ATTRIBUTION: LITE_ADOPTED IS attributable to the post-reset run (baseline clean +0x14)." ;;
  LITE_BASELINE_NO_TRAFFIC:LITE_ADOPTED)
    log "  ⚠ ATTRIBUTION: baseline saw no LITE traffic — post-reset LITE_ADOPTED attribution is UNPROVEN." ;;
  *)
    log "  attribution: baseline=$BASE_LITE post=$POST_LITE — no flagged conflict (baseline OTHER/UNKNOWN post-ADOPTED is unproven, not pre-existing)." ;;
esac
# DONE deliverable assertion: the post-reset keyprobe line must exist in the
# campaign ledger, written this run with a real verdict — else fail loudly.
assert_post_reset_ledger_line
log "  archive results: $RUN_DIR (run.log, verdict.json, lite-verdict.json, lite-baseline.json) · session: $NEW_SESSION"
log "  next (§8): append pairs to the report as §3b; gate/plan state is the decision-relevant datum for .29."
