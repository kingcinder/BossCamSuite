#!/usr/bin/env bash
# ── install-recovery-polkit.sh — authorize the BossCamSuite service for NetworkManager ──
#
# The autonomous camera-recovery worker (CameraRecoveryAutoWorker) needs the systemd
# service user to be able to run `nmcli dev wifi connect` against factory-reset camera
# AP hotspots and back to the home WiFi. Systemd service processes are not in an
# interactive logind session, so polkit denies them by default (observed live 2026-08-11:
# `nmcli dev wifi rescan` was denied from the service context).
#
# This installs scripts/polkit/50-bosscam-networkmanager.rules and reloads polkit.
# Requires root (sudo). Run once after deployment.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/scripts/polkit/50-bosscam-networkmanager.rules"
DST="/etc/polkit-1/rules.d/50-bosscam-networkmanager.rules"

[ -f "$SRC" ] || { echo "✘ rule file missing: $SRC" >&2; exit 1; }

echo "installing $SRC -> $DST"
install -m 644 -o root -g root "$SRC" "$DST"

echo "reloading polkit..."
if command -v systemctl >/dev/null 2>&1 && systemctl is-active polkit >/dev/null 2>&1; then
    systemctl restart polkit
elif command -v systemctl >/dev/null 2>&1 && systemctl is-active polkitd >/dev/null 2>&1; then
    systemctl restart polkitd
elif command -v pkill >/dev/null 2>&1; then
    pkill -HUP polkitd 2>/dev/null || true
fi

echo "✔ installed. Verify with:"
echo "    pkcheck --action-id org.freedesktop.NetworkManager.wifi.connect --process $$ --user cody"
