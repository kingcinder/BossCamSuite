# Discovery & ONVIF Audit Pass — Report

> **Date:** July 31, 2026
> **Branch:** `main`
> **Scope:** deep audit of the discovery pipeline and the ONVIF instantiation/control
> path (5523-W discovery, WS-Discovery, `OnvifImagingControlAdapter`), plus a
> defense-in-depth redaction seam. All findings were confirmed against the code,
> fixed in order, and pinned by `tests/BossCam.Tests/DiscoveryAndOnvifAuditTests.cs`.

---

## 0. The audit

A reviewer went deep into `DiscoveryProviders.cs`, `DiscoveryCoordinator`, the ONVIF
WS-Discovery client, and `MultiBrandTransportAdapters.cs`. Findings clustered into
five areas plus a residual password-redaction seam. **Every finding was verified
against the live code before any fix was written.**

| Cluster | Finding (verified) | Severity |
|---|---|---|
| 0 | `SettingsService.WriteAsync`'s `SnapshotBeforeWrite` bypassed `RedactSnapshot` at one call site | Defense-in-depth seam |
| 1 | Merge/dedupe keyed on bare IP — DHCP renumber fragments identities; IP reuse merges foreign hosts into camera slots | 🔴 identity |
| 1 | `SubnetScanDiscoveryProvider` ran unconditionally despite its "fallback" doc; accepted **any** HTTP response as an IPC camera | 🔴 inventory pollution |
| 2 | ONVIF WS-Discovery: single wildcard socket (multi-homed blind), single non-retried send, hardcoded MessageID, no `NetworkVideoTransmitter` filter, `xAddrs.First()` blind pick | 🔴 missed/false devices |
| 3 | `OnvifImagingControlAdapter.ApplyAsync` **never read `plan.Payload`** — every toggle sent the identical `Exposure.Mode=MANUAL` stub and reported success | 🔴 writes the wrong setting |
| 3 | `ReadAsync`/`SnapshotAsync` only called `GetDeviceInformation` — no `GetImagingSettings`/`GetProfiles` | 🔴 plumbing without payload |
| 3 | Discovered XAddrs stored in `Metadata["xaddrs"]` were never consumed; all ONVIF calls guessed fixed ports × fixed paths | High |
| 4 | SOAP responses parsed with regex — `&amp;` in RTSP query strings never decoded (`channel=1&subtype=0` broke) | High |
| 4 | Read-side SOAP ignored `IsSuccessStatusCode` — a 401 HTML page was indistinguishable from "zero profiles" | Medium |
| 5 | Auth was HTTP Basic only — no WS-Security `UsernameToken` (mandated by the ONVIF spec for SOAP-level auth) | Medium |

---

## 1. Password redaction seam (defense-in-depth)

**Files:** `src/BossCam.Core/Services/RuntimeServices.cs`

`SettingsService.WriteAsync`'s `SnapshotBeforeWrite` path called
`store.SaveSettingsSnapshotAsync(beforeSnapshot, …)` directly, bypassing the
`RedactSnapshot` applied on the `ReadAsync` boundary. Not exploitable (the only
adapter that embeds a password-shaped field — `OwnedRemoteCommandAdapter` — is fixed
at the source) but the boundary was not uniform.

**Fix:** the pre-write snapshot is now routed through `RedactSnapshot` before
persisting, and `finalResult.Response`/`PreWriteValue`/`PostWriteValue` are redacted
via `SensitiveDataRedactor` before the result is returned to the API caller and
written to the audit log. Every snapshot-persisting and result-echoing call site now
shares the same redaction layer.

**Test:** `SettingsService_WriteAsync_Persists_Redacted_SnapshotBeforeWrite` — an
adapter that echoes a secret-bearing payload in its snapshot; asserts the persisted
snapshot never contains the plaintext secret.

---

## 2. Discovery identity redesign (5523-W)

**Files:** `src/BossCam.Core/Services/DiscoveryAndProtocolServices.cs`,
`src/BossCam.Infrastructure/Persistence/SqliteApplicationStore.cs`,
`src/BossCam.Infrastructure/Discovery/DiscoveryProviders.cs`

