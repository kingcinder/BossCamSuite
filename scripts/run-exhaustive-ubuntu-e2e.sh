#!/usr/bin/env bash
# Exhaustive Ubuntu E2E + unit regression for BossCamSuite.
# Produces a machine-readable report under artifacts/e2e/.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export BOSSCAM_E2E_LIVE="${BOSSCAM_E2E_LIVE:-1}"
export BOSSCAM_E2E_IPS="${BOSSCAM_E2E_IPS:-10.0.0.30,10.0.0.170,10.0.0.228}"
export BOSSCAM_FFMPEG_PATH="${BOSSCAM_FFMPEG_PATH:-$(command -v ffmpeg || true)}"

OUT="$ROOT/artifacts/e2e"
mkdir -p "$OUT"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
REPORT="$OUT/report-$STAMP.json"
LOG="$OUT/run-$STAMP.log"

echo "[E2E] Ubuntu exhaustive suite starting ($STAMP)" | tee "$LOG"
echo "[E2E] Live IPs: $BOSSCAM_E2E_IPS (BOSSCAM_E2E_LIVE=$BOSSCAM_E2E_LIVE)" | tee -a "$LOG"

{
  echo "{"
  echo "  \"timestamp\": \"$STAMP\","
  echo "  \"platform\": \"$(uname -a)\","
  echo "  \"dotnet\": \"$(dotnet --version 2>/dev/null || echo missing)\","
  echo "  \"ffmpeg\": \"${BOSSCAM_FFMPEG_PATH:-missing}\","
  echo "  \"liveIps\": \"$BOSSCAM_E2E_IPS\","
  echo "  \"results\": {"
} > "$REPORT"

run_suite() {
  local name="$1"
  local project="$2"
  local trx="$OUT/${name}.trx"
  echo "[E2E] Running $name..." | tee -a "$LOG"
  set +e
  dotnet test "$project" -c Release --logger "trx;LogFileName=${name}.trx" --results-directory "$OUT" 2>&1 | tee -a "$LOG"
  local code=${PIPESTATUS[0]}
  set -e
  echo "    \"$name\": { \"exitCode\": $code, \"trx\": \"$trx\" }," >> "$REPORT"
  return $code
}

UNIT_CODE=0
E2E_CODE=0
run_suite "unit" "$ROOT/tests/BossCam.Tests/BossCam.Tests.csproj" || UNIT_CODE=$?
run_suite "e2e" "$ROOT/tests/BossCam.E2E/BossCam.E2E.csproj" || E2E_CODE=$?

# Linux solution build gate
set +e
dotnet build "$ROOT/BossCamSuite.Linux.sln" -c Release -v q 2>&1 | tee -a "$LOG"
BUILD_CODE=${PIPESTATUS[0]}
set -e
echo "    \"linuxBuild\": { \"exitCode\": $BUILD_CODE }" >> "$REPORT"
echo "  }," >> "$REPORT"
echo "  \"summary\": {" >> "$REPORT"
echo "    \"unitExit\": $UNIT_CODE," >> "$REPORT"
echo "    \"e2eExit\": $E2E_CODE," >> "$REPORT"
echo "    \"buildExit\": $BUILD_CODE," >> "$REPORT"
if [[ $UNIT_CODE -eq 0 && $E2E_CODE -eq 0 && $BUILD_CODE -eq 0 ]]; then
  echo "    \"verdict\": \"PASS\"" >> "$REPORT"
  VERDICT=PASS
else
  echo "    \"verdict\": \"FAIL\"" >> "$REPORT"
  VERDICT=FAIL
fi
echo "  }" >> "$REPORT"
echo "}" >> "$REPORT"

# Human summary
{
  echo
  echo "======== E2E SUMMARY ========"
  echo "Unit tests exit : $UNIT_CODE"
  echo "E2E tests exit  : $E2E_CODE"
  echo "Linux build exit: $BUILD_CODE"
  echo "Verdict         : $VERDICT"
  echo "Report          : $REPORT"
  echo "============================="
} | tee -a "$LOG"

[[ "$VERDICT" == "PASS" ]]
