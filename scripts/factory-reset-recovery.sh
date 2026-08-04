#!/usr/bin/env bash
# factory-reset-recovery.sh — full pipeline from factory-reset camera to
# enrolled, recording, and optionally watched.
#
# Chains together:
#   1. camera-recovery.sh   — poll until deviceInfo, RTSP, snapshot healthy
#   2. rtsp-enable.sh       — try to re-enable RTSP if it didn't come up
#   3. camera-recovery.sh   — re-poll after RTSP enable attempt
#   4. camera-recovery.sh   — enroll + optional continuous record
#   5. camera-recovery.sh   — optional watchdog
#
# Usage:
#   ./scripts/factory-reset-recovery.sh 10.0.0.169
#   ./scripts/factory-reset-recovery.sh --enroll 10.0.0.169
#   ./scripts/factory-reset-recovery.sh --enroll --record --watchdog 10.0.0.169
#   ./scripts/factory-reset-recovery.sh --enroll --reboot 10.0.0.169 admin mypassword
#
# Flags:
#   --enroll    Auto-enroll via BossCam API after recovery
#   --record    Also start continuous recording (requires --enroll)
#   --watchdog  Keep running and re-check after everything is done
#   --reboot    Allow rtsp-enable.sh to reboot the camera if needed
#
# Env vars: all CAMERA_* vars from camera-recovery.sh and rtsp-enable.sh
# are forwarded.  Additionally:
#   PIPELINE_RTSP_RETRIES   max attempts to recover RTSP (default 2)
set -euo pipefail

# ── helpers ──────────────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BLUE='\033[0;34m'
NC='\033[0m'

ts() { date -u '+%Y-%m-%dT%H:%M:%SZ'; }

log()    { printf "${CYAN}[%s]${NC} %s\n" "$(ts)" "$*"; }
pass()   { printf "${GREEN}[%s]  ✔ %s${NC}\n" "$(ts)" "$*"; }
fail()   { printf "${RED}[%s]  ✘ %s${NC}\n" "$(ts)" "$*"; }
warn()   { printf "${YELLOW}[%s]  ⚠ %s${NC}\n" "$(ts)" "$*"; }
banner() { printf "${BLUE}[%s]${NC} %s\n" "$(ts)" "$*"; }

# ── resolve script paths ─────────────────────────────────────────────────

SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECOVERY="${SCRIPTS_DIR}/camera-recovery.sh"
RTSP_ENABLE="${SCRIPTS_DIR}/rtsp-enable.sh"

for script in "$RECOVERY" "$RTSP_ENABLE"; do
  if [[ ! -x "$script" ]]; then
    echo "ERROR: Required script not found or not executable: $script" >&2
    exit 2
  fi
done

# ── parse flags & args ───────────────────────────────────────────────────

HAS_ENROLL=0
HAS_RECORD=0
HAS_WATCHDOG=0
ALLOW_REBOOT=0
SKIP_RTSP_RECOVERY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --enroll)   HAS_ENROLL=1; shift ;;
    --record)   HAS_RECORD=1; shift ;;
    --watchdog) HAS_WATCHDOG=1; shift ;;
    --reboot)   ALLOW_REBOOT=1; shift ;;
    *)          break ;;
  esac
done

IP="${1:-}"
PIPELINE_RTSP_RETRIES="${PIPELINE_RTSP_RETRIES:-2}"

if [[ -z "$IP" ]]; then
  echo "Usage: $0 [--enroll] [--record] [--watchdog] [--reboot] <camera-ip> [username] [password]"
  echo ""
  echo "Flags:"
  echo "  --enroll    Auto-enroll via BossCam API after recovery"
  echo "  --record    Also start continuous recording (requires --enroll)"
  echo "  --watchdog  Keep running and re-check health after everything is done"
  echo "  --reboot    Allow rtsp-enable.sh to reboot the camera if RTSP won't start"
  echo ""
  echo "Pipeline steps:"
  echo "  1. Poll until camera is reachable (camera-recovery.sh)"
  echo "  2. If RTSP is down, try to enable it (rtsp-enable.sh)"
  echo "  3. Re-poll with RTSP check (camera-recovery.sh)"
  echo "  4. Enroll + optional record (camera-recovery.sh --enroll [--record])"
  echo "  5. Optional watchdog (camera-recovery.sh --watchdog)"
  exit 1
fi

