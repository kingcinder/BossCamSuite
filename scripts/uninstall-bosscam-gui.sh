#!/usr/bin/env bash
# BossCamSuite uninstaller — removes the systemd unit, the GUI, launcher and
# desktop entry. Data (recordings, snapshots, SQLite) under the service user's
# data dir is left untouched unless BOSSCAM_PURGE_DATA=1 is set.
set -euo pipefail

SERVICE_PREFIX="${BOSSCAM_PREFIX:-/opt/bosscam}"
GUI_PREFIX="${BOSSCAM_GUI_PREFIX:-/opt/bosscam-gui}"

echo "=== [BossCam] Uninstalling ==="

# 1. Stop + disable the service.
if systemctl list-unit-files 2>/dev/null | grep -q '^bosscam.service'; then
  sudo systemctl stop bosscam.service 2>/dev/null || true
  sudo systemctl disable bosscam.service 2>/dev/null || true
  sudo rm -f /etc/systemd/system/bosscam.service
  sudo systemctl daemon-reload
  echo "[BossCam] Service unit removed."
fi

# 2. Remove the GUI + service install dirs.
sudo rm -rf "$GUI_PREFIX" "$SERVICE_PREFIX"
echo "[BossCam] Removed $GUI_PREFIX and $SERVICE_PREFIX"

# 3. Remove desktop entry + icon.
sudo rm -f /usr/share/applications/bosscam-gui.desktop
sudo rm -f /usr/share/icons/hicolor/scalable/apps/bosscam.svg
sudo update-desktop-database /usr/share/applications 2>/dev/null || true
echo "[BossCam] Desktop entry removed."

# 4. Optional data purge.
if [[ "${BOSSCAM_PURGE_DATA:-0}" == "1" ]]; then
  DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/BossCamSuite"
  sudo rm -rf "$DATA_DIR"
  echo "[BossCam] Purged data dir $DATA_DIR"
fi

echo
echo "[BossCam] Uninstall complete."
