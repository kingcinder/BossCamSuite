#!/usr/bin/env bash
# Fully Ubuntu-native launcher for BossCamSuite (service + web operator UI).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export BOSSCAM_FFMPEG_PATH="${BOSSCAM_FFMPEG_PATH:-$(command -v ffmpeg || true)}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"

API_HOST="${BOSSCAM_BIND:-127.0.0.1}"
API_PORT="${BOSSCAM_PORT:-5317}"
API="http://${API_HOST}:${API_PORT}"
export BossCam__LocalApiBaseUrl="$API"

need() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[BossCam] Missing dependency: $1"
    echo "         Install with: sudo apt-get install -y $2"
    exit 1
  fi
}

echo "[BossCam] Checking Ubuntu dependencies..."
need curl curl
need ffmpeg ffmpeg
if ! command -v dotnet >/dev/null 2>&1; then
  echo "[BossCam] .NET SDK not found."
  echo "         Install .NET 8 SDK: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu"
  echo "         Or: curl -fsSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0"
  exit 1
fi

echo "[BossCam] dotnet $(dotnet --version)"
echo "[BossCam] ffmpeg  ${BOSSCAM_FFMPEG_PATH:-missing}"

# Stop anything already bound to the port
if command -v fuser >/dev/null 2>&1; then
  fuser -k "${API_PORT}/tcp" 2>/dev/null || true
  sleep 0.5
fi

echo "[BossCam] Building Linux service (WPF desktop skipped on non-Windows)..."
dotnet build "$ROOT/src/BossCam.Service/BossCam.Service.csproj" -c Release -v q

echo "[BossCam] Starting operator service on $API"
dotnet run --project "$ROOT/src/BossCam.Service/BossCam.Service.csproj" -c Release --no-build &
PID=$!
cleanup() {
  echo
  echo "[BossCam] Stopping pid $PID"
  kill "$PID" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo -n "[BossCam] Waiting for health"
for i in $(seq 1 60); do
  if curl -fsS "$API/api/health" >/dev/null 2>&1; then
    echo " OK"
    break
  fi
  echo -n "."
  sleep 0.5
  if [[ $i -eq 60 ]]; then
    echo " TIMEOUT"
    exit 1
  fi
done

# Optional auto-register
if [[ "${BOSSCAM_AUTO_REGISTER:-1}" == "1" ]]; then
  if [[ -n "${BOSSCAM_CAMERA_IPS:-}" ]]; then
    IPS="$BOSSCAM_CAMERA_IPS"
    PAYLOAD='['
    first=1
    IFS=',' read -ra CAMS <<< "$IPS"
    for ip in "${CAMS[@]}"; do
      ip="$(echo "$ip" | xargs)"
      [[ -z "$ip" ]] && continue
      [[ $first -eq 0 ]] && PAYLOAD+=','
      first=0
      PAYLOAD+=$(printf '{"ipAddress":"%s","port":80,"loginName":"admin","password":"","hardwareModel":"5523-W"}' "$ip")
    done
    PAYLOAD+=']'
    echo "[BossCam] Registering cameras: $IPS"
    curl -fsS -X POST "$API/api/devices/register-many" -H 'Content-Type: application/json' -d "$PAYLOAD" >/dev/null || true
  else
    echo "[BossCam] Registering Aegon LAN defaults (Juan + Lorex + WVC shells)..."
    curl -fsS -X POST "$API/api/devices/register-aegon-lan" \
      -H 'Content-Type: application/json' \
      -d "{\"lorexPassword\":\"${BOSSCAM_LOREX_PASSWORD:-}\",\"wvcPassword\":\"${BOSSCAM_WVC_PASSWORD:-}\"}" \
      >/dev/null || true
  fi
fi

echo
echo "[BossCam] Ready (Ubuntu)."
echo "  Operator UI : $API/"
echo "  Health      : $API/api/health"
echo "  Swagger     : $API/swagger"
echo "  Data dir    : ${XDG_DATA_HOME:-$HOME/.local/share}/BossCamSuite"
echo "  Recordings  : ${XDG_DATA_HOME:-$HOME/.local/share}/BossCamSuite/recordings"
echo
if command -v xdg-open >/dev/null 2>&1 && [[ "${BOSSCAM_OPEN_BROWSER:-1}" == "1" ]]; then
  xdg-open "$API/" >/dev/null 2>&1 || true
fi

wait "$PID"