# Build common args for forwarding to sub-scripts.
# Reconstruct positional args: IP [user] [pass]
FORWARD_ARGS=("$IP")
if [[ $# -ge 2 ]]; then FORWARD_ARGS+=("$2"); fi
if [[ $# -ge 3 ]]; then FORWARD_ARGS+=("$3"); fi

# Build env vars to export for sub-scripts.
# Pass through any CAMERA_* vars that are already set; sub-scripts have
# their own defaults so we only need to forward explicit overrides.
export CAMERA_ENROLL=0
export CAMERA_RECORD=0
export CAMERA_WATCHDOG=0
[[ -n "${CAMERA_TIMEOUT:-}" ]] && export CAMERA_TIMEOUT
[[ -n "${CAMERA_INTERVAL:-}" ]] && export CAMERA_INTERVAL
[[ -n "${CAMERA_USER:-}" ]] && export CAMERA_USER
[[ -n "${CAMERA_PASS:-}" ]] && export CAMERA_PASS
[[ -n "${CAMERA_PORT:-}" ]] && export CAMERA_PORT
[[ -n "${CAMERA_RTSP_PORT:-}" ]] && export CAMERA_RTSP_PORT
[[ -n "${CAMERA_API:-}" ]] && export CAMERA_API
[[ -n "${CAMERA_MODEL:-}" ]] && export CAMERA_MODEL
[[ -n "${CAMERA_NAME:-}" ]] && export CAMERA_NAME
[[ -n "${CAMERA_WATCHDOG_INTERVAL:-}" ]] && export CAMERA_WATCHDOG_INTERVAL
[[ $ALLOW_REBOOT -eq 1 ]] && export CAMERA_REBOOT=1

# ── pipeline ─────────────────────────────────────────────────────────────

banner "╔══════════════════════════════════════════════════════╗"
banner "║  Factory-Reset Recovery Pipeline                    ║"
banner "║  Camera: ${IP}                                      ║"
banner "╚══════════════════════════════════════════════════════╝"
echo ""

# ── STEP 1: Initial recovery (poll until healthy) ─────────────────────

banner "STEP 1/5: Polling until camera is reachable..."
banner "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if "$RECOVERY" "${FORWARD_ARGS[@]}"; then
  pass "Step 1 complete — camera is healthy (including RTSP)."
  SKIP_RTSP_RECOVERY=1
else
  rc=$?
  warn "Step 1: camera-recovery.sh exited with code ${rc}."
  warn "Camera may be partially up — attempting RTSP recovery next."
  SKIP_RTSP_RECOVERY=0
fi

# ── STEP 2: RTSP recovery (if needed) ──────────────────────────────────

if [[ $SKIP_RTSP_RECOVERY -eq 0 ]]; then
  banner ""
  banner "STEP 2/5: Attempting RTSP recovery..."
  banner "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

  rtsp_attempt=0
  rtsp_ok=0

  while [[ $rtsp_attempt -lt $PIPELINE_RTSP_RETRIES ]]; do
    rtsp_attempt=$((rtsp_attempt + 1))
    log "RTSP recovery attempt ${rtsp_attempt}/${PIPELINE_RTSP_RETRIES}..."

    if "$RTSP_ENABLE" "${FORWARD_ARGS[@]}"; then
      pass "rtsp-enable.sh succeeded on attempt ${rtsp_attempt}."
      rtsp_ok=1
      break
    else
      warn "rtsp-enable.sh attempt ${rtsp_attempt} did not restore RTSP."
      if [[ $rtsp_attempt -lt $PIPELINE_RTSP_RETRIES ]]; then
        log "Retrying in 10s..."
        sleep 10
      fi
    fi
  done

  if [[ $rtsp_ok -eq 0 ]]; then
    warn "All ${PIPELINE_RTSP_RETRIES} RTSP recovery attempts exhausted."
    warn "Continuing pipeline — enrollment will use snapshot-only fallback."
  fi
fi

# ── STEP 3: Re-poll after RTSP recovery ────────────────────────────────

if [[ $SKIP_RTSP_RECOVERY -eq 0 ]]; then
  banner ""
  banner "STEP 3/5: Re-polling camera after RTSP recovery..."
  banner "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

  # Use a shorter timeout for the re-poll since the camera should be close.
  # Default to 60s if the user hasn't set CAMERA_TIMEOUT.
  if CAMERA_TIMEOUT=${CAMERA_TIMEOUT:-60} "$RECOVERY" "${FORWARD_ARGS[@]}"; then
    pass "Step 3 complete — camera is healthy after RTSP recovery."
  else
    warn "Step 3: camera still not fully healthy after RTSP recovery."
    warn "Continuing pipeline — enrollment will handle degraded state."
  fi
fi

# ── STEP 4: Enroll + optional record ───────────────────────────────────

if [[ $HAS_ENROLL -eq 1 ]]; then
  banner ""
  banner "STEP 4/5: Enrolling camera via BossCam API..."
  banner "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

  ENROLL_FLAGS=(--enroll)
  [[ $HAS_RECORD -eq 1 ]] && ENROLL_FLAGS+=(--record)

  if "$RECOVERY" "${ENROLL_FLAGS[@]}" "${FORWARD_ARGS[@]}"; then
    pass "Step 4 complete — camera enrolled."
  else
    fail "Step 4: enrollment failed."
    warn "Check the BossCam API is running and credentials are correct."
  fi
else
  banner ""
  banner "STEP 4/5: Skipped (--enroll not set)."
fi

# ── STEP 5: Watchdog (optional) ────────────────────────────────────────

if [[ $HAS_WATCHDOG -eq 1 ]]; then
  banner ""
  banner "STEP 5/5: Starting watchdog..."
  banner "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

  # Watchdog runs forever — pass through to camera-recovery.sh --watchdog.
  # This call does not return until Ctrl+C.
  exec "$RECOVERY" --watchdog "${FORWARD_ARGS[@]}"
else
  banner ""
  banner "STEP 5/5: Skipped (--watchdog not set)."
fi

# ── done ─────────────────────────────────────────────────────────────────

echo ""
banner "╔══════════════════════════════════════════════════════╗"
banner "║  Pipeline complete.                                 ║"
banner "║  Camera ${IP} is ready.                             ║"
banner "╚══════════════════════════════════════════════════════╝"
