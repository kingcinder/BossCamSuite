# Transport Failover, Connectivity Watchdog, and DI Cycle Fix — Report

> **Date:** July 30, 2026
> **Branch:** `main`
> **Commits:** `8ba4a38` (camera stability system) → `9e113a0` (DI cycle + test repair pass)

---

## 1. Transport failover chain

`TransportFailoverService` (`src/BossCam.Core/Services/TransportFailoverService.cs`)
is the aggressive transport-resolution engine. Given a device, it tries every known
source URL in priority order and returns the first one that actually responds.

### Fallback chain (priority order)

| # | Transport | Pattern / notes |
|---|---|---|
| 1 | RTSP main stream | `ch0_0.264` or URL ending `/11`, metadata `stream=main` |
| 2 | RTSP sub stream | `ch0_1.264` or `/12`, metadata `stream=sub` |
| 3 | ONVIF RTSP | discovered profiles (metadata `stream=main` preferred) |
| 4 | RTSP over HTTP | HTTP-tunneled RTSP |
| 5 | Bubble FLV live HTTP | `FlvOverHttp` / `BubbleFlv` |
| 6 | RTMP | when present |
| 7 | Snapshot JPEG pump | `LanRest` with `kind=snapshot` (last-resort still image) |
| 8 | P2P tunnels | eSee Juan P2P / Kp2p / LinkVision |

Each transport is probed with a short timeout (`ProbeTimeout = 4s`) so a dead camera
never blocks the chain for more than a few seconds. If **all** transports fail,
`ProbeFallbackSourcesAsync` tries three hard-coded fallback URLs directly
(RTSP main, RTSP sub, snapshot JPEG) against the device IP.

### How the broker and failover cooperate

- `TransportBroker.GetSourcesAsync` enumerates sources from all `IVideoTransportAdapter`s.
- If the adapters yield **zero** sources, the broker falls back to
  `TransportFailoverService.ResolveBestSourceAsync`.
- `ResolveBestSourceAsync` itself probes candidates obtained from `TransportBroker`,
  then falls back to direct URL probing.

### DI cycle fix (the reason for lazy resolution)

`TransportBroker` and `TransportFailoverService` originally **injected each other**
through their constructors:

```
TransportBroker → TransportFailoverService → TransportBroker → …
```

Both are registered as singletons, so DI's `ValidateOnBuild` (used by the E2E
`WebApplicationFactory` host) threw `CallSiteFactory.CreateConstructorCallSite` /
`ValidateService` failures at host startup — **all 100 E2E tests failed** as a result.

**Fix (`RuntimeServices.cs`):** `TransportBroker` now takes `IServiceProvider?` and
resolves `TransportFailoverService` lazily via `GetService<T>()` only when the
fallback branch is actually reached. This breaks the construction-time cycle while
preserving the fallback behavior.

**Bonus repair — infinite recursion:** the lazy resolution exposed a latent
infinite-recursion bug: `GetSourcesAsync → ResolveBestSourceAsync → GetSourcesAsync → …`
for a device with an IP but no discoverable sources. A per-execution-context
`AsyncLocal<bool> _inFailoverFallback` reentrancy guard suppresses the nested
failover entry so the chain terminates at `ProbeFallbackSourcesAsync` with an
empty/fallback result instead of a stack overflow.

### Connectivity watchdog & diagnostics

- `ConnectivityWatchdogWorker` (`src/BossCam.Service/Hosted/`) periodically checks
  each registered device, writes a `DeviceConnectivitySnapshot`
  (`Healthy / Degraded / Offline / Unknown`) to the store, and attempts reconnect
  (`AttemptReconnectAsync`) when a device drops.
- `ConnectionDiagnosticService` produces per-device diagnostic summaries with
  transport-level probe results and reconnect actions.
- API surface: `/api/devices/{id}/network/recovery`, connectivity endpoints, and
  SPA connectivity indicators (features panel / live tiles).

> Note: RTSP health checks are TCP :554 only — reachable ≠ streamable. Recording
> still prefers the proven high-res main source via `SelectHighResMainSource`
> (`RecordingService`), never silently shipping sub/snapshot as a "main" recording.

---

## 2. Recording resilience (R1 + R4)

`RecordingService` persists job state and reconciles it across restarts:

