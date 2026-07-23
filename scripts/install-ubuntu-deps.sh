#!/usr/bin/env bash
# Install runtime dependencies for BossCamSuite on Ubuntu 22.04/24.04.
set -euo pipefail

echo "[BossCam] Installing apt packages (curl, ffmpeg, ca-certificates)..."
sudo apt-get update
sudo apt-get install -y curl ffmpeg ca-certificates psmisc

if ! command -v dotnet >/dev/null 2>&1; then
  echo "[BossCam] Installing .NET 8 SDK to \$HOME/.dotnet ..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"
  # shellcheck disable=SC2016
  if ! grep -q '.dotnet' "$HOME/.bashrc" 2>/dev/null; then
    {
      echo ''
      echo '# BossCamSuite / .NET'
      echo 'export DOTNET_ROOT=$HOME/.dotnet'
      echo 'export PATH=$DOTNET_ROOT:$PATH'
    } >> "$HOME/.bashrc"
  fi
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi

echo "[BossCam] dotnet: $(dotnet --version 2>/dev/null || echo missing)"
echo "[BossCam] ffmpeg: $(command -v ffmpeg)"
echo "[BossCam] Done. Start with: ./scripts/start-bosscam-ubuntu.sh"
