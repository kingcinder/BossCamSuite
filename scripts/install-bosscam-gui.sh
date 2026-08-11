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
# The desktop shortcut belongs to the operator who invoked sudo, not root.
OPERATOR_HOME="$(getent passwd "$SERVICE_USER" | cut -d: -f6 2>/dev/null || true)"
OPERATOR_HOME="${OPERATOR_HOME:-$HOME}"

# Locate the .NET SDK for the invoking user. Under sudo, $HOME points at
# /root, so resolve the real operator home from SUDO_USER (or /etc/passwd)
# before defaulting DOTNET_ROOT -- otherwise 'dotnet' is never found.
INVOKING_HOME="$HOME"
if [[ -n "${SUDO_USER:-}" ]]; then
  INVOKING_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)" || INVOKING_HOME="$HOME"
fi
# getent can succeed but return an empty home field on exotic setups; never
# export an empty HOME.
if [[ -z "$INVOKING_HOME" ]]; then
  INVOKING_HOME="$HOME"
fi

# Under sudo, HOME points at /root -> dotnet gets a COLD NuGet cache and its
# first-run experience, which makes `dotnet restore` appear to hang forever.
# Point the whole script at the operator's real home so the warm package cache
# is reused and restore completes in seconds.
export HOME="$INVOKING_HOME"
if [[ -z "${DOTNET_ROOT:-}" && -x "$INVOKING_HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$INVOKING_HOME/.dotnet"
elif [[ -z "${DOTNET_ROOT:-}" && -x "$INVOKING_HOME/.local/share/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$INVOKING_HOME/.local/share/dotnet"
elif [[ -z "${DOTNET_ROOT:-}" ]] && command -v dotnet >/dev/null 2>&1; then
  # Distro-packaged SDK (e.g. /usr/bin/dotnet -> /usr/lib/dotnet/dotnet). The
  # .NET host honors DOTNET_ROOT over its own self-location, so resolve the
  # symlink first: a root of /usr/bin has no shared/Microsoft.NETCore.App and
  # the unit would crash-loop with 'You must install or update .NET'.
  _dotnet_bin="$(readlink -f "$(command -v dotnet)")"
  export DOTNET_ROOT="$(dirname "$_dotnet_bin")"
  unset _dotnet_bin
fi
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}$PATH"

# Never reuse MSBuild build-server nodes inside the installer. A prior aborted
# run (or a killed publish) leaves /nodemode nodes behind whose handshake can
# hang every later dotnet invocation silently at ~0% CPU. Fresh nodes each run.
export MSBUILDDISABLENODEREUSE=1

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Force IPv4 for NuGet HTTP. Many LANs (including the one this installer was
# smoke-tested on) blackhole IPv6: DNS returns AAAA-first and the .NET/NuGet
# client stalls on every package download, so `dotnet restore` appears to hang
# forever while curl -4 works instantly. Overridable for IPv6-only hosts.
: "${DOTNET_SYSTEM_NET_DISABLEIPV6:=1}"
export DOTNET_SYSTEM_NET_DISABLEIPV6

# The systemd unit embeds DOTNET_ROOT; never write a unit with an empty one.
if [[ -z "${DOTNET_ROOT:-}" ]]; then
  echo "[BossCam] .NET SDK not found."
  echo "         Install .NET 8 SDK: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu"
  exit 1
fi

# Restore (explicit, so failures are visible) then publish with --no-restore.
# Publish's implicit restore can hang indefinitely on some hosts (stale local
# feeds, dead MSBuild nodes); a bounded explicit restore keeps installs honest.
restore_or_fail() {
  local project="$1"
  echo "[BossCam] Restoring $project"
  # Bounded: a hanging NuGet restore used to stall the whole install at ~0% CPU.
  # 600s: a genuinely cold operator cache downloads the whole solution on
  # first install; 300s can false-fail a merely-slow restore on slow links.
  if ! timeout 600 dotnet restore "$project" --nologo -v m; then
    echo "[BossCam] Restore FAILED or timed out for $project." >&2
    echo "         Check network access to nuget.org and the sources in nuget.config." >&2
    echo "         Tip: run restore as your own user (HOME=~). IPv4 is forced via" >&2
    echo "         DOTNET_SYSTEM_NET_DISABLEIPV6=1 (override to 0 on IPv6-only hosts)." >&2
    exit 1
  fi
}

publish_project() {
  local project="$1" out="$2"
  # Try publish --no-restore first: fast on hosts with valid assets, and it
  # avoids NuGet restore entirely when everything is cached. If it fails (no
  # assets, or stale/polluted assets like an obj/ regenerated against a partial
  # feed), run the bounded restore and retry once.
  if dotnet publish "$project" -c Release -o "$out" --no-restore --nologo -v q; then
    echo "[BossCam] Published $project (no restore needed)"
    return 0
  fi
  echo "[BossCam] publish failed for $project (missing/stale assets); restoring..."
  restore_or_fail "$project"
  dotnet publish "$project" -c Release -o "$out" --no-restore --nologo -v q
  echo "[BossCam] Published $project (after restore)"
}

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
need curl curl
# DOTNET_ROOT is guaranteed non-empty by the fail-fast above; the explicit
# restore_or_fail helper validates assets before each publish.

# ── 1. Publish + install the service (systemd) ──────────────────────
if [[ "${BOSSCAM_SKIP_SERVICE:-0}" != "1" ]]; then
  echo
  echo "=== [BossCam] Publishing service (Release) ==="
  publish_project "$ROOT/src/BossCam.Service/BossCam.Service.csproj" /tmp/bosscam-service-publish

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
publish_project "$ROOT/src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj" /tmp/bosscam-gui-publish

echo "=== [BossCam] Installing GUI to $GUI_PREFIX ==="
sudo mkdir -p "$GUI_PREFIX"
sudo rsync -a --delete /tmp/bosscam-gui-publish/ "$GUI_PREFIX/"
sudo chmod +x "$GUI_PREFIX/BossCam.Desktop.Avalonia"
sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$GUI_PREFIX"

# ── 3. Launcher that ensures the service is up, then starts the GUI ─
sudo tee "$GUI_PREFIX/launch-bosscam.sh" >/dev/null <<EOF
#!/usr/bin/env bash
# BossCamSuite launcher: the service is enabled at boot and restarted by systemd.
# A normal desktop user may not be allowed to start a system unit, so never silently
# open the GUI against a dead API: try once, then wait for the local health endpoint.
if ! systemctl is-active --quiet bosscam.service 2>/dev/null; then
  systemctl start bosscam.service 2>/dev/null || true
fi
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if curl --silent --fail --max-time 1 http://127.0.0.1:5317/api/health >/dev/null 2>&1; then
    exec "$GUI_PREFIX/BossCam.Desktop.Avalonia" "\$@"
  fi
  sleep 1
done
if command -v notify-send >/dev/null 2>&1; then
  notify-send --urgency=critical "BossCamSuite" "Local service is not responding. Check: systemctl status bosscam"
fi
printf '%s\n' "BossCamSuite service is not responding on http://127.0.0.1:5317." >&2
exit 1
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

DESKTOP_ENTRY_CONTENT="[Desktop Entry]
Type=Application
Name=BOSSCAMSUITE SHRTCUT
GenericName=Camera Control Platform
Comment=BossCamSuite camera control, recording and diagnostics
Exec="${GUI_PREFIX}/launch-bosscam.sh"
Icon=bosscam
Terminal=false
Path=${GUI_PREFIX}
Categories=Utility;AudioVideo;
Keywords=camera;cctv;recording;nvr;
StartupNotify=true"

# Install the application-menu entry.
printf '%s
' "$DESKTOP_ENTRY_CONTENT" | sudo tee /usr/share/applications/bosscam-gui.desktop >/dev/null

# Also place a clickable shortcut on the operator's Ubuntu desktop. Desktop
# environments require the .desktop file to be executable before allowing it
# to launch; ownership is assigned to the real operator, never root.
OPERATOR_DESKTOP="$OPERATOR_HOME/Desktop"
sudo mkdir -p "$OPERATOR_DESKTOP"
sudo chown "$SERVICE_USER:$SERVICE_USER" "$OPERATOR_DESKTOP"
printf '%s
' "$DESKTOP_ENTRY_CONTENT" | sudo tee "$OPERATOR_DESKTOP/BOSSCAMSUITE SHRTCUT.desktop" >/dev/null
sudo chown "$SERVICE_USER:$SERVICE_USER" "$OPERATOR_DESKTOP/BOSSCAMSUITE SHRTCUT.desktop"
sudo chmod +x "$OPERATOR_DESKTOP/BOSSCAMSUITE SHRTCUT.desktop"

sudo update-desktop-database /usr/share/applications 2>/dev/null || true

echo
echo "=== [BossCam] Install complete ==="
echo "  Service : systemctl status bosscam"
echo "  GUI     : $GUI_PREFIX/launch-bosscam.sh   (or use the BOSSCAMSUITE SHRTCUT desktop icon)"
echo "  Shortcut: $OPERATOR_DESKTOP/BOSSCAMSUITE SHRTCUT.desktop"
echo "  Logs    : journalctl -u bosscam -f"
echo "  API     : http://$(hostname -I | awk '{print $1}'):5317/"
echo
echo "  Uninstall with: sudo ./scripts/uninstall-bosscam-gui.sh"
