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
- typed normalization layer and firmware-scoped capability promotion

## Run

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
