#!/usr/bin/env bash
# ── run-prune-mark-tests.sh — combined prune-mark test runner ──
#
# Runs the flips_log_prune_mark unit fixture (mode/ownership preservation,
# idempotency, backfill-style per-path dedup) plus the full KEEP_DAYS prune e2e
# fixture, and reports a single combined PASS/FAIL summary. Both suites extract
# the pruner from the SAME heartbeat, and HB=... is passed through to each, so
# pointing this at a candidate gate-flip-heartbeat.sh runs the whole suite as a
# pre-flight gate before deployment.
#
# Usage:   ./scripts/run-prune-mark-tests.sh
#          HB=/path/to/candidate.sh ./scripts/run-prune-mark-tests.sh
# Exit:    0 all suites green; 1 any assertion failed; 2 a suite was not runnable
#          (missing fixture or structurally-unparseable heartbeat)

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
HB="${HB:-$ROOT/scripts/gate-flip-heartbeat.sh}"
export HB   # pass through to both sub-suites (they honor the same override)
[ -f "$HB" ] || { echo "!! heartbeat script not found (HB=$HB) — set HB=/path/to/gate-flip-heartbeat.sh" >&2; exit 2; }

UNIT="$HERE/test-prune-mark-unit.sh"
E2E="$HERE/test-prune-mark-e2e.sh"
for f in "$UNIT" "$E2E"; do
  [ -f "$f" ] || { echo "!! fixture not found: $f" >&2; exit 2; }
done

TOTAL_PASS=0
TOTAL_FAIL=0
SUITE_RC=0

run_suite() {
  # $1 = label; $2 = script. Runs it, echoes its output, aggregates counts.
  # rc 2 (suite not runnable) takes precedence and propagates to the runner.
  local label="$1" script="$2" out rc p f
  echo ""
  echo "=== suite: $label ($script) ==="
  # Invoke via bash so a missing exec bit can't masquerade as a suite failure.
  out="$(bash "$script" 2>&1)"
  rc=$?
  printf '%s\n' "$out"
  p=$(printf '%s\n' "$out" | grep -c '^PASS:' || true); p=${p:-0}
  f=$(printf '%s\n' "$out" | grep -c '^FAIL:' || true); f=${f:-0}
  echo "  -> $label: $p passed, $f failed (rc=$rc)"
  TOTAL_PASS=$((TOTAL_PASS + p))
  TOTAL_FAIL=$((TOTAL_FAIL + f))
  if [ "$rc" -eq 2 ]; then
    SUITE_RC=2
  elif [ "$rc" -ne 0 ] && [ "$SUITE_RC" -ne 2 ]; then
    SUITE_RC=1
  fi
}

echo "=== prune-mark suite: validating heartbeat $HB ==="
run_suite "flips_log_prune_mark unit fixture" "$UNIT"
run_suite "KEEP_DAYS prune e2e fixture" "$E2E"

echo ""
echo "=== combined: $TOTAL_PASS passed, $TOTAL_FAIL failed ==="
# Key the verdict off SUITE_RC (the exit code), not TOTAL_FAIL: a suite that
# exits nonzero without any ^FAIL: line (e.g. a runtime crash) must never be
# reported as green just because the grep counts are zero.
if [ "$SUITE_RC" -eq 2 ]; then
  echo "PRUNE-MARK TEST SUITE: NOT RUNNABLE — a suite failed to start (rc 2)" >&2
elif [ "$SUITE_RC" -eq 0 ]; then
  echo "PRUNE-MARK TEST SUITE: ALL GREEN — $TOTAL_PASS assertions across unit + e2e (heartbeat: $HB)"
else
  echo "PRUNE-MARK TEST SUITE: FAILURES — $TOTAL_FAIL assertion(s) failed (heartbeat: $HB)" >&2
fi
exit "$SUITE_RC"
