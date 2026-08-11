#!/usr/bin/env bash
# ── test-prune-mark-e2e.sh — E2E fixture for the KEEP_DAYS prune-mark step ──
#
# Validates the heartbeat's prune path against REAL pruning rather than a
# synthetic missing pcap: it seeds a fake captures/ tree with actual
# eseecloud-mitm-<ts>/capture.pcap session dirs and gate-flip-<ts> run dirs,
# ages the OLD ones, then runs the heartbeat's OWN prune block — the live
# `ls -dt ... gate-flip-20* | tail -n +KEEP_RUNS` and
# `find ... eseecloud-mitm-* -mtime +KEEP_DAYS -exec rm -rf` commands plus
# flips_log_prune_mark() — extracted from scripts/gate-flip-heartbeat.sh at
# run time, with KEEP_DAYS=0 / KEEP_RUNS=0 so the commands REALLY delete the
# aged dirs. It then asserts the exact nonce-flips.log rows get the (pruned)
# marker: rows citing pruned pcaps marked, rows citing surviving pcaps
# untouched, already-marked rows preserved, MID_TICK chains intact. A mid-test
# injection then deletes a surviving pcap AFTER the marker pass; the next run
# must mark its rows (and unmark when the pcap is recreated), proving the
# reconcile tracks live disk state rather than being one-shot. A negative
# control — an old-vintage row citing a pcap whose directory still exists — must
# stay unmarked even after a full prune run, locking in that the marker keys on
# actual file existence, not row age or content — and a restored pcap loses the
# marker on the next run.
#
# Because the prune commands and pruner are extracted from the live heartbeat
# source, this fixture always exercises the real implementation: if the
# heartbeat's prune logic changes, this test runs the new commands.
#
# Usage:   ./scripts/test-prune-mark-e2e.sh               # validate the repo's heartbeat
#          HB=/path/to/candidate.sh ./scripts/test-prune-mark-e2e.sh  # pre-flight ANY variant
#
# The HB env override turns this into a pre-flight gate: point it at a candidate
# gate-flip-heartbeat.sh (e.g. a branch with changed prune logic) and the full
# fixture runs against the candidate's OWN extracted commands. A candidate that
# diverges structurally (missing extraction anchors) fails fast with exit 2; a
# candidate whose prune logic is broken fails the assertions with exit 1.
#
# Exit:    0 all assertions passed; 1 any assertion failed (tested and rejected);
#          2 heartbeat missing/unparseable — a clean contract for CI/gate automation.

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
# Pre-flight gate: validate any candidate gate-flip-heartbeat.sh variant via the
# HB env override (default: the repo's current heartbeat). A branch with changed
# prune logic can run the full fixture before deployment.
HB="${HB:-$ROOT/scripts/gate-flip-heartbeat.sh}"
[ -f "$HB" ] || { echo "!! heartbeat script not found (HB=$HB) — set HB=/path/to/gate-flip-heartbeat.sh" >&2; exit 2; }

T="$(mktemp -d /tmp/prune-mark-e2e.XXXXXX)"
trap 'rm -rf "$T"' EXIT
CAP="$T/captures"
mkdir -p "$CAP"
FLIPS="$CAP/nonce-flips.log"

OLD="eseecloud-mitm-20260701T000000Z"   # will be pruned (aged > KEEP_DAYS=0)
NEW="eseecloud-mitm-20260809T120147Z"   # must survive (current mtime)
OLDGF="gate-flip-20260701T000000Z"      # will be pruned (tail -n +1)
NEWGF="gate-flip-20260809T120147Z"      # must survive (newest)

# ── seed the fake fleet ──────────────────────────────────────────────────────
for d in "$OLD" "$NEW"; do
  mkdir -p "$CAP/$d"
  : > "$CAP/$d/capture.pcap"
done
mkdir -p "$CAP/$OLDGF" "$CAP/$NEWGF"
: > "$CAP/$OLDGF/experiment.log"
: > "$CAP/$NEWGF/experiment.log"
# Age ONLY the old dirs so find -mtime +0 / ls -dt actually prune them.
touch -d '2 days ago' "$CAP/$OLD" "$CAP/$OLDGF"

