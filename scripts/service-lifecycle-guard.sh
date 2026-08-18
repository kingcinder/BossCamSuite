#!/usr/bin/env bash
# ── service-lifecycle-guard.sh — BossCamSuite service-lifecycle watchdog ────
#
# Runs on a systemd timer and keeps the BossCamService lifecycle honest:
#
#   1. STALE MANUAL INSTANCE → SYSTEMD HANDOFF — when $API_PORT is held by a
#      BossCam.Service process that is NOT the systemd unit's MainPID (a
#      manually-spawned instance, typically from a GUI auto-start or a
#      pre-systemd launch), the guard stops it gracefully (SIGTERM → SIGKILL)
#      and lets systemd start the unit. systemd becomes the single owner of
#      the port, so the unit stops crash-looping on the bind.
#
#   2. CRASH-LOOPED / FAILED UNIT — when nothing is listening on $API_PORT but
#      the unit has no MainPID (activating/failed/inactive), the guard
#      recovers it: stop → reset-failed → start, then waits for /api/health.
#      Recovery is rate-limited by RECOVER_COOLDOWN so a unit that genuinely
#      cannot start is not hammered every tick.
#
#   3. ORPHAN MJPEG RECORDER REAP — snapshot-pipeline bash scripts
#      (/dev/shm/bosscam-rec-*.sh: "while curl | ffmpeg") whose parent died
#      (PPID 1) are killed together with their ffmpeg/curl children and their
#      script files removed. Each orphan polls a camera every 0.5s and
#      transcodes at 2 fps forever — a fleet of them starves the live view,
#      hammers the cameras, and writes junk .ts segments to disk.
#
# Everything logs to <repo>/local-camera-recovery/service-lifecycle-guard.log;
# a single-instance flock prevents overlapping ticks.
#
# Run: driven by scripts/install-service-lifecycle-guard.sh (systemd timer,
# every 2 minutes). Manual one-shot:  ./scripts/service-lifecycle-guard.sh
# Preflight (no side effects):        DRY_RUN=1 ./scripts/service-lifecycle-guard.sh
#
# Env (all optional):
#   API=...            BossCam API base URL   (default http://127.0.0.1:5317)
#   API_PORT=...       service port           (default 5317)
#   SERVICE=...        systemd unit name      (default bosscam)
#   TERM_GRACE=...     graceful-stop grace before SIGKILL (default 15s)
#   HEALTH_TIMEOUT=... seconds to wait for /api/health after a handoff (default 45)
#   RECOVER_COOLDOWN=.. seconds between unit-recovery attempts (default 120)
#   LOG=...            rolling tick log       (default <repo>/local-camera-recovery/service-lifecycle-guard.log)
#   STATE=...          cooldown state file    (default <repo>/local-camera-recovery/service-lifecycle-guard.state)
#   LOCK=...           single-instance flock  (default <repo>/local-camera-recovery/service-lifecycle-guard.lock)
#   DRY_RUN=1          print the plan; change nothing
#
# The pure parsing/decision helpers read their input on stdin so the unit
# fixture (scripts/test-service-lifecycle-guard-unit.sh) can feed canned data;
# the main flow runs the real commands and pipes them into those helpers.
#
# The unit drives systemctl and kills processes, so the systemd timer should
# run as root (the installer defaults WATCHDOG_USER=root) — a non-root user
# needs a polkit rule to start/stop a system unit.
#
# NOTE: this guard fixes lifecycle OWNERSHIP (who runs the service, who owns
# the port) — it does not deploy code. Refreshing the installed binaries is
# the update shortcut's job (scripts/update-bosscam.sh); run that once after
# installing this guard so the handed-off unit runs the current build.

set -u
set -o pipefail

API="${API:-http://127.0.0.1:5317}"
API_PORT="${API_PORT:-5317}"
SERVICE="${SERVICE:-bosscam}"
TERM_GRACE="${TERM_GRACE:-15}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-45}"
RECOVER_COOLDOWN="${RECOVER_COOLDOWN:-120}"
DRY_RUN="${DRY_RUN:-0}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
mkdir -p "$ROOT/local-camera-recovery" 2>/dev/null || true
LOG="${LOG:-$ROOT/local-camera-recovery/service-lifecycle-guard.log}"
STATE="${STATE:-$ROOT/local-camera-recovery/service-lifecycle-guard.state}"
# Lock lives beside LOG/STATE in the repo dir (NOT /tmp): the root timer and
# manual per-user runs both need to open it, and a /tmp lock's ownership flips
# between the two (observed live: root EACCES on a cody-created /tmp lock, so
# the tick bailed every run). main() chmods it 666 after opening so whichever
# user creates it first never blocks the other.
LOCK="${LOCK:-$ROOT/local-camera-recovery/service-lifecycle-guard.lock}"

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }
log() { echo "$(ts) $1" >> "$LOG" 2>/dev/null || true; }

