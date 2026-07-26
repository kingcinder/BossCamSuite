# BossCamSuite

Windows-first control suite and VMS scaffold for the 5523-w camera family.

Implemented surfaces in this repository:
- LAN NETSDK REST control adapter
- IPCamSuite private HTTP/CGI adapter
- EseeCloud app import and remote-command envelope adapter
- discovery providers for HiChip multicast, DVR broadcast, and ONVIF WS-Discovery
- SQLite-backed local inventory, audit log, capability cache, protocol manifest store, endpoint validation store, transcript store, and firmware artifact catalog
- ASP.NET Core local service host and a WPF desktop shell
- ProbeRunner CLI with staged probe sessions, transcript bundle export, and resumable runs
- contract-driven typed normalization/apply layer for top groups (Video/Image, Network/Wireless, Users/Maintenance)
- endpoint contract catalog + transcript-to-fixture evidence promotion
- firmware-scoped capability promotion driven by contract truth + live evidence quality
- semantic write classification and sensitive-field audit redaction
- FFmpeg-backed recording orchestration + segment indexing + clip export API

## Run

### Ubuntu / Linux (fully supported)

Primary UI on Linux is the **web operator console** served by the service (WPF Desktop remains Windows-only).

```bash
# one-time deps
chmod +x scripts/*.sh
./scripts/install-ubuntu-deps.sh

# start service + open operator UI
./scripts/start-bosscam-ubuntu.sh
```

Then open **http://127.0.0.1:5317/** (or the printed URL).

Optional env vars:

| Variable | Meaning |
|----------|---------|
| `BOSSCAM_BIND` | Bind host (default `127.0.0.1`; use `0.0.0.0` for LAN) |
| `BOSSCAM_PORT` | Port (default `5317`) |
| `BOSSCAM_CAMERA_IPS` | Comma IPs to register instead of Aegon defaults |
| `BOSSCAM_LOREX_PASSWORD` / `BOSSCAM_WVC_PASSWORD` | Brand credentials |
| `BOSSCAM_FFMPEG_PATH` | ffmpeg binary |
| `BOSSCAM_OPEN_BROWSER` | `0` to skip `xdg-open` |
| `BOSSCAM_LAN_TOKEN` | LAN bearer token. Required when the service binds to a non-loopback address (e.g. `0.0.0.0`); recommended to be a 64-hex-char secret from `openssl rand -hex 32`. Without this var AND a non-loopback bind, the service refuses to start rather than expose `/api/*` and `/swagger/*` anonymously. Also accepted on loopback binds (in which case the gate stays passive but a startup warning is logged). |
| `BOSSCAM_BIND` | Host portion of `BossCam:LocalApiBaseUrl` (default `127.0.0.1`). Use `0.0.0.0` to expose on LAN. Setting `BOSSCAM_BIND=0.0.0.0` without `BOSSCAM_LAN_TOKEN` causes startup to fail. |
| `BOSSCAM_E2E_LIVE` | Set to `0` to make the exhaustive E2E suite skip LAN probes and the multicast discovery providers (HiChip/ONVIF/DvrBroadcast). Combined with `run-exhaustive-ubuntu-e2e.sh` it gives a clean offline CI run. |

### LAN Auth Token (host-aware gate)

Let `BossCam:LocalApiBaseUrl` (or env var `BossCam__LocalApiBaseUrl` / `BOSSCAM_BIND+http://...`) decide who can hit the API. The host-aware gate in `Program.cs` evaluates the bind on startup:

| Bind | `BOSSCAM_LAN_TOKEN` env var | `BossCam:LanAuthToken` config | Result |
|------|----------------------------|--------------------------------|--------|
| Loopback (`127.0.0.1`, `::1`, `localhost`) | (unset) | (unset) | No gate. Dev mode. |
| Loopback | (unset) | set | Gate engaged with the config value. |
| Loopback | set | (unset) | Gate *would* be engaged if you bind to LAN. A startup warning is logged: the token is loaded into memory but never required for loopback traffic. Rebind to a non-loopback host to actually enforce. |
| **Non-loopback (`0.0.0.0`, `192.168.x`, `10.x`, `::`)** | (unset) | (unset) | **Refuses to start.** Sets `InvalidOperationException` with the fix recipe. |
| Non-loopback | set | (unset) | Gate engaged with the env-var token. |
| Non-loopback | (unset) | set | Gate engaged with the config token. |
| Non-loopback | set | set | Env-var token wins. Config token is ignored. |

The point of the non-loopback fail-fast: the prior config-only gate relied on operator memory to flip `BossCam:LanAuthToken` whenever they widened the bind. The new gate makes the LAN-bind + no-token case *illegal at startup*, so `/api/*` and `/swagger/*` can never be exposed anonymously even if the operator forgets the token.

To expose the service on the LAN:

```bash
# 1. Generate a token (one line)
openssl rand -hex 32

# 2. Export it in the shell that starts the service. /scripts/start-bosscam-ubuntu.sh
#    reads it transparently via .NET configuration.
export BOSSCAM_LAN_TOKEN='<paste-the-token-here>'

# 3. Bind to LAN instead of loopback.
export BOSSCAM_BIND=0.0.0.0    # start-bosscam-ubuntu.sh sets BossCam__LocalApiBaseUrl from this

# 4. Start
./scripts/start-bosscam-ubuntu.sh
```

