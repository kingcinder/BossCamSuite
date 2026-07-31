#!/usr/bin/env bash
# BossCamSuite system-wide installer (Linux, systemd + desktop launcher).
#
# Installs:
#   1. The BossCamService as a systemd unit (bosscam.service) at /opt/bosscam
#   2. The BossCamSuite native desktop GUI (Avalonia) at /opt/bosscam-gui
#   3. A .desktop launcher so the GUI appears in the application menu
#
# Usage:
#   sudo ./scripts/install-bosscam-gui.sh
#
# Optional env:
#   BOSSCAM_PREFIX=/opt/bosscam            service install dir
#   BOSSCAM_GUI_PREFIX=/opt/bosscam-gui    GUI install dir
#   BOSSCAM_SERVICE_USER=${SUDO_USER:-$USER}  user that owns the service
#   BOSSCAM_SKIP_SERVICE=1                 only install the GUI
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVICE_PREFIX="${BOSSCAM_PREFIX:-/opt/bosscam}"
GUI_PREFIX="${BOSSCAM_GUI_PREFIX:-/opt/bosscam-gui}"
# When run under sudo, prefer the invoking user over root so service data
# (SQLite DB, recordings, snapshots) lands in the operator's home directory
# rather than /root. Override with BOSSCAM_SERVICE_USER=<user>.
SERVICE_USER="${BOSSCAM_SERVICE_USER:-${SUDO_USER:-$USER}}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

echo "=== [BossCam] System-wide install ==="
echo "  Service prefix : $SERVICE_PREFIX"
echo "  GUI prefix     : $GUI_PREFIX"
echo "  Service user   : $SERVICE_USER"

# ── 0. Prerequisites ────────────────────────────────────────────────
need() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[BossCam] Missing dependency: $1"
    echo "         Install with: sudo apt-get install -y $2"
    exit 1
  fi
}
need ffmpeg ffmpeg
need rsync rsync
if ! command -v dotnet >/dev/null 2>&1 && [ ! -x "$DOTNET_ROOT/dotnet" ]; then
  echo "[BossCam] .NET SDK not found."
  echo "         Install .NET 8 SDK: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu"
  exit 1
fi

# ── 1. Publish + install the service (systemd) ──────────────────────
if [[ "${BOSSCAM_SKIP_SERVICE:-0}" != "1" ]]; then
  echo
  echo "=== [BossCam] Publishing service (Release) ==="
  dotnet publish "$ROOT/src/BossCam.Service/BossCam.Service.csproj" -c Release -o /tmp/bosscam-service-publish -v q

  echo "=== [BossCam] Installing service to $SERVICE_PREFIX ==="
  sudo mkdir -p "$SERVICE_PREFIX"
  sudo rsync -a --delete /tmp/bosscam-service-publish/ "$SERVICE_PREFIX/"
  sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$SERVICE_PREFIX"

  sudo tee /etc/systemd/system/bosscam.service >/dev/null <<EOF
[Unit]
Description=BossCamSuite camera control service
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=${SERVICE_USER}
WorkingDirectory=${SERVICE_PREFIX}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ROOT=${DOTNET_ROOT}
Environment=PATH=${DOTNET_ROOT}:/usr/local/bin:/usr/bin:/bin
Environment=BOSSCAM_FFMPEG_PATH=/usr/bin/ffmpeg
Environment=BossCam__LocalApiBaseUrl=http://127.0.0.1:5317
ExecStart=${DOTNET_ROOT}/dotnet ${SERVICE_PREFIX}/BossCam.Service.dll
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
  echo "[BossCam] Service installed and started."
fi

# ── 2. Publish + install the native GUI ─────────────────────────────
echo
echo "=== [BossCam] Publishing desktop GUI (Release) ==="
dotnet publish "$ROOT/src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj" -c Release -o /tmp/bosscam-gui-publish -v q

echo "=== [BossCam] Installing GUI to $GUI_PREFIX ==="
sudo mkdir -p "$GUI_PREFIX"
sudo rsync -a --delete /tmp/bosscam-gui-publish/ "$GUI_PREFIX/"
sudo chmod +x "$GUI_PREFIX/BossCam.Desktop.Avalonia"
sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$GUI_PREFIX"

# ── 3. Launcher that ensures the service is up, then starts the GUI ─
sudo tee "$GUI_PREFIX/launch-bosscam.sh" >/dev/null <<EOF
#!/usr/bin/env bash
# BossCamSuite launcher: start the service if needed, then open the GUI.
if systemctl is-active --quiet bosscam.service 2>/dev/null; then
  :
else
  systemctl start bosscam.service 2>/dev/null || true
  # Give the service time to bind before the GUI first health check.
  sleep 1
fi
exec "$GUI_PREFIX/BossCam.Desktop.Avalonia" "\$@"
EOF
sudo chmod +x "$GUI_PREFIX/launch-bosscam.sh"

# ── 4. .desktop entry (application menu) ────────────────────────────
ICON_DIR=/usr/share/icons/hicolor/scalable/apps
sudo mkdir -p "$ICON_DIR"
sudo tee "$ICON_DIR/bosscam.svg" >/dev/null <<'EOF'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">
  <rect x="8" y="16" width="112" height="72" rx="12" fill="#1a1a2e" stroke="#4a4a78" stroke-width="4"/>
  <circle cx="64" cy="52" r="20" fill="none" stroke="#4caf6a" stroke-width="6"/>
  <circle cx="64" cy="52" r="8" fill="#4caf6a"/>
  <rect x="28" y="92" width="34" height="20" rx="4" fill="#23233f" stroke="#4a4a78" stroke-width="2"/>
  <rect x="66" y="92" width="34" height="20" rx="4" fill="#23233f" stroke="#4a4a78" stroke-width="2"/>
</svg>
EOF

sudo tee /usr/share/applications/bosscam-gui.desktop >/dev/null <<EOF
[Desktop Entry]
Type=Application
Name=BossCamSuite
GenericName=Camera Control Platform
Comment=BossCamSuite camera control, recording and diagnostics
Exec=${GUI_PREFIX}/launch-bosscam.sh
Icon=bosscam
Terminal=false
Categories=Utility;AudioVideo;
Keywords=camera;cctv;recording;nvr;
EOF

sudo update-desktop-database /usr/share/applications 2>/dev/null || true

echo
echo "=== [BossCam] Install complete ==="
echo "  Service : systemctl status bosscam"
echo "  GUI     : $GUI_PREFIX/launch-bosscam.sh   (or launch from the app menu)"
echo "  Logs    : journalctl -u bosscam -f"
echo "  API     : http://$(hostname -I | awk '{print $1}'):5317/"
echo
echo "  Uninstall with: sudo ./scripts/uninstall-bosscam-gui.sh"
