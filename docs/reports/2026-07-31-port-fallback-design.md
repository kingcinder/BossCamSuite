# 2026-07-31 NetSDK REST port-fallback design

## The problem

Discovery records the port a camera answers during brand detection. On 5523-W units
(firmware `3.6.103.5721106`) that recorded port is frequently an **ONVIF/media port**
(`8888` Dahua-media / `8899` WVC), while the actual **NetSDK REST control and snapshot
surface listens on `80`** — live-verified: `deviceInfo` and `snapShot` return 200 on
`:80` and transport-fail on the recorded ONVIF port.

Before this pass, every HTTP caller used `device.Port` blindly:

```csharp
var port = device.Port <= 0 ? 80 : device.Port;   // wrong for port-8888 devices
```

A 5523-W registered through ONVIF discovery therefore had every snapshot, control-plane
read, and settings write pointed at a dead port.

## The rule

Shared helper (single definition of the fallback contract):

```csharp
// src/BossCam.Core/Utilities/NetSdkPortCandidates.cs
public static int[] For(int port)
    => port > 0 && port != 80 ? new[] { port, 80 } : new[] { port > 0 ? port : 80 };
```

- **Recorded port first**, then `80` as fallback — only when the recorded port is valid
  and non-80.
- Port `80`, `0`, or negative ⇒ single-element list (never probe twice for the common case).
- **Never fall back on an HTTP response.** A response — even `401` — is authoritative for
  that port (the camera answered; the auth/semantic result is real). Fallback only fires
  on a **transport-level failure** (no HTTP response at all).

### Digest-retry asymmetry (pinned by tests)

`HttpControlAdapterBase.SendAsync` runs a digest/credential-cache retry on the recorded
port when Basic returns `401`. That retry is the terminal action for the port — it does
**not** cascade into the `:80` fallback. Consequence: a 5523-W that requires digest on
`:80` but transport-fails on the recorded port is covered (the `:80` fallback fires Basic
first); a device that answers `401` on the recorded port but would accept Basic on `:80`
is not (an intentional, documented trade-off — a `401` means the recorded port is the
right surface, just wrong credentials).

## Where the fallback is applied

| Surface | Location | Behavior |
|---------|----------|----------|
| Control-plane HTTP | `HttpControlAdapters.cs` `SendAsync` | recorded port → `:80` on transport failure |
| Live snapshot pump | `LiveStreamService.cs` | recorded port → `:80`, per candidate path |
| Snapshot endpoint | `ApiDevicesStreamingEndpoints.cs` | recorded port → `:80`, per candidate path |
| Save-snapshot endpoint | `ApiStorageEndpoints.cs` | recorded port → `:80`, per candidate path |
| Snapshot descriptors | `StreamDescriptorAdapter` | emits recorded-port descriptor **and** a rank-26 `:80` fallback descriptor |
| Bubble live descriptors | `BubbleFlvAdapter` | emits recorded-port **and** `:80` fallback main/sub descriptors |
| Stream discovery | `StreamDescriptorAdapter` | `/NetSDK/Stream/channel/0` probed across candidate ports |
| Last-resort failover | `TransportFailoverService.ProbeFallbackSourcesAsync` | snapshot fallback URL uses candidate ports (also fixes the old `ip:0` URL when `Port = 0`) |
| Connectivity watchdog | `ConnectivityWatchdogWorker` | deviceInfo + snapshot probes run via `NetSdkPortCandidates.AnyPortSucceedsAsync` (recorded → `:80`) |
| Diagnostics battery | `ConnectionDiagnosticService` | `http:deviceInfo` + `snapshot` probes try recorded port then `:80` |
| Last-resort record URL | `RecordingService.BuildSnapshotUrl` | port derived via `NetSdkPortCandidates.For(...)[0]` (normalizes `Port = 0`) |
| Recording snapshot pick | `RecordingService.ResolveSnapshotUrlAsync` (`StartAsync`) | probes snapshot-kind descriptors in rank order via `NetSdkPortCandidates.FirstReachableSnapshotAsync` — recorded port first, then :80 — JPEG-validated, only when the snapshot pipeline is selected |
| Highlight-board tiles | `HighlightBoardService.BuildTilesAsync` | tile `SnapshotUrl` (and snapshot-mode live fallback) probes snapshot-kind descriptors in rank order via `NetSdkPortCandidates.FirstReachableSnapshotAsync` |

