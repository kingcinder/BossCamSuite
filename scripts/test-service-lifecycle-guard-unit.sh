#!/usr/bin/env bash
# ── test-service-lifecycle-guard-unit.sh — unit fixture for the lifecycle guard ──
#
# Sources scripts/service-lifecycle-guard.sh (its main is gated on being
# executed directly, so sourcing only defines functions) and exercises the
# PURE parsers/decisions with canned input — no live processes, no systemctl,
# no side effects:
#
#   1. ss_listener_pid — extracts the owning pid from `ss -ltnp` output,
#      matching the right line, surviving absent ports and empty input.
#   2. handoff_decision — ok|stale|free matrix for the port-owner vs systemd
#      MainPID comparison.
#   3. orphan_recorders — picks ONLY bosscam-rec-*.sh bash processes whose
#      PPID is 1 (dead parent), ignoring recorder children, live recorders,
#      and unrelated PIDs.
#   4. descendant_pids — transitive closure of a process tree (the ffmpeg/curl
#      children that outlive the recorder bash must all be found).
#   5. cmdline_is_service — BossCam.Service identification.
#   6. sourcing has no main-flow side effects (no log file created).
#
# Usage:   ./scripts/test-service-lifecycle-guard-unit.sh
# Exit:    0 all assertions passed; 1 any assertion failed; 2 guard missing

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
GUARD="${GUARD:-$ROOT/scripts/service-lifecycle-guard.sh}"
[ -f "$GUARD" ] || { echo "!! guard script not found (GUARD=$GUARD)" >&2; exit 2; }

T="$(mktemp -d /tmp/lifecycle-guard-unit.XXXXXX)"
trap 'rm -rf "$T"' EXIT

# Source with isolated LOG/STATE so sourcing is side-effect-free.
LOG="$T/guard.log" STATE="$T/guard.state" source "$GUARD"

PASS=0
FAIL=0
ok()  { printf 'PASS: %s\n' "$*"; PASS=$((PASS + 1)); }
bad() { printf 'FAIL: %s\n' "$*"; FAIL=$((FAIL + 1)); }

expect_eq() { # expect_eq <desc> <expected> <actual>
  if [ "$2" = "$3" ]; then
    ok "$1"
  else
    bad "$1 — expected [$2] got [$3]"
  fi
}

echo "=== unit: service-lifecycle-guard.sh parsers (canned input) ==="

# ── 1) ss_listener_pid ──────────────────────────────────────────────────────
echo "=== 1) ss_listener_pid — port-owner extraction from ss -ltnp ==="
# Longer-port lookalikes (:53170, :2200) are placed BEFORE the real listeners
# (:5317, :22). The parser exits on its first match, so an unanchored regex
# would false-match the lookalike and return the WRONG pid — only the
# end-anchored matcher skips them and lands on the true listener.
SS_OUT='LISTEN 0      64         0.0.0.0:53170      0.0.0.0:*    users:(("webd",pid=6666,fd=5))
LISTEN 0      64         0.0.0.0:2200       0.0.0.0:*    users:(("tcpwrap",pid=7777,fd=4))
LISTEN 0      512        127.0.0.1:5317       0.0.0.0:*    users:(("dotnet",pid=4120007,fd=250))
LISTEN 0      128        0.0.0.0:22         0.0.0.0:*    users:(("sshd",pid=999,fd=3))
LISTEN 0      511       [::]:8080          [::]:*    users:(("nginx",pid=5555,fd=9))'
expect_eq "5317 → dotnet pid (skips :53170)" "4120007" "$(printf '%s\n' "$SS_OUT" | ss_listener_pid 5317)"
expect_eq "22 → sshd pid (skips :2200)" "999" "$(printf '%s\n' "$SS_OUT" | ss_listener_pid 22)"
expect_eq "8080 → nginx pid (ipv6)" "5555" "$(printf '%s\n' "$SS_OUT" | ss_listener_pid 8080)"
expect_eq "absent port → empty" "" "$(printf '%s\n' "$SS_OUT" | ss_listener_pid 8443)"
expect_eq "empty input → empty" "" "$(printf '' | ss_listener_pid 5317)"

# ── 2) handoff_decision ─────────────────────────────────────────────────────
echo "=== 2) handoff_decision — port owner vs systemd MainPID matrix ==="
expect_eq "non-systemd owner → stale"  "stale" "$(handoff_decision 4120007 0)"
expect_eq "systemd owns port → ok"     "ok"    "$(handoff_decision 4120007 4120007)"
expect_eq "no owner → free"            "free"  "$(handoff_decision '' 0)"
expect_eq "no owner, unit pid set → free" "free" "$(handoff_decision '' 12345)"

