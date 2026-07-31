# BossCamSuite — Final Summary Report

> **Date:** July 30, 2026  
> **Repository:** `github.com/kingcinder/BossCamSuite`  
> **Branch:** `main`  
> **Commits:** `d4d9644` → `9e113a0` (9 commits)

---

## Overview

This report covers all 10 code review items identified during the thorough codebase analysis. Each item was addressed systematically over multiple refactoring passes. The project is a .NET 8 ASP.NET Core minimal API application with a Svelte SPA frontend, supporting IP camera discovery, control, recording, and playback.

---

## Item-by-Item Summary

| # | Item | Scope | Commit(s) | Status |
|---|---|---|---|---|
| **#1** | IHttpClientFactory | Replace 20+ `new HttpClient()` call sites with pooled factory | `16b86b4` | ✅ Complete |
| **#2** | Program.cs Size | Split ~1007-line Program.cs into 8 domain-specific files | `4468ed6` | ✅ Complete |
| **#3** | Logging Volume | Gate full payload/response body logging behind `LogLevel.Debug` | `90351a0` | ✅ Complete |
| **#4** | Error Handling | `ControlResult<T>` type created, `SendWithResultAsync` in adapter base | `16b86b4` | ✅ Complete |
| **#5** | Password Handling | `[JsonIgnore]` on `Password`, ciphertext persistence, README docs | `90351a0` | ✅ Complete |
| **#6** | ONVIF Surface | `SetImagingSettings` SOAP + `SystemReboot` in `OnvifImagingControlAdapter` | `16b86b4` | ✅ Complete |
| **#7** | TreatWarningsAsErrors | Enabled in `Directory.Build.props` (0 warnings across all projects) | `90351a0` | ✅ Complete |
| **#8** | SignalR Token Auth | `accessTokenFactory` from localStorage in SPA; `access_token` query param accepted on `/hub/` paths | `90351a0` | ✅ Complete |
| **#9** | Documentation | Windows-only features table + Password Security Model in README | `90351a0` | ✅ Complete |
| **#10** | Fixture/Evidence Policy | `DeleteContractFixturesAsync` + `CleanupAsync` + API endpoint | `16b86b4` | ✅ Complete |

---

## Detailed Changes

### #1 — IHttpClientFactory (Refactor)

**Files modified:** 10+ files across `Core`, `Infrastructure`, `Service`, `Tests`

Replaced 20+ `new HttpClient()` call sites across the entire stack:

| Class | Change |
|---|---|
| `HttpControlAdapterBase` | Added `IHttpClientFactory` parameter, used in `SendOnceAsync` for non-Digest auth |
| `LanDirectNetSdkRestAdapter` | Factory injected via primary constructor |
| `LanPrivateVendorHttpAdapter` | Factory injected via primary constructor |
| `DahuaLorexControlAdapter` | Factory injected, `CanHandleAsync` uses `CreateClient("probe")` |
| `OnvifImagingControlAdapter` | `CanHandleAsync`/`ProbeAsync` uses `CreateClient("onvif")` |
| `DeviceRegistrationService` | 3 `new HttpClient()` sites converted |
| `Program.cs` | Snapshot endpoint uses injected `IHttpClientFactory` |
| `MultiBrandHighResTransportAdapter` | `SoapAsync` still uses per-call handler for Digest auth (intentional) |

**Named clients registered:** `probe`, `snapshot`, `onvif`, `default`

### #2 — Program.cs Size (Refactor)

**Before:** 1,007 lines in a single file  
**After:** 9 files totaling 1,184 lines, with `Program.cs` at 241 lines

| File | Lines | Domain |
|---|---|---|
| `Program.cs` | 241 | Bootstrapping, middleware, SPA fallback, SignalR hub, call chain, record types |
| `ApiDevicesEndpoints.cs` | 131 | Device CRUD, discovery, registration, probe, settings, typed-settings |
| `ApiDevicesInsightsEndpoints.cs` | 165 | Semantic trust, constraints, image control, grouped-config, persistence, native-fallback |
| `ApiDevicesStreamingEndpoints.cs` | 166 | Sources, preview, snapshot, live TS/MJPEG/fMP4, live-info |
| `ApiStorageEndpoints.cs` | 181 | Media storage paths, save-snapshot (with helper functions) |
| `ApiRecordingsEndpoints.cs` | 98 | Recording profiles + highlight board |
| `ApiDiagnosticsEndpoints.cs` | 54 | Health, audit, transcripts, probe sessions, truth sweep |
| `ApiFirmwareContractsProtocolsEndpoints.cs` | 72 | Firmware, contracts, protocols |
| `ApiPlaybackEndpoints.cs` | 76 | All 10 NVR playback methods |

Each file is a `public static class` with a `MapXxxEndpoints(this WebApplication app)` extension method, chained in `Program.cs`:

```csharp
app.MapDevicesEndpoints()
   .MapDevicesStreamingEndpoints()
   .MapDevicesInsightsEndpoints()
   .MapRecordingsEndpoints()
   .MapStorageEndpoints()
   .MapDiagnosticsEndpoints()
   .MapFirmwareContractsProtocolsEndpoints()
   .MapPlaybackEndpoints();
```