Shared primitives:

- `NetSdkPortCandidates.AnyPortSucceedsAsync(recordedPort, probe, ct)` backs the watchdog and
diagnostic reachability loops — probe each candidate in recorded-first/:80 order and short-circuit
on the first success.
- `NetSdkPortCandidates.FirstReachableSnapshotAsync(factory, sources, ct)` returns the first
snapshot-kind descriptor whose URL actually serves a JPEG, probing candidates in ascending rank
order (recorded port first, then :80). It is the single-pick entry point for the recording
snapshot pipeline and the highlight-board tiles, so the rank-26 `:80` fallback descriptor is
genuinely consumed instead of `FirstOrDefault` pinning to the rank-25 (possibly dead) URL.

Consumers benefit end-to-end: `TransportFailoverService.ResolveBestSourceAsync` probes
ranked sources in order, so the rank-26 `:80` snapshot/bubble descriptors are genuinely
tried when the recorded-port candidate fails — and the same holds for snapshot recording
and highlight-board tiles through `FirstReachableSnapshotAsync`.

## Test coverage

### Unit — `HttpAdapterPortFallbackTests` (7 + 6 cases)

- Transport failure on recorded port → falls back to `:80` (both ports probed, in order).
- `Port = 80` / `Port = 0` → single `:80` probe.
- HTTP `200` and `404` on recorded port → **no** `:80` probe (authoritative rule).
- `401` on recorded port → digest retry on the same port, **no** `:80` probe (asymmetry).
- Both ports transport-fail → no response.
- Direct `NetSdkPortCandidates.For` matrix: `8888/8899/8080 → [port, 80]`, `80/0/-1 → [80]`.

### Unit — `CoreServicePortFallbackTests` (6 + 4 cases)

- `AnyPortSucceedsAsync`: recorded→:80 order, single probe for port 80, all-fail → false.
- `ConnectionDiagnosticService.DiagnoseAsync`: `http:deviceInfo` + `snapshot` fall back from a
  recorded ONVIF port to `:80` (both ports probed, in order).
- `ConnectivityWatchdogWorker`: `QuickHttpProbeAsync` + `QuickSnapshotProbeAsync` fall back from
  the recorded port to `:80`.
- `RecordingService.BuildSnapshotUrl`: `:8888` preserved for non-default, `80/0/-1` → `:80`.

### Unit — `SnapshotConsumerProbeTests` (7)

- `FirstReachableSnapshotAsync` prefers the recorded-port descriptor when it serves a JPEG
  (single probe, short-circuit).
- Transport failure on the recorded port → falls back to the `:80` descriptor (both probed, in
  order).
- Non-JPEG 200 (HTML) on the recorded port → not selected; `:80` JPEG wins.
- All candidates fail → null (both ports probed).
- No snapshot-kind descriptors → null, no HTTP.
- `RecordingService.ResolveSnapshotUrlAsync` falls back to `:80`; with no descriptors it falls
  back to `BuildSnapshotUrl` (recorded port preserved).
- `HighlightBoardService` tile `SnapshotUrl` resolves to the `:80` fallback for a dead recorded
  port (recorded-first contract asserted); repeated `GetStateAsync` refreshes do **not** re-probe
  (memoized 15s TTL), and a fully-offline camera's null result is memoized as null without a
  second probe. The tile probe is headers-only: a non-JPEG 2xx (HTML login page) is accepted
  without reading the body (pinned by a throwing-body-content test), while `requireJpeg: true`
  (recording path) still rejects non-JPEG 2xx and falls through to `:80`.

