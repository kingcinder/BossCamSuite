# PTZ Scoping Pass (2026-07-31)

**Punchlist item:** P4 — PTZ control is entirely unimplemented; the owner's Temu PTZ camera cannot
be panned/tilted/zoomed through BossCam at all. The punchlist required a scoping conversation
before any control code is written, and explicitly asked for a **WS-Discovery + GetCapabilities
fixture from the actual device** to decide ONVIF PTZ vs. proprietary CGI.

**Outcome of this pass:** a capture tool the operator runs against the live Temu camera
(`POST /api/diagnostics/onvif/ptz-capture`), a structured verdict that gates the decision, and a
recommendation below. **No PTZ control code was written** — per the punchlist, that stays gated on
the fixture evidence.

---

## 1. What the source-level review established (no device needed)

| Fact | Evidence |
|---|---|
| **Zero PTZ code exists anywhere** | `ContinuousMove`/`GotoPreset`/`AbsoluteMove`/`RelativeMove`/`GetConfigurations` — 0 matches in `src/`. Only a UI group label ("PTZ / Optics" in `ProbeSessionService.MapGroup`) and a NetSDK path hint (`/NetSDK/PTZ/channels` used only as an existence probe in `HttpControlAdapters`) reference PTZ. |
| **ONVIF PTZ would bolt straight onto existing machinery** | `MultiBrandTransportAdapters.cs` already ships WSSE (`OnvifWsse`, nonce-salted PasswordDigest), `PostSoapAsync` with HTTP-status checking, XDocument parsing, XAddr-first service resolution, and per-candidate timeout gating. A PTZ adapter is the same shape as `OnvifImagingControlAdapter`. |
| **`GetCapabilities` is never called** | No `GetCapabilities` SOAP body exists in the codebase. Nothing today can prove a camera advertises a PTZ service — the evidence gap this pass fills. |
| **Discovery already records the ONVIF surface** | `OnvifDiscoveryProvider` stores the WS-Discovery XAddr (device-service URL) + scopes + MAC in `Metadata`; the `xaddrs` key is consumed by `MultiBrandHighResTransportAdapter` and `OnvifImagingControlAdapter`. A captured fixture can therefore be tied to the WS-Discovery identity. |

## 2. The evidence capture tool (added this pass)

**Endpoint:** `POST /api/diagnostics/onvif/ptz-capture`

**Request:**
```json
{
  "deviceId": "…",            // preferred: uses stored credentials + discovered XAddr
  "ipAddress": "10.0.0.x",    // alternative: bare IP for an unenrolled camera
  "loginName": "admin",
  "password": "…"
}
```

**What it does (in order):**

1. Resolves the device (store lookup by `deviceId`, else a synthesized identity from `ipAddress`).
2. Builds device-service candidates — **discovered WS-Discovery XAddr first** (authoritative), then
   per-port `/onvif/device_service` guesses across `OnvifProbePorts` + recorded port.
3. POSTs `GetCapabilities` (WSSE + Basic, same envelope builder as the existing adapters) and reads
   the **PTZ service XAddr** from the response (`Capabilities/Ptz/XAddr`).
4. If a PTZ service is advertised, POSTs `GetConfigurations` to it and counts `PTZConfiguration`
   tokens.
5. Persists the raw SOAP bodies as `EndpointContractFixture` evidence (same `contract_fixtures`
   table the 5523-W fixture flows use) and echoes them back on the response so the operator can
   save them under `src/BossCam.Service/fixtures/<brand>/__ONVIF/` matching the 5523-W pattern.
6. Returns a structured verdict + message.

**Operator one-liner (with LAN token):**
```bash
curl -H "Authorization: Bearer $BOSSCAM_LAN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"ipAddress":"10.0.0.x","loginName":"admin","password":"…"}' \
  http://<host>:<port>/api/diagnostics/onvif/ptz-capture
```

Save the returned `capabilitiesXml`/`configurationsXml` as:
```
src/BossCam.Service/fixtures/temu/__ONVIF/get-capabilities.xml
src/BossCam.Service/fixtures/temu/__ONVIF/ptz-get-configurations.xml
```

## 3. Decision gate — what each verdict means