### #3 — Logging Volume (Quick Win)

**Before:** Full request/response body payloads logged at `LogLevel.Information`  
**After:** Summary trace stays at `Information`; payload/response bodies gated behind `Logger.IsEnabled(LogLevel.Debug)`

```csharp
if (Logger.IsEnabled(LogLevel.Debug))
{
    Logger.LogDebug("HTTP response body. adapter={Adapter} status={Status} response={Response}", ...);
}
```

### #4 — ControlResult&lt;T&gt; (Error Handling)

**New type:** `ControlResult<T>` in `BossCam.Contracts`

```csharp
public sealed record ControlResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public int? HttpStatusCode { get; init; }
    public long? DurationMs { get; init; }

    public static ControlResult<T> Ok(T value, ...) => ...;
    public static ControlResult<T> Fail(string errorCode, ...) => ...;
    public static ControlResult<T> FromException(Exception ex, ...) => ...;
}
```

**New method on `HttpControlAdapterBase`:** `SendWithResultAsync` wraps `SendAsync` with structured error codes (`"no-response"`, `"semantic-failure"`, `"request-exception"`) and duration tracking.

### #5 — Password Handling (Quick Win)

- `DeviceIdentity.Password` is `[JsonIgnore]` — never serialized to disk
- At-rest shape uses `PasswordCiphertext` (AES-GCM keyfile on Linux, DPAPI on Windows)
- `IPasswordCipher` / `CompositePasswordCipher` injected via DI
- `ResolvePlaintextPassword` decrypts transparently on load
- Documented in README under "Password Security Model"

### #6 — ONVIF Imaging (Medium Refactor)

**Before:** `OnvifImagingControlAdapter.ApplyAsync` returned a stub message  
**After:** Real SOAP calls against ONVIF probe ports:

| Method | SOAP Action |
|---|---|
| `ApplyAsync` | `SetImagingSettings` on `/onvif/image_service` |
| `ExecuteMaintenanceAsync` | `SystemReboot` on `/onvif/device_service` |

Both use `httpClientFactory.CreateClient("onvif")` for pooled connections and `ProbeExceptionSwallow.RunAsync` for graceful port-scan failure handling.

### #7 — TreatWarningsAsErrors (Quick Win)

```xml
<!-- Directory.Build.props -->
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

All projects build with **0 errors, 0 warnings** across the entire solution.

### #8 — SignalR Token Auth (Quick Win)

**Client (Svelte SPA):** `accessTokenFactory` reads `LS_LAN_TOKEN` from `localStorage` on every connect/reconnect:

```typescript
const connection = new HubConnectionBuilder()
    .withUrl("/hub/bosscam", {
        accessTokenFactory: () => localStorage.getItem("LS_LAN_TOKEN") ?? ""
    })
    .build();
