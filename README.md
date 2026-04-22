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