### 2.1 MAC-first merge + dedupe keys

`DiscoveryCoordinator.BuildMergeKey` was keyed on the bare `IpAddress`. Two failure
modes:

1. A 5523-W whose DHCP lease renews to a new address became a "brand-new device" —
   losing its credentials, capability map, and settings history.
2. A camera that goes offline and has its old IP handed to a laptop/phone would
   silently inherit the camera's identity and credentials.

**Fix — key on durable identity:**

```
mac:{MacAddress}      → when MAC captured (HiChip, SubnetScan via deviceInfo, ONVIF via scopes, manual registration)
ip:{IpAddress}        → fallback when no MAC
deviceId:{DeviceId}   → fallback
esee:{EseeId|Guid}    → last resort
```

The store's `BuildDedupeKey` (SQLite `devices.dedupe_key UNIQUE`) was updated to the
same MAC-first policy so persistence and in-memory merge agree.

### 2.2 MAC capture on every ingest path

MAC-first keying only works if every path that discovers/registers a camera actually
populates `MacAddress`:

| Ingest path | MAC source | Status |
|---|---|---|
| HiChip multicast | `MAC`/`hwaddr` header | already captured |
| SubnetScan | `mac`/`macAddress` from accepted `deviceInfo` JSON body | **now captured** (was missing — fragmented from HiChip copies) |
| ONVIF WS-Discovery | `onvif://www.onvif.org/mac/…` in Scopes | **now captured** via `ExtractMacFromScopes` |
| Manual registration | `macAddress`/`mac` from `deviceInfo` | **now captured on the property** (was only `Metadata["macAddress"]`) |

Without this, the same physical camera discovered by two providers would split into
`mac:` vs `ip:` identities — the exact fragmentation the redesign was meant to kill.

**Tests:** same-MAC/different-IP merges into one identity with credentials
preserved; same-IP/different-MAC stays two identities; ONVIF scopes MAC extraction.

---

## 3. Subnet scan — true fallback + real acceptance bar

**Files:** `src/BossCam.Core/Abstractions/Interfaces.cs`,
`src/BossCam.Core/Services/DiscoveryAndProtocolServices.cs`,
`src/BossCam.Infrastructure/Discovery/DiscoveryProviders.cs`,
`src/BossCam.Service/ApiDevicesEndpoints.cs`,
`src/BossCam.ManagementUI/src/lib/api.ts` + `Sidebar.svelte`

### 3.1 Fallback-only execution

`SubnetScanDiscoveryProvider` was registered like any other provider and the
coordinator ran **every** provider on every cycle — so each discovery pass did a full
`/24 × 6 ports` sweep (~1,500 probes) regardless of whether multicast found anything,
contradicting the provider's own "fallback when multicast yields no results" doc.

**Fix:**

- New marker interface `ISubnetScanDiscoveryProvider` with a transient
  `SubnetRangeOverride` property.
- The coordinator partitions providers into passive vs subnet. Passive
  (multicast/broadcast) runs first; the subnet sweep runs **only** when passive
  yielded zero devices **or** the caller explicitly requested a range scan.
- `RunAsync(string? ipRangeOverride, …)` sets the override on the provider for the
  pass and clears it in `finally`.
- The SPA "Scan subnet" button now posts `{ ipRangeOverride: "auto" }` to
  `/api/devices/discover`, forcing the sweep; `auto` scans all local `/24`s, a CIDR
  (`10.0.0.0/24`) restricts to that subnet. This gives the operator the explicit
  trigger the docs always promised.

**Tests:** subnet skipped when passive finds a device; fires when passive finds
nothing; explicit range forces the sweep and delivers/clears the override.

### 3.2 Acceptance bar: 200 + NetSDK-shaped body

`TryProbeAsync` previously accepted **any** HTTP response (`GET
/NetSDK/System/deviceInfo`) and set `DeviceType = "IPC"` unconditionally — so a
printer, NAS, router admin panel, or Docker web UI answering on 80/8080/8000/8888
was pulled into inventory labeled as a camera.