# ── 3) orphan_recorders ─────────────────────────────────────────────────────
echo "=== 3) orphan_recorders — only PPID-1 bosscam-rec bash processes ==="
PS_SAMPLE=' 116697       1    14:23:28 /bin/bash /dev/shm/bosscam-rec-762cf76317f64adab2cecff1ea22488a.sh
 116699  116697    14:23:28 /usr/bin/ffmpeg -hide_banner -f image2pipe -framerate 2 -c:v mjpeg -i -
 126790       1    14:19:30 /bin/bash /dev/shm/bosscam-rec-e560f0ef63084267a8c90e0571601fde.sh
  9999  4120007    0:00 /bin/bash /dev/shm/bosscam-rec-live-owner.sh
  8888       1    0:00 /usr/bin/python3 /usr/local/bin/unrelated
 204077       1    13:48:57 /bin/bash /dev/shm/bosscam-rec-932090d641d640b4b9103bb0edec0f0e.sh'
EXPECTED='116697	/dev/shm/bosscam-rec-762cf76317f64adab2cecff1ea22488a.sh
126790	/dev/shm/bosscam-rec-e560f0ef63084267a8c90e0571601fde.sh
204077	/dev/shm/bosscam-rec-932090d641d640b4b9103bb0edec0f0e.sh'
GOT="$(printf '%s\n' "$PS_SAMPLE" | orphan_recorders)"
if [ "$GOT" = "$EXPECTED" ]; then
  ok "orphan set exact (3 orphans, children/live/foreign excluded)"
else
  bad "orphan set mismatch"
  diff -u <(printf '%s\n' "$EXPECTED") <(printf '%s\n' "$GOT") >&2 || true
fi
PS_CLEAN=' 116699  116697    14:23:28 /usr/bin/ffmpeg -hide_banner -f image2pipe -framerate 2 -c:v mjpeg -i -
  9999  4120007    0:00 /bin/bash /dev/shm/bosscam-rec-live-owner.sh
  8888       1    0:00 /usr/bin/python3 /usr/local/bin/unrelated
  7777       1    0:00 /usr/sbin/sshd -D'
expect_eq "no orphans → empty" "" "$(printf '%s\n' "$PS_CLEAN" | orphan_recorders)"

# ── 4) descendant_pids ──────────────────────────────────────────────────────
echo "=== 4) descendant_pids — transitive process-tree closure ==="
PS_EDGES='1 0
100 1
101 100
102 100
103 101
200 1
300 200'
expect_eq "descendants of 100" "101 102 103 " "$(printf '%s\n' "$PS_EDGES" | descendant_pids 100 | sort -n | tr '\n' ' ')"
expect_eq "descendants of 101" "103 " "$(printf '%s\n' "$PS_EDGES" | descendant_pids 101 | sort -n | tr '\n' ' ')"
expect_eq "descendants of 200" "300 " "$(printf '%s\n' "$PS_EDGES" | descendant_pids 200 | sort -n | tr '\n' ' ')"
expect_eq "leaf has no descendants" "" "$(printf '%s\n' "$PS_EDGES" | descendant_pids 103 | sort -n | tr '\n' ' ')"
expect_eq "unknown root → empty" "" "$(printf '%s\n' "$PS_EDGES" | descendant_pids 9999 | sort -n | tr '\n' ' ')"

# ── 5) cmdline_is_service ───────────────────────────────────────────────────
echo "=== 5) cmdline_is_service — BossCam.Service identification ==="
if cmdline_is_service '/home/cody/.dotnet/dotnet /opt/bosscam/BossCam.Service.dll'; then
  ok "installed-service cmdline recognized"
else
  bad "installed-service cmdline NOT recognized"
fi
if cmdline_is_service 'dotnet /opt/bosscam/BossCam.Service.dll --urls=http://0.0.0.0:5317'; then
  ok "service cmdline with args recognized"
else
  bad "service cmdline with args NOT recognized"
fi
if ! cmdline_is_service '/usr/bin/sshd -D'; then
  ok "sshd cmdline rejected"
else
  bad "sshd cmdline wrongly accepted"
fi
if ! cmdline_is_service '/usr/bin/ffmpeg -f image2pipe -i -'; then
  ok "ffmpeg cmdline rejected"
else
  bad "ffmpeg cmdline wrongly accepted"
fi

# ── 6) sourcing side effects ────────────────────────────────────────────────
echo "=== 6) sourcing has no main-flow side effects ==="
[ ! -e "$T/guard.log" ] \
  && ok "sourcing did not write the tick log (main not executed)" \
  || bad "sourcing wrote $T/guard.log — main ran on source?"
[ ! -e "$T/guard.state" ] \
  && ok "sourcing did not write the state file" \
  || bad "sourcing wrote $T/guard.state"
type ss_listener_pid >/dev/null 2>&1 \
  && ok "parsers are defined after sourcing" \
  || bad "parsers missing after sourcing"

echo ""
echo "=== results: $PASS passed, $FAIL failed ==="
if [ "$FAIL" -eq 0 ]; then
  echo "LIFECYCLE-GUARD UNIT FIXTURE: ALL GREEN"
else
  echo "LIFECYCLE-GUARD UNIT FIXTURE: FAILURES" >&2
fi
# Clamp to 1: exit 2 is reserved for 'guard not testable'.
exit $(( FAIL > 0 ? 1 : FAIL ))