For systemd units, edit the `BossCam:LocalApiBaseUrl` line in `deploy/systemd/bosscam.service` (or set `Environment=BOSSCAM_LAN_TOKEN=...`) and reload with `sudo systemctl restart bosscam.service`. An `#EnvironmentFile=-/etc/bosscam/bosscam.env` line is included but commented out so operators can store the token outside the unit file.

The middleware accepts the token via either of two headers:

- `X-LAN-Token: <token>` (preferred; sent by the SPA automatically after the first window.prompt succeeds)
- `Authorization: Bearer <token>` (useful for `curl` and other non-browser clients)

Compare is constant-time via `CryptographicOperations.FixedTimeEquals`. Query-string tokens are intentionally rejected because they leak via referer / browser history / access logs.

Open paths (always accessible): `/api/health`, `/`, `/index.html`, `/app.js`, `/app.css`, `/favicon.svg`.

The CORS allowlist (`BossCam:AllowedOrigins`) defaults to empty in token mode — same-origin requests still work because browsers don't require CORS for them. Override per remote host:

```json
"BossCam": { "AllowedOrigins": [ "https://operator.lan.example" ] }
```

systemd install:

```bash
./scripts/install-systemd.sh
sudo systemctl status bosscam
```

Linux solution (no WPF):

```bash
dotnet build BossCamSuite.Linux.sln -c Release
dotnet test BossCamSuite.Linux.sln -c Release
```

#### Test index (    `tests/BossCam.Tests`, 16 files / 18 [Fact] classes — `BossCamSuiteTests.cs` houses 3 nested [Fact] classes: `ProtocolManifestProviderTests`, `ImportProviderTests`, `FirmwareArtifactAnalyzerTests`):

| Class | Purpose |
|-------|---------|
| `BossCamSuiteTests` | Protocol manifest provider, ImportProvider, FirmwareArtifactAnalyzer — happy paths + corruption recovery. |
| `BindAddressInspectorTests` | Classifies bind strings into loopback/LAN/IPv6/unspecified. Drives the bearer-token gate's startup guard. |
| `CompositeInteractionRulesTests` | Cross-rule precedence for read/write/audit interactions. |
| `ContractDrivenWorkflowTests` | End-to-end promotion: transcript → contract fixture → capability. |
| `ControlPointInventoryServiceTests` | SQLite-backed device inventory lifecycle. |
| `ImageTruthClassificationTests` | Per-image truth classification (wire-equivalent vs cosmetic). |
| `ImageTruthServiceTests` | Image-sweep service against synthetic fixtures. |
| `LanBoundTokenGateTests` | Host-aware bearer-token middleware: loopback-skip, constant-time compare, fail-fast. |
| `LiveTopGroupFixtureTests` | Live-proven top-group fixtures (Video/Image, Network/Wireless, Users). |
| `NvrLayerTests` | NVR playback/search indexing and stoppability. |
| `OnvifImagingControlAdapterTimeoutTests` | 5 regressions: brand-probe half-timeout, device-info full-timeout, ratio, linear over HttpTimeoutSeconds, 2-second floor. Shared `BlackholeListenerFixture` (`IClassFixture`). |
| `OperatorRuntimeRepairTests` | Operator-flow repair paths for corrupted runtime state. |
| `RunningRecordingEqualityTests` | Value-equality for `RunningRecording` record (the post-refactor 3-field tuple replacement). |
| `SemanticTrustServiceTests` | Trust decisions based on semantic-write classifier + audit log. |
| `TrustHardeningWorkflowTests` | Combined trust + contract verification workflow. |
| `TypedSettingsAndProbeWorkflowTests` | Apply-batch typed settings + persistence verification. |

Exhaustive E2E (unit + in-process HTTP matrix + simulated-LAN coverage):

```bash
# Offline run (no cameras needed). Multicast discovery is auto-skipped.
BOSSCAM_E2E_LIVE=0 ./scripts/run-exhaustive-ubuntu-e2e.sh

# Live run against the Aegon LAN cameras
./scripts/run-exhaustive-ubuntu-e2e.sh
# override targets:
BOSSCAM_E2E_IPS=10.0.0.30,10.0.0.170,10.0.0.228 BOSSCAM_E2E_LIVE=1 ./scripts/run-exhaustive-ubuntu-e2e.sh
```

Data lives under `~/.local/share/BossCamSuite/` (DB + recordings).

### Windows (desktop + service)

One-command launcher (build + service health wait + desktop):

```powershell
& .\scripts\Start-BossCamSuite.ps1
```

Optional: include safe read probe against known cameras:

```powershell
& .\scripts\Start-BossCamSuite.ps1 -RunProbe
```

Service API host:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\src\BossCam.Service\BossCam.Service.csproj
```

Desktop shell:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\src\BossCam.Desktop\BossCam.Desktop.csproj
```