### Unit — `VideoAdapterPortFallbackTests` (5)

- `StreamDescriptorAdapter` emits a rank-26 `:80` snapshot fallback for a port-8888 device;
  a single descriptor for a port-80 device.
- `StreamDescriptorAdapter` stream discovery falls back to `:80` after recorded-port refusal.
- `BubbleFlvAdapter` emits `:80` fallback main/sub (4 descriptors) for port-8888; 2 for port-80.

### E2E — `SnapshotPortFallbackE2ETests` (2)

- **Positive (environment-gated):** fake HTTP responder on `127.0.0.1:80` serves a JPEG; a device
  registered with a *closed* ephemeral recorded port still gets `200 image/jpeg` from
  `/api/devices/{id}/snapshot`. Binding `:80` requires elevation, so it early-returns on
  unprivileged runners (same convention as the live-camera E2E tests).
- **Negative:** no server anywhere → `502 Bad Gateway`. Runs everywhere (loopback
  connection-refused is instant; nothing can legitimately serve a JPEG at a NetSDK path).

## Live evidence

- 5523-W `10.0.0.30` / `10.0.0.170` (firmware `3.6.103.5721106`): `deviceInfo` + `snapShot`
  return 200 on `:80`, transport-fail on the recorded ONVIF port — the exact scenario this
  design exists for.
- Previously broken: any discovery path that recorded a non-80 port produced dead
  snapshot/control URLs.

## Notes for future maintainers

- **Single-pick consumers probe now** — `RecordingService.ResolveSnapshotUrlAsync` and
  `HighlightBoardService.BuildTilesAsync` both call `NetSdkPortCandidates.FirstReachableSnapshotAsync`,
  so the rank-26 `:80` fallback descriptor is genuinely consumed (JPEG-validated, rank-ordered).
  The recording probe is lazy — it only fires when the snapshot pipeline is selected, never on the
  direct-RTSP path.
- **Tile-path probe cost is bounded** — `BuildTilesAsync` runs on every `GetStateAsync`/`Flip`/`Select`
  and probes snapshot candidates through a **per-device memoized** resolver
  (`ResolveTileSnapshotAsync`): 15s TTL cache per device + a tighter **2s per-probe bound** and a
  **headers-only reachability check** (`requireJpeg: false` — any 2xx counts, the JPEG body is never
  downloaded). The recording path keeps full JPEG validation (`requireJpeg: true`, 4s default). A
  *fully offline* camera therefore stalls at most one 2s timeout per candidate per TTL window, and
  repeated refreshes never re-probe it at all (the null result is also cached). Healthy cameras skip
  the duplicate probe too.

  **Accepted trade-off:** headers-only accepts *any* 2xx, so a recorded port that answers `200`
  with an HTML/web-UI page (instead of transport-failing) pins the tile to that non-JPEG URL and
  does **not** self-heal to `:80`. The live-verified 5523-W recorded ports transport-fail, so the
  fallback still fires there; the any-2xx rule is chosen over a `Content-Type: image/*` header
  check because budget cameras do not reliably set a correct Content-Type, and a false reject
  would drop the tile snapshot entirely. Recording (which feeds ffmpeg) never takes this path.
- Do **not** collapse the two-element port list into a single URL — the whole point is that
  consumers probe candidates in order (`TransportFailoverService` probes by rank; snapshot
  endpoints loop ports × paths).
- Do **not** add a `!=` relational pattern: C# relational patterns support only
  `<`, `>`, `<=`, `>=` (`device.Port is > 0 and != 80` does not compile). The helper uses
  the classic boolean form.
- The fallback target is a fixed `80` — it mirrors the NetSDK REST surface, not the ONVIF
  probe list (`BossCam:OnvifProbePorts`).
