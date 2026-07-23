#!/usr/bin/env bash
# Start BossCam service on Linux, optionally register live 5523-W cameras.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export BOSSCAM_FFMPEG_PATH="${BOSSCAM_FFMPEG_PATH:-$(command -v ffmpeg || true)}"
API="${BOSSCAM_API:-http://127.0.0.1:5317}"

echo "[BossCam] Building service..."
dotnet build "$ROOT/src/BossCam.Service/BossCam.Service.csproj" -c Release -v q

echo "[BossCam] Starting API on $API"
dotnet run --project "$ROOT/src/BossCam.Service/BossCam.Service.csproj" -c Release --no-build &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT

echo -n "[BossCam] Waiting for health"
for i in $(seq 1 40); do
  if curl -fsS "$API/api/health" >/dev/null 2>&1; then
    echo " OK"
    break
  fi
  echo -n "."
  sleep 0.5
  if [[ $i -eq 40 ]]; then
    echo " TIMEOUT"
    exit 1
  fi
done

# Register known live cameras if IPs provided or defaults for this LAN.
IPS="${BOSSCAM_CAMERA_IPS:-10.0.0.30,10.0.0.170}"
IFS=',' read -ra CAMS <<< "$IPS"
PAYLOAD='['
first=1
for ip in "${CAMS[@]}"; do
  ip="$(echo "$ip" | xargs)"
  [[ -z "$ip" ]] && continue
  if [[ $first -eq 0 ]]; then PAYLOAD+=','; fi
  first=0
  PAYLOAD+=$(printf '{"ipAddress":"%s","port":80,"loginName":"admin","password":"","name":"5523-W %s","hardwareModel":"5523-W"}' "$ip" "$ip")
done
PAYLOAD+=']'

echo "[BossCam] Registering cameras: $IPS"
curl -fsS -X POST "$API/api/devices/register-many" \
  -H 'Content-Type: application/json' \
  -d "$PAYLOAD" | python3 -m json.tool | head -80

echo "[BossCam] Highlight board:"
curl -fsS "$API/api/highlights" | python3 -m json.tool | head -100

echo "[BossCam] Service running (pid $PID). Ctrl+C to stop."
echo "  GET  $API/api/devices"
echo "  GET  $API/api/highlights"
echo "  POST $API/api/highlights/next"
echo "  POST $API/api/recordings/start-all"
echo "  GET  $API/swagger"
wait $PID
