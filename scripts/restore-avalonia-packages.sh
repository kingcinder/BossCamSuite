#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# restore-avalonia-packages.sh
#
# Download and restore all Avalonia 11.1.0 NuGet packages offline into a local
# feed, then restore the project. This works around environments where
# `dotnet restore` itself times out but individual curl downloads succeed.
#
# Usage:
#   chmod +x scripts/restore-avalonia-packages.sh
#   ./scripts/restore-avalonia-packages.sh
#
# After running, the Avalonia project can be built normally:
#   dotnet build src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
FEED_DIR="/tmp/bosscam-avalonia-feed"
NUGET_SRC="https://api.nuget.org/v3-flatcontainer"

echo "=== Creating local NuGet feed at $FEED_DIR ==="
mkdir -p "$FEED_DIR"

# Core Avalonia packages + all transitive dependencies at correct versions
declare -A PACKAGES
PACKAGES=(
  ["avalonia"]="11.1.0"
  ["avalonia.desktop"]="11.1.0"
  ["avalonia.themes.fluent"]="11.1.0"
  ["avalonia.fonts.inter"]="11.1.0"
  ["avalonia.diagnostics"]="11.1.0"
  ["avalonia.remote.protocol"]="11.1.0"
  ["avalonia.native"]="11.1.0"
  ["avalonia.x11"]="11.1.0"
  ["avalonia.skia"]="11.1.0"
  ["avalonia.win32"]="11.1.0"
  ["avalonia.controls.colorpicker"]="11.1.0"
  ["avalonia.controls.datagrid"]="11.1.0"
  ["avalonia.themes.simple"]="11.1.0"
  ["avalonia.freedesktop"]="11.1.0"
  ["avlonia.angle.windows.natives"]="11.1.0"
  ["communitytoolkit.mvvm"]="8.3.2"
  ["microcom.runtime"]="0.11.0"
  ["avalonia.buildservices"]="0.0.29"
  ["skiasharp"]="2.88.8"
  ["skiasharp.nativeassets.linux"]="2.88.8"
  ["skiasharp.nativeassets.webassembly"]="2.88.8"
  ["harfbuzzsharp"]="7.3.0.2"
  ["harfbuzzsharp.nativeassets.linux"]="7.3.0.2"
  ["harfbuzzsharp.nativeassets.webassembly"]="7.3.0.2"
  ["tmds.dbus"]="0.20.0"
  ["system.numerics.vectors"]="4.5.0"
)

TOTAL=${#PACKAGES[@]}
COUNT=0
FAILED=0

for slug in "${!PACKAGES[@]}"; do
  ver="${PACKAGES[$slug]}"
  nupkg="$slug.$ver.nupkg"
  dest="$FEED_DIR/$nupkg"
  COUNT=$((COUNT + 1))

  if [ -f "$dest" ]; then
    echo "  [$COUNT/$TOTAL] $slug $ver — already cached"
    continue
  fi

  echo -n "  [$COUNT/$TOTAL] Downloading $slug $ver ... "
  if curl -sS --max-time 60 -L "$NUGET_SRC/$slug/$ver/$nupkg" -o "$dest" 2>/dev/null; then
    if unzip -tq "$dest" > /dev/null 2>&1; then
      echo "OK"
    else
      echo "CORRUPT — retrying..."
      rm -f "$dest"
      sleep 1
      if curl -sS --max-time 60 -L "$NUGET_SRC/$slug/$ver/$nupkg" -o "$dest" && unzip -tq "$dest" > /dev/null 2>&1; then
        echo "  -> OK on retry"
      else
        echo "  -> FAILED"
        FAILED=$((FAILED + 1))
      fi
    fi
  else
    echo "FAILED"
    FAILED=$((FAILED + 1))
  fi
done

echo ""
echo "=== Download complete: $((TOTAL - FAILED))/$TOTAL packages OK ==="

if [ "$FAILED" -gt 0 ]; then
  echo "WARNING: $FAILED package(s) failed to download."
  echo "The restore may still work if those are optional transitive deps."
fi

echo ""
echo "=== Restoring Avalonia project from local feed ==="
cd "$REPO_DIR"
dotnet restore src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj \
  --source "$FEED_DIR" \
  --source https://api.nuget.org/v3/index.json \
  $([ "$FAILED" -eq 0 ] && echo "")

echo ""
echo "=== Build test ==="
dotnet build src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj --no-restore --nologo 2>&1 | tail -5

echo ""
echo "=== Done ==="
echo "If restore fails, run the following manually with only the local feed:"
echo "  dotnet restore src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj --source $FEED_DIR"