# Seed the flip history: rows citing both pcaps, one already marked, one MID_TICK,
# and an old-vintage negative-control row (2026-08-08T18:40:00Z, .29, NONCE_FLIP —
# the same era/camera/kind as the marked rows) citing the SURVIVING pcap. If the
# marker were driven by row age or content rather than file existence, this row
# would get marked too.
cat > "$FLIPS" <<EOF
2026-08-09T12:01:47Z flip cam=10.0.0.169 old=2ce84978 new=8671353c kind=NONCE_FLIP pcap=$CAP/$NEW/capture.pcap
2026-08-08T18:33:13Z flip cam=10.0.0.29 old=06b71d86 new=6f5e0c61 kind=NONCE_FLIP pcap=$CAP/$OLD/capture.pcap
2026-08-08T19:12:25Z flip cam=10.0.0.29 old=6f5e0c61 new=b1fe37ab kind=NONCE_FLIP pcap=$CAP/$OLD/capture.pcap(pruned)
2026-08-08T18:40:00Z flip cam=10.0.0.29 old=6f5e0c61 new=b1fe37ab kind=NONCE_FLIP pcap=$CAP/$NEW/capture.pcap
2026-08-10T13:00:00Z flip cam=10.0.0.227 old=38dfb8c8 new=9c4e6f8a kind=MID_TICK_FLIP pcap=$CAP/$OLD/capture.pcap chain=38dfb8c8,9c4e6f8a
EOF

# ── extract the heartbeat's LIVE prune path into a runnable block ────────────
# The pruner function: definition start -> its closing brace at column 0.
PRUNER="$(sed -n '/^flips_log_prune_mark()/,/^}/p' "$HB")"
[ -n "$PRUNER" ] || { echo "!! could not extract flips_log_prune_mark() from $HB — candidate diverged structurally?" >&2; exit 2; }
# The prune block: from the first column-0 `ls -dt "$ROOT/captures"/gate-flip-20*`
# (the prune section's command — run_camera's copy is indented, so ^ls -dt
# cannot match it) through the exact `flips_log_prune_mark` call line (the
# function def line has `() {` so ^...$ cannot match it; the trailing `exit 0`
# of the heartbeat is excluded so the fixture keeps running).
PRUNE_BLOCK="$(sed -n '/^ls -dt .*gate-flip-20/,/^flips_log_prune_mark$/p' "$HB")"
[ -n "$PRUNE_BLOCK" ] || { echo "!! could not extract the prune block from $HB — candidate diverged structurally?" >&2; exit 2; }

cat > "$T/run.sh" <<EOF
set -u
set -o pipefail
$PRUNER
$PRUNE_BLOCK
EOF

run_prune() {
  # Run the heartbeat's real prune commands against the fake captures tree.
  # KEEP_RUNS=1 keeps the NEWEST gate-flip dir (tail -n +2 drops only the
  # oldest) — KEEP_RUNS=0 would make tail -n +1 pass BOTH dirs to xargs and
  # prune the "newest" too, breaking the survive assertion.
  ROOT="$T" FLIPS_LOG="$FLIPS" KEEP_RUNS=1 KEEP_DAYS=0 bash "$T/run.sh"
}

PASS=0
FAIL=0
ok()  { printf 'PASS: %s\n' "$*"; PASS=$((PASS + 1)); }
bad() { printf 'FAIL: %s\n' "$*"; FAIL=$((FAIL + 1)); }

# ── run + assert ─────────────────────────────────────────────────────────────
echo "=== pre-flight gate: validating $HB ==="
echo "heartbeat md5: $(md5sum "$HB" | cut -d' ' -f1)"
echo ""
echo "=== seed state ==="
echo "old session dir: $([ -d "$CAP/$OLD" ] && echo present || echo MISSING)"
echo "new session dir: $([ -d "$CAP/$NEW" ] && echo present || echo MISSING)"
echo ""
echo "=== run 1 (KEEP_DAYS=0 KEEP_RUNS=1: real pruning of aged dirs) ==="
run_prune

# 1. Directory pruning actually happened
[ -d "$CAP/$OLD" ]  && bad "old session dir was NOT pruned by find -mtime +0" \
                    || ok "old session dir pruned by find -mtime +0"