| Concern | Behavior |
|---|---|
| **Persist on start/stop** | `SaveRecordingJobsAsync` writes `id, deviceId, profileId, sourceUrl (redacted), outputDirectory, segmentPattern, segmentSeconds, processId, startedAt, stoppedAt, isRunning, mode` to SQLite `recording_jobs`. |
| **Startup reconcile** | `ReconcilePersistedJobsAsync` loads `isRunning=true` rows: alive OS PID → re-attach into the in-memory table (with a PID-reuse guard comparing process start time); dead PID / no PID → mark `IsRunning=false` + `StoppedAt=UtcNow`, persist, and SignalR `RecordingJobStopped`. |
| **Stall watchdog** | `CheckStalledJobsAsync(stallTimeoutSeconds, autoRestart, ct)` detects no segment growth (mtime/size under the job's `OutputDirectory`/`SegmentPattern`) for the stall window; stops the job, persists, broadcasts; optionally restarts once per profile (`StallAutoRestart`). `0` disables. |
| **Lifecycle worker** | `RecordingLifecycleWorker` (hosted service) runs reconcile on boot (after `RecordingStartupReconcileDelaySeconds`), then periodic housekeeping + index refresh + stall checks at `RecordingHousekeepingMinutes`. |
| **Audio** | Direct FFmpeg pipeline maps audio (`-map 0:a:0? -c:a copy`) when the source has it; snapshot pipeline remains video-only by design. |

Unit coverage: `tests/BossCam.Tests/RecordingResilienceTests.cs` (dead PID, live PID
re-attach, no PID, stall-stop-and-persist, fresh-segment no-stall, timeout-zero
disabled) — all using a real SQLite store and real child processes.

---

## 3. New regression tests

`tests/BossCam.Tests/DependencyInjectionCycleTests.cs` locks down both repairs:

1. **`Full_Container_Builds_With_ValidateOnBuild_No_Circular_Dependency`**
   Builds the full production container (`AddLogging` + host-level
   `IBossCamEventBroadcaster` + `IHostEnvironment` + `AddBossCamInfrastructure` +
   `AddBossCamCore`) with `ValidateOnBuild = ValidateScopes = true` and resolves
   `TransportBroker`, `TransportFailoverService`, `RecordingService`,
   `LiveStreamService`. This test **fails fast if any future DI cycle or
   unresolvable constructor is introduced** (the exact class of failure that
   previously broke all 100 E2E tests at host startup).
2. **`TransportBroker_With_Failover_Does_Not_Recurse_For_Sourceless_Device`**
   A device with an IP (192.0.2.1 — RFC 5737 TEST-NET, fast-fail) and no adapter
   sources; asserts `GetSourcesAsync` returns an empty list instead of recursing
   to stack overflow. This pins the `AsyncLocal` reentrancy guard.

---

## 4. Verification

| Suite | Command | Result |
|---|---|---|
| Release build | `dotnet build BossCamSuite.Linux.sln -c Release` | ✅ 0 warnings / 0 errors |
| Unit tests | `dotnet test tests/BossCam.Tests -c Release` | ✅ **150/150** |
| Avalonia unit tests | `dotnet test src/BossCam.Desktop.Avalonia.Tests -c Release` | ✅ **17/17** |
| E2E (offline) | `BOSSCAM_E2E_LIVE=0 dotnet test tests/BossCam.E2E -c Release` | ✅ **100/100** |
| E2E (live) | `dotnet test tests/BossCam.E2E -c Release` | ✅ **100/100** |
| SPA build | `cd src/BossCam.ManagementUI && npm run build` | ✅ clean (favicon preserved via `public/`) |

Additional repairs in this pass:

- **Avalonia test project restructure** — `BossCam.Desktop.Avalonia.Tests` moved to
  its own directory with a **unique `.sln` GUID** (it previously *duplicated*
  `BossCam.E2E`'s GUID), eliminating shared-`obj/` fragility and a nested
  `Directory.Build.props` that shadowed the root one.
- **favicon durability** — favicon moved to `src/BossCam.ManagementUI/public/`
  so Vite's `emptyOutDir` no longer wipes `wwwroot/favicon.svg` on rebuilds.
- **Stale asset test fix** — `LanBoundAuthE2ETests` discovered a real `wwwroot/assets`
  file at test time instead of asserting the long-gone `/app.js`.
- **`FindRepoRoot` sln-name fix** — `OperatorRuntimeRepairTests` now accepts
  `BossCamSuite.Linux.sln` (the repo renamed away from `BossCamSuite.sln`).
