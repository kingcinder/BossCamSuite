# BossCamSuite — Linux/Ubuntu Edition

> **Linux-native build** of BossCamSuite for Ubuntu. The Windows edition (WPF Desktop + PowerShell launcher) is maintained as a separate repo at [github.com/kingcinder/BossCam-Suite---Windows-Edition](https://github.com/kingcinder/BossCam-Suite---Windows-Edition).

Linux/Ubuntu control suite and VMS scaffold for the 5523-w camera family.

Implemented surfaces in this repository:
- LAN NETSDK REST control adapter
- IPCamSuite private HTTP/CGI adapter
- EseeCloud app import and remote-command envelope adapter
- discovery providers for HiChip multicast, DVR broadcast, and ONVIF WS-Discovery
- SQLite-backed local inventory, audit log, capability cache, protocol manifest store, endpoint validation store, transcript store, and firmware artifact catalog
- ASP.NET Core local service host
- **Svelte 5 + Vite + TypeScript** web operator console (primary UI)
- ProbeRunner CLI with staged probe sessions, transcript bundle export, and resumable runs
- contract-driven typed normalization/apply layer for top groups (Video/Image, Network/Wireless, Users/Maintenance)
- endpoint contract catalog + transcript-to-fixture evidence promotion
- firmware-scoped capability promotion driven by contract truth + live evidence quality
- semantic write classification and sensitive-field audit redaction
- FFmpeg-backed recording orchestration + segment indexing + clip export API
- **SignalR real-time push events** for live UI updates

---

## Quick Start

### Prerequisites

```bash
# Ubuntu 22.04+ or other Debian-based distro
sudo ./scripts/install-ubuntu-deps.sh
```

This installs:
- .NET 8 SDK
- ffmpeg
- curl, Node.js (for Svelte UI rebuilds)
- OpenSSL (for LAN token generation)

### One-Command Launcher

```bash
chmod +x scripts/*.sh
./scripts/start-bosscam-ubuntu.sh
```

Then open **http://127.0.0.1:5317/** in your browser.

### Build & Run Manually

```bash
dotnet build BossCamSuite.Linux.sln -c Release
dotnet run --project src/BossCam.Service/BossCam.Service.csproj
```

---

## Environment Variables

| Variable | Meaning |
|----------|---------|
| `BOSSCAM_BIND` | Bind host (default `127.0.0.1`; use `0.0.0.0` for LAN) |
| `BOSSCAM_PORT` | Port (default `5317`) |
| `BOSSCAM_CAMERA_IPS` | Comma IPs to register instead of Aegon defaults |
| `BOSSCAM_LOREX_PASSWORD` / `BOSSCAM_WVC_PASSWORD` | Brand credentials |
| `BOSSCAM_FFMPEG_PATH` | ffmpeg binary path |
| `BOSSCAM_OPEN_BROWSER` | `0` to skip `xdg-open` |
| `BOSSCAM_LAN_TOKEN` | LAN bearer token. Required when binding to a non-loopback address (`0.0.0.0`). Generate with `openssl rand -hex 32`. |
| `BOSSCAM_E2E_LIVE` | Set to `0` to skip LAN probes in E2E tests (offline CI mode). |

---

## LAN Auth Token (host-aware gate)

When binding to a LAN address, a bearer token is **required** — the service refuses to start without one.

```bash
# 1. Generate a token
openssl rand -hex 32

# 2. Export it
export BOSSCAM_LAN_TOKEN='<paste-the-token-here>'

# 3. Bind to LAN
export BOSSCAM_BIND=0.0.0.0

# 4. Start
./scripts/start-bosscam-ubuntu.sh
```

For systemd units, edit `deploy/systemd/bosscam.service` and reload:

```bash
sudo systemctl daemon-reload
sudo systemctl restart bosscam.service
```

The middleware accepts the token via:
- `X-LAN-Token: <token>` (preferred; sent by the SPA automatically)
- `Authorization: Bearer <token>` (for curl / API clients)

Open paths (always accessible): `/api/health`, `/`, `/index.html`.

---

## Docker

A multi-stage Dockerfile is provided for containerized deployment.

```bash
sudo docker compose build
sudo docker compose run -e BOSSCAM_LAN_TOKEN=$(openssl rand -hex 32) -p 5317:5317 bosscam

# Or as a daemon
echo 'BOSSCAM_LAN_TOKEN=<your-token>' > .env
sudo docker compose up -d
```

---

## Svelte Management UI (development)

```bash
cd src/BossCam.ManagementUI
npm install
npm run dev      # Vite dev server at http://localhost:5173, proxies /api to the service
```

Production builds (`npm run build`) output compiled assets to `src/BossCam.Service/wwwroot/`, served automatically.

---

## Running Tests

```bash
dotnet test BossCamSuite.Linux.sln -c Release

# Offline E2E (no cameras needed)
BOSSCAM_E2E_LIVE=0 ./scripts/run-exhaustive-ubuntu-e2e.sh

# Live E2E against Aegon LAN cameras
./scripts/run-exhaustive-ubuntu-e2e.sh
```

---

## Probe Runner

```bash
# Safe read-only on known targets
dotnet run --project src/BossCam.ProbeRunner/BossCam.ProbeRunner.csproj -- \
  --mode SafeReadOnly --device-ips 10.0.0.4,10.0.0.29,10.0.0.227 \
  --resume true --export-dir ./artifacts --export-summary ./artifacts/probe-summary.json

# Safe write-verify on a single device
dotnet run --project src/BossCam.ProbeRunner/BossCam.ProbeRunner.csproj -- \
  --mode SafeWriteVerify --device-ip 10.0.0.4 --resume true \
  --include-persistence false --export-dir ./artifacts
```

Probe stage values: `InventoryOnly`, `SafeReadOnly`, `SafeWriteVerify`, `NetworkImpacting`, `RebootRequired`, `ExpertFull`.

---

## Data Storage

Data lives under `~/.local/share/BossCamSuite/` (SQLite DB + recordings + firmware artifacts).

---

## API Reference

- **Recording:** `POST /api/recordings/start`, `POST /api/recordings/stop/{jobId}`, `GET /api/recordings/jobs`
- **Highlights:** `GET /api/highlights`, `POST /api/highlights/select/{deviceId}`
- **Device Settings:** `GET /api/devices`, `POST /api/devices/{id}/settings/write`
- **Storage:** `GET /api/storage/paths`, `POST /api/storage/paths`
- **Swagger:** `http://127.0.0.1:5317/swagger`
- **SignalR Hub:** `/hub/bosscam` (real-time push events)

Full API documentation is available via Swagger UI when the service is running.

---

## systemd Install

```bash
sudo ./scripts/install-systemd.sh
sudo systemctl status bosscam
```

---

## Project Structure

```
BossCamSuite-main/
├── BossCamSuite.Linux.sln        # Linux solution (no WPF Desktop)
├── Dockerfile                    # Multi-stage container build
├── docker-compose.yml            # Docker Compose config
├── deploy/
│   └── systemd/
│       └── bosscam.service       # systemd unit file
├── scripts/
│   ├── install-systemd.sh
│   ├── install-ubuntu-deps.sh
│   ├── start-bosscam-ubuntu.sh
│   ├── start-bosscam-linux.sh
│   └── run-exhaustive-ubuntu-e2e.sh
├── src/
│   ├── BossCam.Service/          # ASP.NET Core API host
│   ├── BossCam.ManagementUI/     # Svelte 5 web operator console
│   ├── BossCam.Core/             # Business logic & services
│   ├── BossCam.Infrastructure/   # SQLite, discovery, control adapters
│   ├── BossCam.Contracts/        # Shared DTOs & models
│   ├── BossCam.ProbeRunner/      # CLI probe tool
│   └── BossCam.NativeBridge/     # Native DLL interop
├── tests/
│   ├── BossCam.Tests/            # Unit tests (18 test classes)
│   └── BossCam.E2E/              # E2E integration tests
└── assets/
    └── protocols/                # Protocol manifests
```

---

## Test Index

| Class | Purpose |
|-------|---------|
| `BossCamSuiteTests` | Protocol manifest provider, ImportProvider, FirmwareArtifactAnalyzer |
| `BindAddressInspectorTests` | Bind string classification (loopback/LAN/IPv6) |
| `CompositeInteractionRulesTests` | Cross-rule precedence for read/write/audit |
| `ContractDrivenWorkflowTests` | End-to-end promotion: transcript → fixture → capability |
| `ControlPointInventoryServiceTests` | SQLite-backed device inventory lifecycle |
| `ImageTruthClassificationTests` | Per-image truth classification |
| `ImageTruthServiceTests` | Image-sweep service against synthetic fixtures |
| `LanBoundTokenGateTests` | Host-aware bearer-token middleware |
| `LiveTopGroupFixtureTests` | Live-proven top-group fixtures |
| `NvrLayerTests` | NVR playback/search indexing |
| `OnvifImagingControlAdapterTimeoutTests` | 5 timeout regression tests |
| `OperatorRuntimeRepairTests` | Operator-flow repair paths |
| `RunningRecordingEqualityTests` | Value-equality for RunningRecording record |
| `SemanticTrustServiceTests` | Trust decisions + audit log |
| `TrustHardeningWorkflowTests` | Combined trust + contract verification |
| `TypedSettingsAndProbeWorkflowTests` | Apply-batch typed settings + persistence verification |