[ -d "$CAP/$NEW" ]  && ok "new session dir survived find -mtime +0" \
                    || bad "new session dir was wrongly pruned"
[ -d "$CAP/$OLDGF" ] && bad "old gate-flip dir was NOT pruned by ls -dt | tail -n +1" \
                     || ok "old gate-flip dir pruned by ls -dt | tail -n +1"
[ -d "$CAP/$NEWGF" ] && ok "new gate-flip dir survived (newest kept)" \
                     || bad "new gate-flip dir was wrongly pruned"

# 2. Exact marker placement in nonce-flips.log
marked_old=$(grep -c "pcap=$CAP/$OLD/capture.pcap(pruned)" "$FLIPS" 2>/dev/null || true)
marked_old=${marked_old:-0}
marked_new=$(grep -c "pcap=$CAP/$NEW/capture.pcap(pruned)" "$FLIPS" 2>/dev/null || true)
marked_new=${marked_new:-0}
[ "$marked_old" -eq 3 ] \
  && ok "exactly 3 rows citing the pruned pcap are marked (plain + already-marked + MID_TICK)" \
  || bad "expected 3 marked rows for pruned pcap, got $marked_old"
[ "$marked_new" -eq 0 ] \
  && ok "no row citing the surviving pcap was marked" \
  || bad "row citing surviving pcap was marked ($marked_new)"
grep -q "pcap=$CAP/$OLD/capture.pcap(pruned) chain=38dfb8c8,9c4e6f8a" "$FLIPS" \
  && ok "MID_TICK row marked with chain= preserved verbatim" \
  || bad "MID_TICK row chain= lost or marker misplaced"
grep -q "pcap=$CAP/$NEW/capture.pcap$" "$FLIPS" \
  && ok "row citing surviving pcap still ends unmarked" \
  || bad "row citing surviving pcap was altered"
# Snapshot the post-run-1 log so the injection phase can PROVE it restores
# byte-identical state (the run-2 md5.1 snapshot is taken after the injection,
# so it alone can't detect drift the injection's per-row greps miss).
md5sum "$FLIPS" > "$T/md5.post1"

echo ""
echo "=== live-reconcile injection: delete a pcap AFTER the marker pass → the next run must mark it ==="
echo "The marker pass above (run 1) left rows citing $NEW unmarked because the pcap existed."
echo "Deleting the pcap now simulates disk state changing after pruning — the reconcile must"
echo "be LIVE, not one-shot: the next run must append (pruned) to rows citing it, and a"
echo "recreated pcap must drop the marker again on the run after that."
[ -f "$CAP/$NEW/capture.pcap" ] \
  && ok "injection precondition: NEW pcap exists after the marker pass" \
  || bad "injection precondition: NEW pcap already missing"
rm -f "$CAP/$NEW/capture.pcap"
run_prune   # the 'next run' — reconcile against the changed disk state
marked_new=$(grep -c "pcap=$CAP/$NEW/capture.pcap(pruned)" "$FLIPS" 2>/dev/null || true)
marked_new=${marked_new:-0}
[ "$marked_new" -eq 2 ] \
  && ok "live reconcile: both rows citing the deleted NEW pcap marked on the next run" \
  || bad "live reconcile: expected 2 marked rows for deleted NEW pcap, got $marked_new"
marked_old=$(grep -c "pcap=$CAP/$OLD/capture.pcap(pruned)" "$FLIPS" 2>/dev/null || true)
marked_old=${marked_old:-0}
[ "$marked_old" -eq 3 ] \
  && ok "live reconcile: the 3 OLD-marked rows from run 1 are preserved" \
  || bad "live reconcile: OLD rows changed state ($marked_old)"
total_pruned=$(grep -c "(pruned)" "$FLIPS" 2>/dev/null || true)
total_pruned=${total_pruned:-0}
[ "$total_pruned" -eq 5 ] \
  && ok "live reconcile: all 5 rows now carry the (pruned) marker" \
  || bad "live reconcile: expected 5 total marked rows, got $total_pruned"
