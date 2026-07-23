# 2026-07-23 Ubuntu Linux compatibility

## Goal
Make BossCamSuite fully usable on Ubuntu without Windows/WPF.

## Approach
| Layer | Ubuntu path |
|-------|-------------|
| Control API | ASP.NET Core `BossCam.Service` (net8.0) |
| Operator UI | Web console at `/` (`wwwroot/`) |
| Recording | ffmpeg + bash snapshot/RTSP pipelines |
| Persistence | SQLite under `~/.local/share/BossCamSuite/` |
| Daemon | systemd (`scripts/install-systemd.sh`) |
| WPF Desktop | Windows-only stub on non-Windows builds |

## Verified on this host
- OS: Ubuntu 24.04.4 LTS
- Runtime: .NET 8.0.29
- `BossCamSuite.Linux.sln` **build OK**
- `BossCamSuite.sln` **build OK** (Desktop becomes empty net8.0 library on Linux)
- Tests: **56/56**
- `GET /` → operator HTML 200
- `GET /app.css`, `/app.js` → 200
- High-res record: `ch0_0.264` → HEVC 2560×1920 TS segments

## How to run
```bash
./scripts/install-ubuntu-deps.sh
./scripts/start-bosscam-ubuntu.sh
# open http://127.0.0.1:5317/
```

## Files added/updated
- `src/BossCam.Service/wwwroot/*` — Linux operator UI
- `src/BossCam.Service/Program.cs` — static files, systemd, platform health
- `src/BossCam.Service/appsettings.Linux.json`
- `src/BossCam.Desktop/BossCam.Desktop.csproj` — OS-conditional stub
- `BossCamSuite.Linux.sln`
- `scripts/start-bosscam-ubuntu.sh`, `install-ubuntu-deps.sh`, `install-systemd.sh`
- `deploy/systemd/bosscam.service`