```

**Server (LanBoundTokenGate):** Accepts `access_token` query parameter for `/hub/` paths only (standard SignalR WebSocket negotiation flow).

### #9 — Documentation (Quick Win)

**README additions:**
- **Windows-Only Features** table — 6 capabilities documented (WPF Desktop, Windows Service, MediaFoundation, etc.)
- **Password Security Model** — cipher choice, keyfile path, at-rest encryption flow
- **Leaked Token Implications** — per-endpoint power assessment

### #10 — Fixture/Evidence Cleanup (Medium Refactor)

**Interface additions:**

| Interface | Method |
|---|---|
| `IApplicationStore` | `DeleteContractFixturesAsync(Guid? deviceId, int olderThanDays, int maxPerDevice, int maxTotal, CancellationToken)` → `Task<int>` |
| `IContractEvidenceService` | `CleanupAsync(int olderThanDays, int maxPerDevice, int maxTotal, CancellationToken)` → `Task<int>` |

**Implementation:** `SqliteApplicationStore.DeleteContractFixturesAsync` performs two-step cleanup:
1. Age-based: `DELETE ... WHERE captured_at < cutoff`
2. Excess-based: `DELETE ... ORDER BY captured_at ASC LIMIT excess`

**API endpoint:** `POST /api/contracts/fixtures/cleanup` with `ContractFixtureCleanupRequest` body (defaults: 90 days, 2000/device, 10000 total).

---

## E2E Test Suite Results

All **100 E2E tests pass** (offline `BOSSCAM_E2E_LIVE=0` **and** live mode):

| Test Suite | Tests | Status |
|---|---|---|
| `ApiRouteMatrixTests` | 46 | ✅ Passed |
| `LanAuthE2ETests` + `LanBound*E2ETests` | 24 | ✅ Passed |
| `LanBoundFailFastE2ETests` | 1 | ✅ Passed |
| `LinuxUiFeatureTests` | 11 | ✅ Passed |
| `LiveCameraExhaustiveTests` | 5 | ✅ Passed |
| `SimulatedLanCleanupTests` | 2 | ✅ Passed |
| `UbuntuPlatformAndStaticUiTests` | 11 | ✅ Passed |
| **Total** | **100** | **✅ All Passed** |

Unit suites: **BossCam.Tests 150/150** (incl. new `RecordingResilienceTests` and
`DependencyInjectionCycleTests`), **BossCam.Desktop.Avalonia.Tests 17/17**.

## Stability Pass (commits `8ba4a38` → `9e113a0`)

After the review items above, a full stability pass added camera-connectivity
resilience and closed the loop with regression tests:

| Area | Work |
|---|---|
| **Transport failover** | `TransportFailoverService` probes RTSP main → sub → ONVIF → HTTP/FLV → RTMP → snapshot → P2P in priority order with a 4s per-transport timeout; `TransportBroker` falls back to it when adapters yield nothing. |
| **DI cycle fix** | `TransportBroker` ↔ `TransportFailoverService` singleton circular dependency broke `ValidateOnBuild` at host startup (all 100 E2E tests failed). `TransportBroker` now resolves failover lazily via `IServiceProvider`; an `AsyncLocal` reentrancy guard also fixes a latent infinite-recursion (broker → failover → broker) for sourceless devices. |
| **Connectivity watchdog** | `ConnectivityWatchdogWorker` periodically snapshots device health (`Healthy/Degraded/Offline`), attempts reconnects, and broadcasts via SignalR; `ConnectionDiagnosticService` + `/api/devices/{id}/network/recovery` provide diagnosis. |
| **Recording R1/R4** | Jobs persisted on start/stop; `ReconcilePersistedJobsAsync` re-attaches live PIDs (with PID-reuse guard) or closes dead jobs; `CheckStalledJobsAsync` stops/optionally restarts stalled pipelines; `RecordingLifecycleWorker` owns reconcile + housekeeping + stall checks. |
| **Recording audio** | Direct FFmpeg pipeline maps audio (`-map 0:a:0? -c:a copy`); snapshot pipeline stays video-only by design. |
| **Avalonia test isolation** | `BossCam.Desktop.Avalonia.Tests` moved to its own directory with a **unique `.sln` GUID** (previously duplicated `BossCam.E2E`'s), removing shared-`obj/` fragility and a shadowing `Directory.Build.props`. |
| **favicon durability** | Moved to `ManagementUI/public/` so Vite `emptyOutDir` no longer wipes `wwwroot/favicon.svg`. |
| **Stale test repair** | `LanBoundAuthE2ETests` discovers real assets instead of `/app.js`; `OperatorRuntimeRepairTests` accepts `BossCamSuite.Linux.sln`. |

Full detail: `docs/reports/2026-07-30-transport-failover-di-fix-report.md`.

---

## Architecture After Refactoring

```
Program.cs (241 lines)
├── Builder setup
├── Middleware wiring (CORS, rate-limiter, LAN gate, Swagger)
├── app.MapDevicesEndpoints()              → ApiDevicesEndpoints.cs
├── app.MapDevicesStreamingEndpoints()     → ApiDevicesStreamingEndpoints.cs
├── app.MapDevicesInsightsEndpoints()      → ApiDevicesInsightsEndpoints.cs
├── app.MapRecordingsEndpoints()           → ApiRecordingsEndpoints.cs
├── app.MapStorageEndpoints()              → ApiStorageEndpoints.cs
├── app.MapDiagnosticsEndpoints()          → ApiDiagnosticsEndpoints.cs
├── app.MapFirmwareContractsProtocolsEndpoints() → ApiFirmwareContractsProtocolsEndpoints.cs
├── app.MapPlaybackEndpoints()             → ApiPlaybackEndpoints.cs
├── SPA fallback + SignalR hub
└── Record types + Program partial class

HttpControlAdapterBase
├── IHttpClientFactory injection (pooled connections)
├── SendWithResultAsync → ControlResult<T>
├── SendAsync → HttpAdapterResponse? (existing)
└── Conditional logging (Information summary / Debug bodies)

OnvifImagingControlAdapter
├── SetImagingSettings SOAP (per probe port)
└── SystemReboot SOAP (per probe port)

SqliteApplicationStore
├── DeleteContractFixturesAsync (age + excess)
├── IPasswordCipher encryption at rest
└── StoreTable enum-based SQL injection prevention
```

---

## Key Metrics

| Metric | Before | After |
|---|---|---|
| Program.cs lines | 1,007 | 241 |
| Endpoint files | 1 | 9 |
| `new HttpClient()` sites | 24 | 3 (intentional: Digest auth needs per-call handler) |
| Build warnings | Several | 0 (`TreatWarningsAsErrors=true`) |
| E2E tests passing | ~85/91 | **100/100** |
| Unit tests passing | — | **150/150** (BossCam.Tests) + **17/17** (Avalonia) |
| DI host startup | Circular-dependency crash | **Clean** (ValidateOnBuild regression test added) |
| Password exposure | Plaintext in serialized JSON | AES-GCM/DPAPI encrypted at rest |
| ONVIF ApplyAsync | Stub | Real `SetImagingSettings` SOAP |
| Fixture cleanup | None | Age-based + excess-limit deletion |