**Fix:** the probe now requires:

1. `response.IsSuccessStatusCode` (a 404/401/403 is **not** a camera), and
2. `LooksLikeNetSdkDeviceInfo(body)` — the body parses as a JSON object carrying at
   least one NetSDK-specific key: `serial`, `model`, `deviceName`, `deviceID`,
   `deviceId`, `mac`, `macAddress`, `firmware`.

The generic `name` key was deliberately **excluded** — any JSON-returning web
service can answer `{"name": "…"}` and would defeat the filter.

**Test:** `[Theory]` — accepts serial/model/deviceName bodies, rejects
`{"name": …}`, arrays, HTML, and empty bodies.

---

## 4. ONVIF WS-Discovery rewrite

**File:** `src/BossCam.Infrastructure/Discovery/DiscoveryProviders.cs`

The old client did `new UdpClient(0)` (single wildcard socket — the OS picks one
default-route interface, so multi-homed hosts silently find nothing on other NICs),
fired one non-retried Probe with a hardcoded `uuid:00000000-…` MessageID, accepted
any responder (printers included), and committed to `xAddrs.Split(' ').First()`.

**Fix — all five audit items:**

| Item | Before | After |
|---|---|---|
| Interface scoping | single wildcard socket | per local interface (`DiscoveryHelpers.GetLocalIpv4Addresses()`), like HiChip |
| Reliability | one UDP send | **3 Probes with jittered spacing** (multicast is lossy by design) |
| MessageID | hardcoded constant | **fresh `urn:uuid:{Guid:D}` per probe** — relays/proxies dedupe by MessageID; a static ID can silently suppress repeat scans |
| Response validation | any responder accepted | **`NetworkVideoTransmitter` required** in `Types` or `Scopes` (printers rejected) |
| XAddrs | `.First()` blind pick | **tries every XAddr**, first syntactically valid wins; falls back to the responder address |

Parsing uses `XDocument.Parse` (the same approach the audit praised elsewhere).
The `Metadata["xaddrs"]` value is the winning device-service URL — now actually
consumed (see §5.4).

**Tests:** accepts `NetworkVideoTransmitter` ProbeMatch; rejects printer Types;
tries every XAddr (first unparsable → second valid wins); responder-address
fallback; scope-MAC extraction.

---

## 5. ONVIF instantiation & toggle manipulation

**File:** `src/BossCam.Infrastructure/Video/MultiBrandTransportAdapters.cs`

### 5.1 Real field → SOAP write mapping (the 🔴 fix)

The old `OnvifImagingControlAdapter.ApplyAsync` **never read `plan.Payload`**. Every
toggle request sent the identical hardcoded `Exposure.Mode=MANUAL` SOAP call and
reported `Success = true` — actively flipping cameras into manual exposure as an
unintended side effect, and reporting "saved" for whatever the operator actually
changed.

**Fix — `BuildImagingSettingsElement(fieldKey, value)`** maps a BossCam field to the
ONVIF `tt:` imaging element that actually carries it:

| Field | SOAP element |
|---|---|
| `brightness` | `tt:Brightness` |
| `contrast` | `tt:Contrast` |
| `saturation` | `tt:ColorSaturation` |
| `sharpness` | `tt:Sharpness` |
| `gamma` | `tt:Gamma` |
| `exposure` | `tt:Exposure/tt:Mode` (MANUAL/AUTO) |
| `awb` / `whitebalance` | `tt:WhiteBalance/tt:Mode` |
| `wdr` | `tt:WideDynamicRange/tt:Mode` |
| `daynight` / `ircut` / `irmode` | `tt:IrCutFilter/tt:Mode` (day→OFF, night→ON, auto→AUTO) |

Scalars are clamped to the **signed ONVIF range `-100..100`** (the old 0–100 clamp
rejected negative brightness/contrast that `BuildImageGroup` legitimately reads back).
`ResolveFieldKey` prefers the trailing contract-key segment; `ExtractFieldValue`
tolerates `Level`/`Mode`/`$.*`-suffixed payload keys.

