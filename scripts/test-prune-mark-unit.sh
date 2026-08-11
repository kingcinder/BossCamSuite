#!/usr/bin/env bash
# ── test-prune-mark-unit.sh — unit fixture for flips_log_prune_mark() ──
#
# Exercises the pruner DIRECTLY (no KEEP_DAYS/KEEP_RUNS directory pruning —
# that is the e2e fixture's job) against fixture history files:
#
#   1. Mode + ownership preservation across the tmp+mv rewrite (640 and 600,
#      mark and unmark paths) — the fix for the silent umask/owner drift.
#   2. Idempotency — mark and unmark re-runs are byte-identical, with no
#      .prune.tmp residue left behind.
#   3. Backfill-style per-path dedup — a timeline like backfill-nonce-state.sh
#      seeds (many rows, several citing the SAME pcap): the pruner's _pcap_ok
#      cache performs one existence check per unique path and marks every row
#      citing a gone pcap consistently, while rows citing a surviving pcap stay
#      unmarked, and a re-run is byte-identical.
#
# Honors HB=... exactly like the e2e fixture, so a candidate heartbeat can be
# pre-flighted here too.
#
# Usage:   ./scripts/test-prune-mark-unit.sh
#          HB=/path/to/candidate.sh ./scripts/test-prune-mark-unit.sh
# Exit:    0 all assertions passed; 1 any assertion failed; 2 heartbeat missing/unparseable

set -u
set -o pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
HB="${HB:-$ROOT/scripts/gate-flip-heartbeat.sh}"
[ -f "$HB" ] || { echo "!! heartbeat script not found (HB=$HB) — set HB=/path/to/gate-flip-heartbeat.sh" >&2; exit 2; }

T="$(mktemp -d /tmp/prune-mark-unit.XXXXXX)"
trap 'rm -rf "$T"' EXIT

# Extract the pruner from the candidate heartbeat (same anchor as the e2e
# fixture): function definition start -> its closing brace at column 0.
PRUNER="$(sed -n '/^flips_log_prune_mark()/,/^}/p' "$HB")"
[ -n "$PRUNER" ] || { echo "!! could not extract flips_log_prune_mark() from $HB — candidate diverged structurally?" >&2; exit 2; }

cat > "$T/run.sh" <<EOF
set -u
set -o pipefail
$PRUNER
flips_log_prune_mark
EOF

run_pruner() {
  # Run the extracted pruner once against the given history file.
  FLIPS_LOG="$1" bash "$T/run.sh"
}

PASS=0
FAIL=0
ok()  { printf 'PASS: %s\n' "$*"; PASS=$((PASS + 1)); }
bad() { printf 'FAIL: %s\n' "$*"; FAIL=$((FAIL + 1)); }

echo "=== unit: flips_log_prune_mark extracted from $HB ==="
echo "heartbeat md5: $(md5sum "$HB" | cut -d' ' -f1)"

# ── 1) mode + ownership preservation across the rewrite ─────────────────────
echo "=== 1) mode + ownership preservation (mark 640, unmark 600) ==="
LOG="$T/log-640"
cat > "$LOG" <<EOF
2026-08-08T18:33:13Z flip cam=10.0.0.29 old=06b71d86 new=6f5e0c61 kind=NONCE_FLIP pcap=$T/captures/old/capture.pcap
2026-08-10T13:00:00Z flip cam=10.0.0.227 old=38dfb8c8 new=9c4e6f8a kind=MID_TICK_FLIP pcap=$T/captures/old/capture.pcap chain=38dfb8c8,9c4e6f8a
EOF
chmod 640 "$LOG"
mb=$(stat -c '%a' "$LOG"); ob=$(stat -c '%u:%g' "$LOG")
run_pruner "$LOG"   # pcap missing -> mark both rows
ma=$(stat -c '%a' "$LOG"); oa=$(stat -c '%u:%g' "$LOG")
[ "$ma" = "$mb" ] \
  && ok "640 mode preserved across mark rewrite ($mb -> $ma)" \
  || bad "640 mode drifted across mark rewrite: $mb -> $ma"
[ "$oa" = "$ob" ] \
  && ok "owner:group preserved across mark rewrite ($ob)" \
  || bad "owner:group drifted across mark rewrite: $ob -> $oa"
[ "$(grep -c '(pruned)' "$LOG")" -eq 2 ] \
  && ok "both rows citing the missing pcap marked" \
  || bad "expected 2 marked rows, got $(grep -c '(pruned)' "$LOG")"
# unmark path on a 600 file: recreate the pcap, drop mode, re-run
mkdir -p "$T/captures/old"
: > "$T/captures/old/capture.pcap"
chmod 600 "$LOG"
mb=$(stat -c '%a' "$LOG"); ob=$(stat -c '%u:%g' "$LOG")
run_pruner "$LOG"
ma=$(stat -c '%a' "$LOG"); oa=$(stat -c '%u:%g' "$LOG")
[ "$ma" = "$mb" ] \
  && ok "600 mode preserved across unmark rewrite ($mb -> $ma)" \
  || bad "600 mode drifted across unmark rewrite: $mb -> $ma"
