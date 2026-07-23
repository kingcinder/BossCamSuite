#!/usr/bin/env bash
# Publish BossCam service to /opt/bosscam and install a systemd unit.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${BOSSCAM_PREFIX:-/opt/bosscam}"
SERVICE_USER="${BOSSCAM_SERVICE_USER:-$USER}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

echo "[BossCam] Publishing self-contained-less framework-dependent build to $PREFIX"
dotnet publish "$ROOT/src/BossCam.Service/BossCam.Service.csproj" -c Release -o /tmp/bosscam-publish -v q

sudo mkdir -p "$PREFIX"
sudo rsync -a --delete /tmp/bosscam-publish/ "$PREFIX/"
sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$PREFIX"

UNIT=/etc/systemd/system/bosscam.service
sudo tee "$UNIT" >/dev/null <<EOF
[Unit]
Description=BossCamSuite camera control service
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=${SERVICE_USER}
WorkingDirectory=${PREFIX}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ROOT=${DOTNET_ROOT}
Environment=PATH=${DOTNET_ROOT}:/usr/local/bin:/usr/bin:/bin
Environment=BOSSCAM_FFMPEG_PATH=/usr/bin/ffmpeg
Environment=BossCam__LocalApiBaseUrl=http://0.0.0.0:5317
ExecStart=${DOTNET_ROOT}/dotnet ${PREFIX}/BossCam.Service.dll
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=bosscam

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable bosscam.service
sudo systemctl restart bosscam.service
sudo systemctl --no-pager --full status bosscam.service || true

echo
echo "[BossCam] systemd unit installed."
echo "  sudo systemctl status bosscam"
echo "  journalctl -u bosscam -f"
echo "  Open http://$(hostname -I | awk '{print $1}'):5317/"