| Verdict | Meaning | Milestone decision |
|---|---|---|
| `PtzReady` | Capabilities advertises a PTZ service **and** ≥1 `PTZConfiguration` exists | **IN SCOPE.** Implement `OnvifPtzControlAdapter` with `ContinuousMove` / `Stop` / `GotoPreset` on the existing SOAP stack. |
| `NoPtzService` | Device answers ONVIF but advertises no PTZ service | **Out of scope for ONVIF.** Next lead is a proprietary CGI (Foscam-style `/decoder_control.cgi`, vendor `ptz.cgi`, etc.) — a separate small adapter requiring its own fixture/authorization evidence, and likely not worth it unless the operator confirms the exact protocol. |
| `PtzAdvertisedNoConfigs` | PTZ service exists but zero configurations | **Out of scope.** Non-functional PTZ stub — implementing against it would produce toggles that never move the camera. |
| `AuthFailure` | Device service 401/403 on every candidate | **Blocked on credentials.** Re-run with the correct admin password; PTZ scoping cannot proceed on bad auth. |
| `DeviceUnreachable` | No ONVIF device service answered | **Blocked on reachability.** Wi-Fi cameras are expected flaky; retry after connectivity stabilizes, or check the camera's ONVIF toggle. |
| `NoDevice` | Bad request (no `deviceId`/`ipAddress`) | N/A — fix the request. |

## 4. Implementation sketch IF the fixture says `PtzReady`

Bound to the existing architecture, sized from what already exists:

- **New adapter** `OnvifPtzControlAdapter` (`IControlAdapter`, Priority below
  `OnvifImagingControlAdapter`), same shape as the imaging adapter:
  - `CanHandleAsync`/`ProbeAsync` — reuse `BuildDeviceServiceCandidates` + `GetCapabilities` to
    assert the PTZ service XAddr; expose `SupportedEndpointPaths = ["/onvif/ptz_service"]`.
  - `ApplyAsync` — map a `WritePlan` to SOAP via a field→element table (mirroring
    `BuildImagingSettingsElement`): `pan`/`tilt` → `ContinuousMove` velocity vector,
    `zoom` → `ContinuousMove` zoom, `stop` → `Stop`, `gotoPreset` → `GotoPreset` (preset token from
    `GetPresets`). Unmapped fields fail loudly, exactly like the imaging write gate.
  - `ReadAsync`/`SnapshotAsync` — `GetConfigurations` + `GetStatus` (position), surfaced as a
    "PTZ" setting group.
- **Contract seed** `ptz.onvif.*` single-field contracts (same pattern as the `image.onvif.*`
  seeds) so the SPA Features panel renders the controls via the existing typed-apply pipeline.
- **SPA wiring** — the Features panel already renders write-verified control points generically;
  PTZ fields flowing through `TypedSettingsService` appear with no bespoke UI, per the earlier
  ONVIF-to-SPA pass.
- **Tests** — the `OnvifPtzCapabilityProbeTests` stub-HTTP pattern generalizes: canned
  GetCapabilities/GetConfigurations/GetPresets SOAP responses pin the request envelopes, and
  unmapped-field refusal is proven network-free.

**Risk note:** the Temu PTZ is a budget Wi-Fi unit; even when the fixture says `PtzReady`,
continuous-move UX should bound movement with a watchdog stop (a stuck `ContinuousMove` is the
classic cheap-camera failure mode).

## 5. Recommendation

**PTZ control is NOT in scope for the current milestone.** The current milestone is fleet
enrollability, live view, and continuous recording (the punchlist's own priority order: "live+record
is mandatory first; do not block enroll on PTZ"). PTZ is a fast follow-up — gated on one operator
action:

1. Run `POST /api/diagnostics/onvif/ptz-capture` against the Temu camera (one curl, above).
2. If verdict = `PtzReady` → commit the two fixture XML files under `fixtures/temu/__ONVIF/`, then
   the adapter implementation (sketch in §4) is a ~1-day, self-contained change.
3. If verdict = `NoPtzService` → log the camera's web login page / CGI endpoints as a follow-up
   research item; do **not** build ONVIF PTZ against a camera that doesn't advertise it.

The capture tool and its tests ship in this pass (no secrets in fixtures — the SOAP *responses*
never contain the password; only the request carries it, and it is not echoed).