> **WPF desktop-only / Linux path.** `BossCam.Desktop` targets `net8.0-windows` because WPF's `System.Windows` is wired to DirectX. The 40+ `async void` event handlers in `MainWindow.xaml.cs` and `MainWindow.Nvr.cs` are not a bug — that's the XAML code-behind signature WPF *mandates* for event handlers (`async void` propagates exceptions straight to the dispatcher). The long-term cross-platform port is Avalonia UI 11, which is API-compatible with WPF XAML and runs on net8.0; the event-handler signatures would port verbatim, including the `async void` pattern. A near-term shortcut is GtkSharp with hand-mapped XAML, but it loses the radichter-design surface. Linux operators today use the **web operator console** at `http://127.0.0.1:5317/` (the SPA in `src/BossCam.Service/wwwroot`), which is feature-equivalent for the common flows (registry, settings, contract promotion, NVR playback); WPF is only required for NVR live decoding on Windows.

Probe runner (known live 5523-w targets, safe read-only stage):

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\src\BossCam.ProbeRunner\BossCam.ProbeRunner.csproj -- --mode SafeReadOnly --device-ips 10.0.0.4,10.0.0.29,10.0.0.227 --resume true --export-dir .\artifacts --export-summary .\artifacts\probe-summary.json
```

Probe runner (single device, safe-write-verify stage):

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\src\BossCam.ProbeRunner\BossCam.ProbeRunner.csproj -- --mode SafeWriteVerify --device-ip 10.0.0.4 --resume true --include-persistence false --export-dir .\artifacts
```

Probe stage values:
- `InventoryOnly`
- `SafeReadOnly`
- `SafeWriteVerify`
- `NetworkImpacting`
- `RebootRequired`
- `ExpertFull`

The protocol evidence loaded by the runtime lives under `assets/protocols`.

## Contract/Evidence APIs

- `GET /api/contracts/endpoints`
- `GET /api/contracts/endpoints?deviceId=<guid>`
- `POST /api/contracts/fixtures/promote/<deviceId>`
  - body: `{ "exportRoot": "C:\\Users\\ceide\\Documents\\BossCamSuite\\artifacts" }`
- `GET /api/contracts/fixtures`
- `GET /api/contracts/fixtures?deviceId=<guid>`

Typed settings APIs:
- `POST /api/devices/{id}/settings/typed/apply-batch`
- `GET /api/devices/{id}/persistence/eligible-fields`
- `POST /api/devices/{id}/persistence/verify-field`

## Contract/Evidence Storage

SQLite tables:
- `endpoint_contracts`
- `contract_fixtures`

Runtime fixture export:
- `<exportRoot>\\contracts\\<group>\\<firmware>\\*.json`

Regression fixtures in repo:
- `tests/BossCam.Tests/Fixtures/contracts/video_image/5523_w`
- `tests/BossCam.Tests/Fixtures/contracts/network_wireless/5523_w`
- `tests/BossCam.Tests/Fixtures/contracts/users_maintenance/5523_w`

## Recording APIs

- `POST /api/recordings/start`
- `POST /api/recordings/stop/{jobId}`
- `GET /api/recordings/jobs`
- `POST /api/recordings/reconcile`
- `POST /api/recordings/index/refresh`
- `GET /api/recordings/index`
- `POST /api/recordings/export`
- `POST /api/recordings/housekeeping`

## NVR Playback/Search APIs

- `POST /api/devices/{id}/playback/find-file`
- `POST /api/devices/{id}/playback/find-next-file`
- `POST /api/devices/{id}/playback/get-file-by-time`
- `POST /api/devices/{id}/playback/playback-by-time`
- `POST /api/devices/{id}/playback/find-close`
- `POST /api/devices/{id}/playback/playback-by-name`
- `POST /api/devices/{id}/playback/get-file-by-name`
- `POST /api/devices/{id}/playback/stop-get-file`
- `POST /api/devices/{id}/playback/playback-save-data`
- `POST /api/devices/{id}/playback/stop-playback-save`

## Grouped Config Re-test APIs

- `GET /api/devices/{id}/grouped-config/snapshots`
- `GET /api/devices/{id}/grouped-config/profiles`
- `GET /api/devices/{id}/grouped-config/retest-results`
- `POST /api/devices/{id}/grouped-config/retest-unsupported`
- `GET /api/grouped-config/sdk-field-catalog`
- `POST /api/devices/{id}/grouped-config/force-enumerate-sdk-fields`

Optional ffmpeg override:
- environment variable `BOSSCAM_FFMPEG_PATH`

Recording lifecycle worker:
- auto-start enabled recording profiles on service startup (`AutoStart=true`)
- periodic index refresh + retention housekeeping

Profile retention knobs:
- `RetentionDays` (delete old `.mp4` segments)
- `MaxStorageBytes` (cap storage and prune oldest first)

Native fallback assessment API:
- `GET /api/devices/{id}/native-fallback-assessment`

Native diagnostics now include:
- DLL loadability checks
- expected-export presence checks per known vendor library

Service tuning knobs (`BossCam` section in `appsettings.json`):
- `RecordingHousekeepingMinutes`
- `RecordingStartupReconcileDelaySeconds`