**Unmapped fields fail loudly** — no more silent stub: a field with no mapping (e.g.
`mirror`, `resolution`) returns `Success=false` with `"no mapped SetImagingSettings
element"` **before** any network call. The mapping check is proven network-free by a
test that throws on any HTTP request.

### 5.2 Real reads

`ReadAsync` now resolves endpoints, then builds three real groups:

- **Device** — `GetDeviceInformation` (manufacturer/model/firmware/serial)
- **Video** — `GetProfiles` (profile tokens, resolution, frame rate)
- **Image** — `GetImagingSettings` (via a `VideoSourceToken` resolved from
  `GetProfiles`' `VideoSourceConfiguration`/`SourceToken`) — brightness, contrast,
  saturation, sharpness, gamma, exposure mode, WDR, day/night, AWB

Write path resolves the same `VideoSourceToken` before issuing `SetImagingSettings`
with `<img:ForcePersistence>true</img:ForcePersistence>`.

### 5.3 SOAP parsing + status checks

- **`ExtractTag`/`Extract`** now parse with `XDocument` instead of regex, so
  XML-escaped `&amp;` decodes to `&` — Dahua/Hikvision-style
  `rtsp://…/cam/realmonitor?channel=1&subtype=0` URIs survive the round-trip.
- **Read-side SOAP** (`SoapAsync`, `PostSoapAsync`, `ApplyAsync` write) now checks
  `response.IsSuccessStatusCode` and returns `null`/failure on non-success. A 401
  HTML error page is no longer indistinguishable from "zero profiles".

### 5.4 Consumed discovered XAddrs

`BuildDeviceServiceCandidates(device, …)` puts the WS-Discovery XAddr **first** (it
is the authoritative device-service URL; compliant devices may host services at
arbitrary paths/ports on that address), then brand-guessed per-port candidates as a
fast-path fallback. `MultiBrandHighResTransportAdapter.DiscoverOnvifStreamsAsync`
and the imaging adapter both consume it. The audit's "discovered address is never
used" finding is closed.

---

## 6. WS-Security UsernameToken (SOAP-level auth)

**File:** `src/BossCam.Infrastructure/Video/MultiBrandTransportAdapters.cs`
(new internal static `OnvifWsse`)

ONVIF mandates WS-Security `UsernameToken` (nonce-salted `PasswordDigest`) for
SOAP-level authentication; the previous client sent HTTP Basic only. Strict or
enterprise Profile S/T stacks can reject or ignore SOAP calls carrying only
transport-layer auth.

**Fix:** a `wsse:Security` header is injected into **all four** ONVIF SOAP envelopes
(stream-discovery `SoapAsync`, imaging `PostSoapAsync`, `SetImagingSettings`, and
`SystemReboot`):

- `PasswordDigest = Base64(SHA1(rawNonce + Created + Password))` — raw nonce bytes
  concatenated with the ISO-8601 `Created` timestamp and the plaintext password, per
  the WS-Security UsernameToken profile.
- Fresh 16-byte nonce and `Created` per call; Basic auth is retained alongside as a
  compatibility fast-path for consumer cameras.

**Tests:** deterministic nonce pins the exact SHA-1 digest wire format; header
carries username/nonce/digest and never the plaintext password.

---

## 7. Verification

| Suite | Command | Result |
|---|---|---|
| Release build | `dotnet build BossCamSuite.Linux.sln -c Release` | ✅ 0 warnings / 0 errors |
| Full unit suite | `dotnet test tests/BossCam.Tests -c Release` | ✅ **255/255** |
| New audit tests | `DiscoveryAndOnvifAuditTests` filter | ✅ **27/27** |
| Constrained suites | `TrustHardeningWorkflowTests`, `OnvifImagingControlAdapterTimeoutTests` | ✅ pass |

Two independent code reviews were run after implementation; both confirmed the pass
and their three follow-up findings (subnet/ONVIF/registration MAC capture, dashed
MessageID, signed imaging clamp) were folded in and re-reviewed with no remaining
critical issues.
