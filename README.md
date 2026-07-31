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

## Operator Configuration (`BossCam:` appsettings keys)

Two `BossCam:` keys in `src/BossCam.Service/appsettings.json` (or your
systemd/`appsettings.Linux.json` override) govern security- and
topology-sensitive behavior. Both default to **empty** and are opt-in.

### `BossCam:FirmwareAllowedDirectories`

Directory allow-list for firmware files. Both the **SPA** (`FirmwarePanel`
"Register" → `api.firmwareRegister(filePath)` → `POST /api/firmware/register`)
and the **Avalonia GUI** (Firmware section → `FirmwareViewModel.RegisterFirmwareAsync`)
let an operator submit a firmware path that is then uploaded to a camera's
CGI. Instead of accepting any existing file the caller names (an exfiltration-
by-proxy vector), the service only accepts files that resolve inside a
configured firmware root:

- If `FirmwareAllowedDirectories` is **empty**, the only allowed root is
  `BossCam:FirmwareArtifactDirectory` (default `~/.local/share/BossCamSuite/firmware`).
- When set, the list **replaces** `FirmwareArtifactDirectory` as the allowed
  roots — include it too if you want the default root to stay accepted.
- Containment is segment-aware (`/opt/firmware-evil` cannot masquerade as
  inside `/opt/firmware`) and cross-drive paths are rejected.

```json
{
  "BossCam": {
    "FirmwareArtifactDirectory": "/var/bosscam/firmware",
    "FirmwareAllowedDirectories": [
      "/var/bosscam/firmware",
      "/opt/vendor-fw"
    ]
  }
}
```

With this config, an operator can point either UI's firmware register at
`/var/bosscam/firmware/NVR50_8.1.8.bin` or `/opt/vendor-fw/DH_IPC-HFW.bin`;
anything outside those roots is rejected (`Firmware upload rejected: ...`).

### `BossCam:AegonLanDevices`

Config-driven camera list for the one-shot **Aegon bulk import** batch. The
historic hardcoded home-LAN topology (real IPs + camera labels) was removed
from the repo; the batch now registers whatever is listed here. Both the
**SPA** (Devices → Aegon bulk import → `api.registerAegonLan(lorex, wvc)` →
`POST /api/devices/register-aegon-lan`) and the **Avalonia GUI** (Devices
section → Aegon bulk import → `DevicesViewModel` →
`RegisterAegonLanAsync(lorexPassword, wvcPassword)`) surface the same batch.

```json
{
  "BossCam": {
    "AegonLanDevices": [
      { "IpAddress": "192.168.1.20", "Port": 80,     "LoginName": "admin", "Name": "Driveway", "HardwareModel": "5523-W" },
      { "IpAddress": "192.168.1.21", "Port": 8899,   "LoginName": "admin", "Name": "Porch",    "HardwareModel": "W5C" }
    ]
  }
}
```

Entry fields: `IpAddress` (required; entries without one are skipped),
`Port` (default `80`; recorded-port-first → `:80` fallback still applies),
`LoginName` (default `admin`), `Name`, and `HardwareModel`. The optional
per-call `lorexPassword` / `wvcPassword` are matched to each entry by
`HardwareModel` — a model containing **`W5C`** gets the WVC password, one
containing **`Lorex`** gets the Lorex password; other models register
passwordless. If `AegonLanDevices` is empty (the default), the batch returns
`[]` and logs a warning pointing here — add entries to enable it.

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

## Windows-Only Features (unavailable on this Linux edition)

The following capabilities require Windows-native binaries (DLLs) and are **not available** on this Linux/Ubuntu edition:

| Feature | Requires | Windows-only because |
|---------|----------|---------------------|
| **WPF Desktop app** | `src/BossCam.Desktop/` | Avalonia replaced WPF on Linux; see `src/BossCam.Desktop.Avalonia/` for the cross-platform equivalent |
| **IPCamSuite import provider** | `C:\Program Files\IPCamSuite\MAINSET.INI` | INI-file parser reads the Windows OEM install directory; degrades to empty result set on Linux |
| **EseeCloud import provider** | `C:\Program Files (x86)\EseeCloud\cms_data.db` | SQLite database reader for the Windows EseeCloud client; degrades to empty result set on Linux |
| **NativeFallbackAdapter** | `NetSdk.dll`, `EseeCloud P2P` DLLs | NativeBridge probes for Windows OEM DLLs via P/Invoke; `NativeInteropProbe` returns zero results on Linux |
| **DPAPI password cipher** | Windows Data Protection API | `CompositePasswordCipher` falls back to AES-GCM keyfile (`~/.local/share/BossCamSuite/secret.key`) |
| **Windows Service hosting** | `Microsoft.Extensions.Hosting.WindowsServices` | `Program.cs` falls back to `UseSystemd()` on Linux |