# ── pure parsers (stdin-fed; unit-tested) ───────────────────────────────────

# ss_listener_pid <port> — read `ss -ltnp` output on stdin; print the pid of
# the process listening on <port> ("" when none / not visible to this user).
# Matches the LOCAL-ADDRESS field ($4) end-anchored on ":<port>" so a longer
# port can never false-match (e.g. :22 vs :2200).
ss_listener_pid() {
  local port="$1"
  awk -v port=":$port" '
    $1 == "LISTEN" && $4 ~ (port "$") && /pid=[0-9]+/ {
      if (match($0, /pid=[0-9]+/)) { print substr($0, RSTART + 4, RLENGTH - 4); exit }
    }
  '
}

# handoff_decision <port_owner> <unit_main_pid> — print ok|stale|free:
#   ok     the systemd unit owns the port
#   stale  a non-systemd process owns the port (handoff candidate)
#   free   nobody owns the port
handoff_decision() {
  if [ -n "$1" ]; then
    if [ "$1" = "$2" ] && [ -n "$2" ] && [ "$2" != "0" ]; then
      echo ok
    else
      echo stale
    fi
  else
    echo free
  fi
}

# orphan_recorders — read `ps -eo pid=,ppid=,cmd=` on stdin; print
# "<pid>\t<script-path>" for every bosscam-rec-*.sh bash whose parent is dead
# (PPID 1). Recorder children (curl/ffmpeg) and live recorders (non-1 PPID)
# are not matched.
orphan_recorders() {
  awk '
    $2 == 1 && /bosscam-rec-/ && /bash/ {
      script = ""
      for (i = 3; i <= NF; i++) if ($i ~ /bosscam-rec-/) script = $i
      if (script != "") print $1 "\t" script
    }
  '
}

# descendant_pids <root> — read `ps -eo pid=,ppid=` on stdin; print every pid
# that transitively descends from <root>.
descendant_pids() {
  local root="$1"
  awk -v root="$root" '
    function walk(p,  i) {
      for (i = 1; i <= n; i++) {
        if (pp[i] == p && !seen[pid[i]]) { seen[pid[i]] = 1; print pid[i]; walk(pid[i]) }
      }
    }
    { pid[++n] = $1; pp[n] = $2 }
    END { walk(root) }
  '
}

# cmdline_is_service <cmdline> — true when the command line is BossCam.Service.
cmdline_is_service() {
  case "$1" in
    *BossCam.Service.dll*) return 0 ;;
    *) return 1 ;;
  esac
}

# ── system helpers ──────────────────────────────────────────────────────────

systemd_available() { command -v systemctl >/dev/null 2>&1; }

# unit_prop <Property> — print a systemd property for $SERVICE ("unknown" on
# failure, so a permissions problem degrades to a logged skip, not a crash).
unit_prop() {
  systemctl show -p "$1" --value "$SERVICE" 2>/dev/null || echo unknown
}

pid_is_service() { cmdline_is_service "$(ps -p "$1" -o cmd= 2>/dev/null || true)"; }

# wait_for_health — poll /api/health until HEALTH_TIMEOUT expires.
wait_for_health() {
  local i
  for i in $(seq 1 "$HEALTH_TIMEOUT"); do
    if curl -fsS -m 3 "$API/api/health" >/dev/null 2>&1; then
      log "health OK on $API after ${i}s"
      return 0
    fi
    sleep 1
  done
  log "health NOT reached on $API within ${HEALTH_TIMEOUT}s"
  return 1
}

# stop_pid <pid> <reason> — SIGTERM, wait TERM_GRACE, then SIGKILL. Waits for
# the pid to actually exit so the port frees before the unit starts.
stop_pid() {
  local pid="$1" why="$2" i
  log "stopping pid $pid ($why)"
  kill -TERM "$pid" 2>/dev/null || true
  for i in $(seq 1 "$TERM_GRACE"); do
    kill -0 "$pid" 2>/dev/null || { log "pid $pid exited after ${i}s"; return 0; }
    sleep 1
  done
  log "pid $pid still alive after ${TERM_GRACE}s — SIGKILL"
  kill -KILL "$pid" 2>/dev/null || true
  for i in $(seq 1 5); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
}

