#!/usr/bin/env bash
# ── sweep-full-pool.sh — exhaustive live password sweep of the FULL candidate
#    pool (28,272 ROM/decompile strings, not just the 96 meaningful ones).
#
# Makes the Precondition-A "0 hits" verdict unconditional before the controlled
# reset experiment touches the button. Sweeps each camera's /NetSDK/System/
# deviceInfo with Basic auth: HTTP 200 = crack, 401 = reject, anything else is
# inspected for a lockout signature (lock|forbidden|too many|denied) and aborts
# that camera on detection.
#
# Safety / design:
#   - 500 ms pacing per attempt (LOCKOUT-avoidance; env-overridable via PACING)
#   - per-camera abort on lockout signature, on 5 consecutive failures (lost),
#     or on a 3-probe unreachable preflight
#   - Basic header built manually (immune to ':' / '@' in candidate strings)
#   - checkpoint/resume per camera with a POOL FINGERPRINT guard: if the pool
#     file changed between runs, resume is refused (restart from #1) so a
#     regenerated pool can never skip the wrong candidates
#   - parallel workers, one per camera; distinct exit codes (see below)
#
# Usage:
#   ./scripts/sweep-full-pool.sh [pool-file] [camera-ip ...]
#     pool-file      default /tmp/sweep-full.txt (28,272 candidates)
#     camera-ips     default 10.0.0.29 10.0.0.169
#   Env: PACING=0.5   seconds between attempts per camera
#   Output: /tmp/sweep-full-<cam>.log|.ckpt|.hits|.lockout
#
# Exit codes: 0 = all cameras swept cleanly, 0 hits (verdict UNCONDITIONAL)
#             1 = a match was found (see /tmp/sweep-full-*.hits)
#             2 = usage/pool error
#             3 = a camera was unreachable (verdict INCOMPLETE)
#             4 = a camera was lost mid-sweep (verdict INCOMPLETE)
#             5 = lockout abort (verdict INCOMPLETE)

set -u
set -o pipefail

POOL="${1:-/tmp/sweep-full.txt}"
if [ "$#" -gt 1 ]; then shift; CAMS="$*"; else CAMS="10.0.0.29 10.0.0.169"; fi
PACING="${PACING:-0.5}"
TMPDIR_BASE="${TMPDIR:-/tmp}"

[ -f "$POOL" ] || { echo "!! pool not found: $POOL"; exit 2; }
command -v curl >/dev/null || { echo "!! curl required"; exit 2; }
command -v base64 >/dev/null || { echo "!! base64 required"; exit 2; }

total=$(wc -l < "$POOL")
pool_fp=$(md5sum "$POOL" | awk '{print $1}')
echo "═══ FULL-POOL PASSWORD SWEEP ═══"
echo "  pool    : $POOL ($total candidates, md5 $pool_fp)"
echo "  cameras : $CAMS"
echo "  pacing  : ${PACING}s per attempt (per camera)"
echo ""