All other features (recordings, live streaming, probe runner, SignalR real-time events, REST API, Svelte SPA, ONVIF discovery) work identically on both platforms.

---

## Password Security Model

Device passwords are handled in a three-layer security model:

1. **In-memory (plaintext):** `DeviceIdentity.Password` is available for camera HTTP Basic auth. Marked `[JsonIgnore]` — never serialized to disk or transmitted over SignalR/Swagger.
2. **At-rest (encrypted):** `DeviceIdentity.PasswordCiphertext` stores an AES-GCM encrypted blob (Linux) or DPAPI-protected blob (Windows). Written by `SqliteApplicationStore` on each save, decrypted back to `Password` on each load.
3. **Over-the-wire (SignalR):** The `PasswordCiphertext` is encrypted and requires the local host keyfile (`secret.key`) to decrypt. While theoretically safe to transmit, the SPA does not use this field — consumers should rely on `Password` (in-memory only).

The keyfile at `~/.local/share/BossCamSuite/secret.key` is created with `0600` permissions on first cipher use. Protect this file the same way you would an SSH private key.

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

> **Note for Docker users:** The container uses the AES-GCM keyfile cipher. The `secret.key` is generated inside the container on first use — mount `/home/app/.local/share/BossCamSuite/` as a volume to persist it across container restarts.

---

## Svelte Management UI (development)