grep -q "2026-08-08T18:40:00Z.*capture.pcap(pruned)" "$FLIPS" \
  && ok "live reconcile: negative-control row marked now that its pcap is gone" \
  || bad "live reconcile: negative-control row not marked despite deleted pcap"
echo ""
echo "=== injection restore: recreate the pcap → the next run must unmark again ==="
: > "$CAP/$NEW/capture.pcap"
run_prune
marked_new=$(grep -c "pcap=$CAP/$NEW/capture.pcap(pruned)" "$FLIPS" 2>/dev/null || true)
marked_new=${marked_new:-0}
[ "$marked_new" -eq 0 ] \
  && ok "injection restore: rows citing the recreated NEW pcap unmarked again" \
  || bad "injection restore: expected 0 marked rows for recreated NEW pcap, got $marked_new"
grep -q "2026-08-08T18:40:00Z.*capture.pcap$" "$FLIPS" \
  && ok "injection restore: negative-control row clean again (ends unmarked)" \
  || bad "injection restore: negative-control row not clean"
md5sum "$FLIPS" > "$T/md5.restored"
cmp -s "$T/md5.post1" "$T/md5.restored" \
  && ok "injection restore: log byte-identical to post-run-1 state (self-contained)" \
  || bad "injection restore: log drifted from post-run-1 state"

echo ""
echo "=== run 2 (idempotency: nothing to prune, nothing to mark) ==="
md5sum "$FLIPS" > "$T/md5.1"
run_prune
md5sum "$FLIPS" > "$T/md5.2"
cmp -s "$T/md5.1" "$T/md5.2" \
  && ok "re-run idempotent — nonce-flips.log byte-identical" \
  || bad "re-run modified nonce-flips.log"

echo ""
echo "=== negative control: the (pruned) marker keys on FILE EXISTENCE, not row age/content ==="
echo "The 2026-08-08T18:40:00Z .29 NONCE_FLIP row is the same vintage and content shape as"
echo "the rows marked above, but cites a pcap whose directory STILL exists — after a full"
echo "prune run it must remain unmarked."
[ -d "$CAP/$NEW" ] \
  && ok "negative control: survivor dir present before the full prune run (precondition)" \
  || bad "negative control: survivor dir missing — premise broken, a marker would be legitimate"
run_prune
md5sum "$FLIPS" > "$T/md5.nc"
cmp -s "$T/md5.2" "$T/md5.nc" \
  && ok "negative control: full prune run left the log byte-identical — survivor row unmarked" \
  || bad "negative control: prune run modified the log — marker NOT keyed on file existence"
grep -q "2026-08-08T18:40:00Z.*capture.pcap$" "$FLIPS" \
  && ok "negative control: old-vintage survivor row present and still ends unmarked" \
  || bad "negative control: old-vintage survivor row missing or marked"

echo ""
echo "=== restore: recreate the pruned session, re-run (marker must go away) ==="
mkdir -p "$CAP/$OLD"
: > "$CAP/$OLD/capture.pcap"
run_prune
restored=$(grep -c "(pruned)" "$FLIPS" 2>/dev/null || true)
restored=${restored:-0}
[ "$restored" -eq 0 ] \
  && ok "restored pcap: all (pruned) markers removed" \
  || bad "restored pcap still marked ($restored marker(s) remain)"
grep -q "pcap=$CAP/$OLD/capture.pcap chain=38dfb8c8,9c4e6f8a" "$FLIPS" \
  && ok "restored MID_TICK row clean with chain= preserved" \
  || bad "restored MID_TICK row not clean"

echo ""
echo "=== results: $PASS passed, $FAIL failed ==="
if [ "$FAIL" -eq 0 ]; then
  echo "E2E PRUNE-MARK FIXTURE: ALL GREEN — pre-flight gate passed for $HB"
else
  echo "E2E PRUNE-MARK FIXTURE: FAILURES — pre-flight gate rejected $HB" >&2
fi
# Clamp the failure count to 1: exit 2 is reserved for 'candidate not testable'
# (missing/unparseable), so a gate can distinguish tested-and-failed from
# couldn't-even-test — the count is still printed above for diagnostics.
exit $(( FAIL > 0 ? 1 : FAIL ))