# start_unit — break any restart loop, reset-failed, start, wait for health.
start_unit() {
  if ! systemd_available; then
    log "systemctl unavailable — cannot manage $SERVICE"
    return 1
  fi
  if [ "$DRY_RUN" = "1" ]; then
    log "DRY: would systemctl stop/reset-failed/start $SERVICE, then wait for health"
    return 0
  fi
  systemctl stop "$SERVICE" 2>/dev/null || true
  systemctl reset-failed "$SERVICE" 2>/dev/null || true
  systemctl start "$SERVICE" 2>/dev/null || true
  wait_for_health
}

cooldown_ok() {
  local last now
  last=$(grep '^last_recovery=' "$STATE" 2>/dev/null | tail -1 | cut -d= -f2-)
  last=${last:-0}
  now=$(date -u +%s)
  [ $(( now - last )) -ge "$RECOVER_COOLDOWN" ]
}

mark_recovery() {
  echo "last_recovery=$(date -u +%s)" > "${STATE}.tmp" 2>/dev/null && mv "${STATE}.tmp" "$STATE" 2>/dev/null || true
}

# reap_orphans — kill orphaned MJPEG recorders (PPID 1) and their trees.
reap_orphans() {
  local orphans pid script children c n=0
  orphans=$(ps -eo pid=,ppid=,cmd= 2>/dev/null | orphan_recorders)
  if [ -z "$orphans" ]; then
    log "no orphaned MJPEG recorder processes (PPID 1)"
    return 0
  fi
  while IFS=$'\t' read -r pid script; do
    [ -n "$pid" ] && [ -n "$script" ] || continue
    log "ORPHAN REAP: pid $pid ($script) — parent died"
    if [ "$DRY_RUN" = "1" ]; then
      log "DRY: would TERM/KILL pid $pid + children, rm $script"
      continue
    fi
    children=$(ps -eo pid=,ppid= 2>/dev/null | descendant_pids "$pid")
    for c in $children; do kill -TERM "$c" 2>/dev/null || true; done
    sleep 1
    for c in $children; do kill -0 "$c" 2>/dev/null && kill -KILL "$c" 2>/dev/null || true; done
    kill -TERM "$pid" 2>/dev/null || true
    sleep 1
    kill -0 "$pid" 2>/dev/null && kill -KILL "$pid" 2>/dev/null || true
    rm -f "$script" 2>/dev/null || true
    log "reaped pid $pid (children: $(echo "$children" | grep -c . 2>/dev/null || true)); removed $script"
    n=$(( n + 1 ))
  done <<< "$orphans"
  log "reaped $n orphaned recorder(s)"
}

# ── main ────────────────────────────────────────────────────────────────────
main() {
  exec 9>"$LOCK"
  chmod 666 "$LOCK" 2>/dev/null || true
  if ! flock -n 9; then
    echo "$(ts) another lifecycle-guard tick still active — skipping" >> "$LOG" 2>/dev/null || true
    return 0
  fi

  local owner unitpid active
  owner=$(ss -ltnp 2>/dev/null | ss_listener_pid "$API_PORT" | head -1)
  unitpid=$(systemd_available && unit_prop MainPID || echo 0)
  unitpid=${unitpid:-0}
  case "$unitpid" in
    ''|unknown) unitpid=0 ;;
  esac

  case "$(handoff_decision "$owner" "$unitpid")" in
    ok)
      log "service healthy: systemd $SERVICE (pid $unitpid) owns port $API_PORT"
      ;;
    stale)
      if pid_is_service "$owner"; then
        log "STALE MANUAL INSTANCE: pid $owner (BossCam.Service) holds port $API_PORT; systemd main pid is $unitpid"
        if [ "$DRY_RUN" = "1" ]; then
          log "DRY: would stop pid $owner (grace ${TERM_GRACE}s) and hand off to $SERVICE"
        else
          stop_pid "$owner" "stale manual BossCam.Service"
          start_unit
        fi
      else
        log "port $API_PORT held by foreign pid $owner (not BossCam.Service) — leaving alone"
      fi
      ;;
    free)
      active=$(unit_prop ActiveState)
      log "port $API_PORT free; unit $SERVICE ActiveState=$active MainPID=$unitpid"
      if [ "$active" != "active" ]; then
        if cooldown_ok; then
          log "unit $SERVICE is $active with nothing listening — recovering"
          mark_recovery
          start_unit
        else
          log "unit recovery in cooldown (${RECOVER_COOLDOWN}s) — skipping"
        fi
      fi
      ;;
  esac

  reap_orphans
  return 0
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main
fi