[ "$oa" = "$ob" ] \
  && ok "owner:group preserved across unmark rewrite ($ob)" \
  || bad "owner:group drifted across unmark rewrite: $ob -> $oa"
[ "$(grep -c '(pruned)' "$LOG")" -eq 0 ] \
  && ok "rows citing the recreated pcap unmarked" \
  || bad "expected 0 marked rows after unmark, got $(grep -c '(pruned)' "$LOG")"
grep -q 'chain=38dfb8c8,9c4e6f8a' "$LOG" \
  && ok "MID_TICK chain= preserved through the unmark rewrite" \
  || bad "MID_TICK chain= lost in the unmark rewrite"

# ── 2) idempotency + no tmp residue ─────────────────────────────────────────
echo "=== 2) idempotency: mark and unmark re-runs are byte-identical ==="
ID="$T/log-idem"
cat > "$ID" <<EOF
2026-08-08T18:33:13Z flip cam=10.0.0.29 old=06b71d86 new=6f5e0c61 kind=NONCE_FLIP pcap=$T/captures/gone/capture.pcap
EOF
run_pruner "$ID"
md5sum "$ID" > "$T/idem.1"
run_pruner "$ID"
md5sum "$ID" > "$T/idem.2"
cmp -s "$T/idem.1" "$T/idem.2" \
  && ok "mark re-run byte-identical (idempotent)" \
  || bad "mark re-run modified the log"
[ ! -e "$ID.prune.tmp" ] \
  && ok "no .prune.tmp residue after mark runs" \
  || bad ".prune.tmp residue left after mark runs"
mkdir -p "$T/captures/gone"
: > "$T/captures/gone/capture.pcap"
run_pruner "$ID"
md5sum "$ID" > "$T/idem.3"
run_pruner "$ID"
md5sum "$ID" > "$T/idem.4"
cmp -s "$T/idem.3" "$T/idem.4" \
  && ok "unmark re-run byte-identical (idempotent)" \
  || bad "unmark re-run modified the log"
[ ! -e "$ID.prune.tmp" ] \
  && ok "no .prune.tmp residue after unmark runs" \
  || bad ".prune.tmp residue left after unmark runs"

# ── 3) backfill-style per-path dedup ────────────────────────────────────────
echo "=== 3) backfill-style per-path dedup: one existence check per unique path ==="
# A timeline shaped like backfill-nonce-state.sh seeds: many rows, several
# citing the SAME pcap paths. The pruner's _pcap_ok cache checks each unique
# path once and must mark every row citing a gone pcap consistently.
D="$T/log-dedup"
cat > "$D" <<EOF
2026-08-08T18:30:00Z flip cam=10.0.0.29 old=06b71d86 new=6f5e0c61 kind=NONCE_FLIP pcap=$T/captures/pruned-a/capture.pcap
2026-08-08T18:33:13Z flip cam=10.0.0.29 old=6f5e0c61 new=b1fe37ab kind=NONCE_FLIP pcap=$T/captures/pruned-a/capture.pcap
2026-08-08T18:40:00Z flip cam=10.0.0.29 old=b1fe37ab new=c2d3e4f5 kind=NONCE_FLIP pcap=$T/captures/pruned-a/capture.pcap
2026-08-09T12:01:47Z flip cam=10.0.0.169 old=2ce84978 new=8671353c kind=NONCE_FLIP pcap=$T/captures/kept/capture.pcap
2026-08-09T12:02:00Z flip cam=10.0.0.169 old=8671353c new=aa11bb22 kind=NONCE_FLIP pcap=$T/captures/kept/capture.pcap
EOF
mkdir -p "$T/captures/kept"
: > "$T/captures/kept/capture.pcap"
run_pruner "$D"
pruned_shared=$(grep -c "pcap=$T/captures/pruned-a/capture.pcap(pruned)" "$D" 2>/dev/null || true)
pruned_shared=${pruned_shared:-0}
[ "$pruned_shared" -eq 3 ] \
  && ok "all 3 rows citing the shared pruned pcap marked consistently" \
  || bad "expected 3 marked rows for shared pruned path, got $pruned_shared"
[ "$(grep -c "pcap=$T/captures/kept/capture.pcap(pruned)" "$D")" -eq 0 ] \
  && ok "both rows citing the surviving pcap stay unmarked" \
  || bad "surviving-pcap rows were marked"
md5sum "$D" > "$T/dedup.1"
run_pruner "$D"
md5sum "$D" > "$T/dedup.2"
cmp -s "$T/dedup.1" "$T/dedup.2" \
  && ok "dedup-history re-run byte-identical" \
  || bad "dedup-history re-run drifted"

echo ""
echo "=== results: $PASS passed, $FAIL failed ==="
if [ "$FAIL" -eq 0 ]; then
  echo "PRUNE-MARK UNIT FIXTURE: ALL GREEN"
else
  echo "PRUNE-MARK UNIT FIXTURE: FAILURES" >&2
fi
# Clamp to 1: exit 2 is reserved for 'candidate not testable'.
exit $(( FAIL > 0 ? 1 : FAIL ))