> **Primary operator console.** The Svelte 5 SPA is the suite's **primary UI**: it is
> served automatically by the service at `http://127.0.0.1:5317/`, requires nothing
> but a browser, and carries the full operator surface (live views, features apply,
> image/stream/network settings, recordings + clip export, highlights, storage
> paths, firmware). The Avalonia desktop app is a companion native frontend over the
> same HTTP API — see the
> [UI parity matrix](#ui-parity-matrix-spa--avalonia) below.

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

### Connectivity health semantics

- **HTTP / snapshot reachability** uses recorded-port-first → `:80` fallback
  (`NetSdkPortCandidates`): discovery can record an ONVIF/media port while the NetSDK REST
  surface listens on 80, so a 5523-W is still reported reachable when `:80` answers.
- **RTSP health means playable, not just TCP-open.** The connectivity watchdog, diagnostics,
  and transport failover probe RTSP with an `OPTIONS` handshake (`RtspProbe`) — a bare TCP
  connect on `:554` only proves *something* is listening, which is not a recordable/live
  stream. A peer that answers `RTSP/1.x` is up; a silent or non-RTSP listener is not.
- **Live preview vs recording audio:** live multi-view streams are video-only by design
  (`-an` keeps the low-latency transcode cheap); recordings route through
  `DirectFfmpegRecordingPipeline`, which maps audio (`-map 0:a:0? -c:a copy`). The two argvs
  deliberately differ.

---

## systemd Install

```bash
sudo ./scripts/install-systemd.sh
sudo systemctl status bosscam
```

---

## Avalonia Desktop App (standalone GUI)

A standalone native desktop frontend is available at `src/BossCam.Desktop.Avalonia/` using [Avalonia UI](https://www.avaloniaui.net/) 11.1. It wraps **every feature the suite offers** behind a single window and talks to the local `BossCam.Service` instance over HTTP.

### Sections

| Section | What it wraps |
|---------|---------------|
| **Live View** | Live snapshot stream of the selected camera, identity info, snapshot save |
| **Dashboard** | Health, recording jobs, connectivity snapshot at a glance |
| **Devices** | Browse, discover, register, and manage cameras (LAN auth, Aegon bulk import) |
| **Features** | Firmware toggles/sliders/enums: probe → write-verify → typed apply, expert override gating |
| **Recordings** | Start/stop continuous recording, reconcile jobs, segment index + clip export |
| **Highlights** | Highlight board selection |
| **Playback** | SD-card NVR playback search (host download of clips) |
| **Diagnostics** | Audit log, endpoint validation transcripts, probe sessions |
| **Firmware** | Firmware catalog, capability profiles, persistence verification |
| **Connectivity** | Transport failover chain: health, diagnose, reconnect per device |
| **Storage** | Storage root paths and config |

### Explainer popups

Every clickable button, input, selectable row, and **static menu title** carries an explainer popup (`InfoExplainer.Explanation` attached property). Hover or Tab-focus any control and a styled popup appears describing exactly what it does and what it is for. The popup is non-interactive, so it never steals pointer events.

### Run from source

**First restore requires internet access** — Avalonia 11.1 has ~15 transitive NuGet dependencies (~70 MB total):

```bash
dotnet restore src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj
dotnet run --project src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.csproj
```

The service must be running first (`dotnet run --project src/BossCam.Service/BossCam.Service.csproj`, or the installed systemd unit).

### System-wide install (traditional installation process)

```bash
sudo ./scripts/install-bosscam-gui.sh
```

The installer follows a conventional Linux installation flow:

1. Publishes the service (`Release`) and installs it to `/opt/bosscam` as a **systemd unit** (`bosscam.service`, auto-start on boot, `Restart=on-failure`).
2. Publishes the native GUI and installs it to `/opt/bosscam-gui`.
3. Installs a **launcher** (`/opt/bosscam-gui/launch-bosscam.sh`) that starts the service if needed, then opens the GUI.
4. Installs a **`.desktop` entry + SVG icon** so the app appears in the application menu.

Optional env vars: `BOSSCAM_PREFIX`, `BOSSCAM_GUI_PREFIX`, `BOSSCAM_SERVICE_USER`, `BOSSCAM_SKIP_SERVICE=1` (GUI only).

```bash
# Launch
/opt/bosscam-gui/launch-bosscam.sh          # or the app-menu entry "BossCamSuite"
# Service health / logs
systemctl status bosscam
journalctl -u bosscam -f

# Uninstall (data under ~/.local/share/BossCamSuite is preserved)
sudo ./scripts/uninstall-bosscam-gui.sh
# To also purge camera data:
BOSSCAM_PURGE_DATA=1 sudo ./scripts/uninstall-bosscam-gui.sh
```

### Tests

Unit + ViewModel tests live in `src/BossCam.Desktop.Avalonia.Tests/` (currently **42 tests** covering every section ViewModel, the shared device-selection sync, typed-apply request shape, and expert-override gating).

```bash
dotnet test src/BossCam.Desktop.Avalonia.Tests/BossCam.Desktop.Avalonia.Tests.csproj -c Release
```

---

## UI parity matrix (SPA ↔ Avalonia)

The Svelte SPA is the **primary operator console** (served at the service root,
`http://127.0.0.1:5317/`, browser-only, no install). The Avalonia desktop app is a
companion native frontend that wraps the same local `BossCam.Service` HTTP API —
nothing the SPA or GUI does is unique to either surface at the backend level.

The matrix below documents **Features apply** and **recordings / clip export**
parity (the two workflows the July 31 review focused on).

### Features apply

| Capability | SPA (primary) | Avalonia desktop |
|---|---|---|
| Control-point inventory | ✅ `FeaturesPanel` (features tab) | ✅ `FeaturesViewModel` (Features section) |
| Quick Probe (normalize + probe) | ✅ | ✅ |
| Toggle apply → typed settings | ✅ `applyToggle` → `api.applyTypedField` | ✅ `FeatureControlRow.ApplyAsync` → `ApplyTypedFieldAsync` |
| Slider apply | ✅ `applySlider` | ✅ |
| Enum/dropdown apply | ⚠️ `applyEnum` wired, but the eligible-widget filter admits Toggle/Slider only | ✅ `Dropdown` widget |
| Numeric / text inputs | ❌ not interactive (falls back to “no interactive control”) | ✅ `NumericInput` / `TextInput` widgets |
| Apply-batch | ✅ client `applyTypedBatch` | ✅ `ApplyTypedBatchAsync` |
| Expert-override gating | ✅ per-item + global reveal | ✅ section-level `ExpertOverride` switch |
| Write-verify gating (only `Writable` enabled) | ✅ | ✅ (`IsEnabled` gate) |
| Editors seeded from live camera values | ✅ | ✅ |
| In-flight apply feedback | ✅ spinner + toast | ✅ `IsApplying` + status text |

### Recordings & clip export

| Capability | SPA (primary) | Avalonia desktop |
|---|---|---|
| Start selected / start-all / stop-all / stop-job | ✅ `RecordPanel` | ✅ `RecordingsViewModel` |
| Index refresh + segment listing | ✅ | ✅ |
| Clip export (device + time window + path) | ✅ `exportClip` → `api.recordingExport` | ✅ `ExportClipAsync` |
| Re-encode fallback surfaced | ✅ (`reEncoded` in result) | ✅ (`ReEncoded` in result) |
| Download exported clip | ✅ inline `recordingDownloadUrl` link | ⚠️ API client exposes `GetRecordingDownloadUrl`; Recordings section reports the output path (Playback section hosts clip downloads) |
| Housekeeping / reconcile / stall-check | ❌ not exposed in the SPA | ✅ dedicated buttons (`🧹 Housekeeping`, `♻ Reconcile`, `🛑 Stall Check`) |

> Both surfaces call the same REST routes and consume the same `WriteResult` /
> `ClipExportResult` payloads; the GUI additionally renders explainer popups on
> every control. Where a behavior differs (numeric/text editors, housekeeping /
> reconcile / stall-check, and the clip-download link are GUI-only today), it is a
> UI-surface choice, not an API gap — the SPA `api.ts` and the Avalonia
> `IBossCamApiClient` are thin clients over the same `BossCam.Service` endpoints.

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
│   ├── install-bosscam-gui.sh       # system-wide install: /opt + systemd + .desktop
│   ├── uninstall-bosscam-gui.sh
│   ├── start-bosscam-ubuntu.sh
│   ├── start-bosscam-linux.sh
│   └── run-exhaustive-ubuntu-e2e.sh
├── src/
│   ├── BossCam.Service/          # ASP.NET Core API host
│   ├── BossCam.ManagementUI/     # Svelte 5 web operator console
│   ├── BossCam.Core/             # Business logic & services
│   ├── BossCam.Infrastructure/   # SQLite, discovery, control adapters
│   ├── BossCam.Contracts/        # Shared DTOs & models
│   ├── BossCam.Desktop.Avalonia/ # Cross-platform desktop app (Avalonia UI)
│   ├── BossCam.ProbeRunner/      # CLI probe tool
│   └── BossCam.NativeBridge/     # Native DLL interop
├── tests/
│   ├── BossCam.Tests/            # Unit tests (28 test classes)
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
| `CameraStabilityTests` | Connectivity enums/snapshots, diagnostic report roundtrips, failover null-IP, high-res source selection |
| `CompositeInteractionRulesTests` | Cross-rule precedence for read/write/audit |
| `ContractDrivenWorkflowTests` | End-to-end promotion: transcript → fixture → capability |
| `ControlPointInventoryServiceTests` | SQLite-backed device inventory lifecycle |
| `CoreServicePortFallbackTests` | Recorded-port-first → `:80` fallback in watchdog/diagnostics/`BuildSnapshotUrl` |
| `DependencyInjectionCycleTests` | `TransportBroker` ↔ failover DI-cycle + reentrancy regressions |
| `HttpAdapterPortFallbackTests` | HTTP control-plane port fallback + digest asymmetry |
| `ImageTruthClassificationTests` | Per-image truth classification |
| `ImageTruthServiceTests` | Image-sweep service against synthetic fixtures |
| `LanBoundTokenGateTests` | Host-aware bearer-token middleware |
| `LiveTopGroupFixtureTests` | Live-proven top-group fixtures |
| `NvrLayerTests` | NVR playback/search indexing |
| `OnvifImagingControlAdapterTimeoutTests` | 5 timeout regression tests |
| `OperatorRuntimeRepairTests` | Operator-flow repair paths |
| `RecordingResilienceTests` | Recording start/stop/stall/reconcile process-lifetime resilience |
| `RunningRecordingEqualityTests` | Value-equality for RunningRecording record |
| `RtspPlayabilityTests` | RTSP `OPTIONS` handshake probe (health semantics) |
| `SemanticTrustServiceTests` | Trust decisions + audit log |
| `SnapshotConsumerProbeTests` | Rank-ordered snapshot probing for recording + highlight-board tiles |
| `SqlIdentifierMapTests` | Store-table → identifier-map SQL injection guard |
| `TrustHardeningWorkflowTests` | Combined trust + contract verification |
| `TypedSettingsAndProbeWorkflowTests` | Apply-batch typed settings + persistence verification |
| `UnknownFirmwareCapabilityPromotionTests` | Firmware-scoped capability promotion from contract truth |
| `VideoAdapterPortFallbackTests` | `:80` fallback snapshot/bubble descriptor emission |