# ── one camera worker ─────────────────────────────────────────────────────────
# return codes: 0 clean, 1 MATCH, 3 unreachable, 4 lost mid-sweep, 5 lockout
sweep_camera() {
  local ip="$1"
  local log="$TMPDIR_BASE/sweep-full-$ip.log"
  local ckpt="$TMPDIR_BASE/sweep-full-$ip.ckpt"
  local hits="$TMPDIR_BASE/sweep-full-$ip.hits"
  local lock="$TMPDIR_BASE/sweep-full-$ip.lockout"
  local body="$TMPDIR_BASE/sweep-full-$ip.body"
  local start_line=1 i=0 consec_fail=0 code out
  : > "$log"

  # resume only if the checkpoint's pool fingerprint matches the current pool
  if [ -f "$ckpt" ]; then
    local ckpt_line ckpt_fp
    ckpt_line=$(sed -n '1p' "$ckpt")
    ckpt_fp=$(sed -n '2p' "$ckpt")
    if [ "$ckpt_fp" = "$pool_fp" ] && [ "$ckpt_line" -gt 1 ] 2>/dev/null; then
      start_line=$ckpt_line
      echo "[$ip] checkpoint found — resuming from candidate #$start_line" | tee -a "$log"
    else
      echo "[$ip] ⚠ checkpoint pool-fingerprint mismatch or stale — restarting from #1" | tee -a "$log"
      start_line=1
    fi
  fi

  # reachability preflight: 3 quick probes, abort camera if all fail
  local ok=0 probe
  for probe in 1 2 3; do
    code=$(curl -sS -o /dev/null -w '%{http_code}' -m 3 "http://$ip/NetSDK/System/deviceInfo" 2>/dev/null)
    code="${code:-000}"
    if [ "$code" != "000" ]; then ok=1; break; fi
    sleep 1
  done
  if [ "$ok" -ne 1 ]; then
    echo "[$ip] ⚠ UNREACHABLE (3 probes all failed) — skipping this camera; verdict INCOMPLETE" | tee -a "$log"
    return 3
  fi
  echo "[$ip] preflight OK (HTTP $code) — starting sweep at #$start_line of $total" | tee -a "$log"

  while IFS= read -r pw; do
    i=$((i + 1))
    [ "$i" -lt "$start_line" ] && continue

    out=$(curl -sS -o "$body" -w '%{http_code}' -m 4 \
      -H "Authorization: Basic $(printf 'admin:%s' "$pw" | base64 -w0)" \
      "http://$ip/NetSDK/System/deviceInfo" 2>/dev/null)
    out="${out:-000}"

    case "$out" in
      200)
        echo "★★★ MATCH: admin:$pw → HTTP 200 on $ip" | tee -a "$log" "$hits"
        return 1
        ;;
      401)
        consec_fail=0
        ;;
      000)
        consec_fail=$((consec_fail + 1))
        if [ "$consec_fail" -ge 5 ]; then
          echo "[$ip] ⚠ 5 consecutive failures — camera unreachable mid-sweep; aborting camera" | tee -a "$log"
          return 4
        fi
        ;;
      *)
        if grep -qiE 'lock|forbidden|too many|account.{0,3}denied' "$body" 2>/dev/null; then
          echo "⚠ LOCKOUT on $ip after '$pw' (HTTP $out): $(head -c 160 "$body")" | tee -a "$log" "$lock"
          return 5
        fi
        echo "[$ip] note: HTTP $out for '$pw' (non-lockout) — continuing" >> "$log"
        ;;
    esac

    if [ $((i % 500)) -eq 0 ]; then
      echo "[$ip] $i/$total candidates tested (last: '$pw')" | tee -a "$log"
    fi
    # checkpoint: line count + pool fingerprint; written AFTER the candidate is
    # fully processed so resume re-tests at most one candidate (idempotent).
    printf '%s\n%s\n' "$i" "$pool_fp" > "$ckpt"
    sleep "$PACING"
  done < "$POOL"

  echo "[$ip] done — $total candidates, 0 hits" | tee -a "$log"
  return 0
}

# ── run workers in parallel, one per camera ───────────────────────────────────
pids=()
for cam in $CAMS; do
  sweep_camera "$cam" &
  pids+=("$!")
done

match_found=0
abort_rc=0
for idx in "${!pids[@]}"; do
  wait "${pids[$idx]}"
  rc=$?
  case "$rc" in
    1) match_found=1 ;;
    3|4|5) [ "$abort_rc" -eq 0 ] && abort_rc=$rc ;;
  esac
done

echo ""
echo "═══ SWEEP COMPLETE ═══"
echo "  artifacts: $TMPDIR_BASE/sweep-full-*"
if [ "$match_found" -eq 1 ]; then
  echo "★★★ A MATCH WAS FOUND — see /tmp/sweep-full-*.hits"
  exit 1
fi
if [ "$abort_rc" -ne 0 ]; then
  echo "⚠ A camera aborted (code $abort_rc: 3=unreachable, 4=lost, 5=lockout) —"
  echo "  the 0-hit verdict is INCOMPLETE, not unconditional. Re-run once the camera is back."
  exit "$abort_rc"
fi
echo "✓ All cameras swept cleanly, 0 hits — the Precondition-A verdict is now UNCONDITIONAL."
exit 0
