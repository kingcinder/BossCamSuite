# 2026-08-09 — Factory-Default Unlock Draft (ONVIF SetSystemFactoryDefault on 10.0.0.169)

Status: **DRAFT — NOT EXECUTED. Destructive. Awaiting operator approval before any
request is sent.** This is the last-resort software route for `.169`: it resets the
device to factory state over the ONVIF plane (which we hold with `admin:admin`), in the
hope that wiping the config clears the dead cloud binding that keeps `/user/*.xml`
gated with `mesg="check in falied"`.

> ⚠ Read the risks (§3) before approving. A factory reset has **no rollback** and may
> take `.169` off its current IP.

---

## 1. Why this route exists

- The campaign (`docs/reports/2026-08-09-unlock-campaign-exhausted.md`) proved the 401
  on `.169` is a **ticket gate** (`$.Auth.ticket` set only by a cloud check-in whose
  account binding is dead: `error:3004 "no user to push"`).
- The **NetSDK `$.factoryDefault` expert field** exists in the contract catalog
  (`EndpointContractCatalogService.cs`, `DisruptionClass.FactoryReset`) — but it is
  behind the same gated NetSDK plane that rejects everything, so it is unusable.
- The **ONVIF plane is independent and open**: `admin:admin` verifies on
  `/onvif/device_service` (per `OnvifCredentialScanner`). A device-wide factory reset
  issued over ONVIF is the one software path that can touch device state we otherwise
  cannot reach.
- Precedent caveat: `SetUser` over ONVIF proved **inert** on the web plane earlier
  (planes are independent on this Anyka/5523-W firmware). A factory default *may* be
  device-wide — or may equally be scoped like `SetUser`. **Genuinely unknown until
  tried.** This draft is honest about that uncertainty.

---

## 2. The exact SOAP request

### 2.1 Endpoint, credentials, and headers

| Item | Value |
|------|-------|
| URL | `http://<DeviceServiceUrl-host:port>/onvif/device_service` — **do not hardcode :80**; read the host/port from the last successful scan's `DeviceServiceUrl` (the scanner's `BuildOnvifPorts` tries the recorded ONVIF/media port first — these units' ONVIF/media ports (8888/8899) differ from the NetSDK :80 surface per the port-fallback report). |
| Credential | `admin` / `admin` (verified working via WSSE + Basic) |
| Method | `POST` |
| Content-Type | `application/soap+xml; charset=utf-8; action="http://www.onvif.org/ver10/device/wsdl/SetSystemFactoryDefault"` |
| SOAPAction (legacy header, belt-and-suspenders) | `http://www.onvif.org/ver10/device/wsdl/SetSystemFactoryDefault` |
| Authorization | `Basic YWRtaW46YWRtaW4=` **plus** the WSSE header below (project pattern: WSSE digest + Basic fast-path together) |

### 2.2 WSSE UsernameToken (project-verified shape, `OnvifWsse.BuildSecurityHeader`)

```
PasswordDigest = Base64( SHA1( rawNonceBytes + Created + Password ) )
```
- `Nonce`: 16 random bytes, Base64-encoded (fresh per request — do not reuse)
- `Created`: UTC `yyyy-MM-dd'T'HH:mm:ss'Z'`
- `PasswordDigest`: SHA-1 over the **raw nonce bytes** ‖ `Created` ‖ `admin`, Base64

Worked example (deterministic values, matches the project's unit-test pinning):
```
nonce bytes (hex): 0102030405060708
Created:           2026-08-09T09:00:00Z
password:          admin
input: 0102030405060708 ‖ "2026-08-09T09:00:00Z" ‖ "admin"
SHA1  → 9ce241bfa1a1e9e51f5f1a2e4f47a1b4b2f4f2f3 (illustrative)
Base64 → <digest>
```

### 2.3 Full envelope — Hard variant (full wipe including network config)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
  <s:Header>
    <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
                   xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
      <wsse:UsernameToken>
        <wsse:Username>admin</wsse:Username>
        <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"><DIGEST></wsse:Password>
        <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"><NONCE_B64></wsse:Nonce>
        <wsu:Created>2026-08-09T09:00:00Z</wsu:Created>
      </wsse:UsernameToken>
    </wsse:Security>
  </s:Header>
  <s:Body>
    <tds:SetSystemFactoryDefault xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
      <tds:FactoryDefaultType>Hard</tds:FactoryDefaultType>
    </tds:SetSystemFactoryDefault>
  </s:Body>
</s:Envelope>
```

### 2.4 Soft variant (keeps network settings — see recommendation §3)

Identical envelope with only the body element changed:

```xml
  <s:Body>
    <tds:SetSystemFactoryDefault xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
      <tds:FactoryDefaultType>Soft</tds:FactoryDefaultType>
    </tds:SetSystemFactoryDefault>
  </s:Body>
```

### 2.5 curl reference (with computed values filled in)

```bash
curl -sS -i -m 15 \
  -H 'Content-Type: application/soap+xml; charset=utf-8; action="http://www.onvif.org/ver10/device/wsdl/SetSystemFactoryDefault"' \
  -H 'SOAPAction: "http://www.onvif.org/ver10/device/wsdl/SetSystemFactoryDefault"' \
  -H 'Authorization: Basic YWRtaW46YWRtaW4=' \
  --data-binary @set-factory-default-soft.xml \
  http://10.0.0.169:80/onvif/device_service
```

### 2.6 Expected responses

- **Success**: HTTP 200 with an (effectively empty) `tds:SetSystemFactoryDefaultResponse`
  body, then the camera reboots (typically 60–120 s of silence on all ports).
- **Fault** (common on cameras that reject the op): HTTP 500 with
  `tds:Fault><Reason>…Not Implemented…</Reason>` — means this firmware does **not**
  implement the ONVIF factory default and the route is dead (mirrors `SetUser` inertness).
- **401**: the WSSE/Basic pair failed — re-scan with `OnvifCredentialScanner` before
  retrying; never blind-retry with the same digest.

### 2.7 Reboot contingency (if no auto-reboot)

Many ONVIF stacks apply the factory default but do **not** auto-reboot. If 20–30 s
pass after a 200 with no port silence, issue a follow-up reboot:

```bash
curl -sS -i -m 15 \
  -H 'Content-Type: application/soap+xml; charset=utf-8; action="http://www.onvif.org/ver10/device/wsdl/Reboot"' \
  -H 'Authorization: Basic YWRtaW46YWRtaW4=' \
  --data-binary '<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Header><wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"><wsse:UsernameToken><wsse:Username>admin</wsse:Username><wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"><DIGEST></wsse:Password><wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"><NONCE_B64></wsse:Nonce><wsu:Created><CREATED></wsu:Created></wsse:UsernameToken></wsse:Security></s:Header><s:Body><tds:Reboot xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/></s:Body></s:Envelope>' \
  http://<DeviceServiceUrl-host:port>/onvif/device_service
```

### 2.8 Ready-to-use request file

Save the Soft envelope verbatim as `set-factory-default-soft.xml` (replace the three
WSSE placeholders):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
  <s:Header>
    <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
                   xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
      <wsse:UsernameToken>
        <wsse:Username>admin</wsse:Username>
        <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"><DIGEST></wsse:Password>
        <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"><NONCE_B64></wsse:Nonce>
        <wsu:Created><CREATED></wsu:Created>
      </wsse:UsernameToken>
    </wsse:Security>
  </s:Header>
  <s:Body>
    <tds:SetSystemFactoryDefault xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
      <tds:FactoryDefaultType>Soft</tds:FactoryDefaultType>
    </tds:SetSystemFactoryDefault>
  </s:Body>
</s:Envelope>
```

---

## 3. Risks — read before approving

| # | Risk | Severity | Mitigation / note |
|---|------|----------|-------------------|
| 1 | **No rollback.** Factory reset wipes users, image/network/PTZ config, and the cloud binding irreversibly. | HIGH | Operator must accept config loss. |
| 2 | **IP loss (Hard).** `Hard` resets network config → camera may drop 10.0.0.169 and appear on DHCP or a default 192.168.1.x address. | HIGH | Prefer **Soft** first (keeps network); escalate to Hard only if Soft fails to clear the gate. |
| 3 | **May not unlock at all.** `SetUser` precedent shows this firmware can scope ONVIF ops away from the web plane; factory default may be similarly inert. | MED | Treat as a probe: any 200 response is new information; a Fault closes the route. |
| 4 | **Reboot window.** `.169` is currently the fleet's only reachable camera (RTSP 554 open); it goes fully dark during reboot. | MED | Schedule during a window when .169 being down is acceptable; fleet monitor will log the gap. |
| 5 | **Credential change post-reset.** Factory state restores vendor defaults (likely `admin:admin` or blank) — ONVIF re-scan is required before relying on any creds. | LOW | Built into re-enrollment (§4). |
| 6 | **Cloud re-pair side effect.** If the binding is truly wiped, the camera re-registers fresh with EseeCloud and may require app-side re-pairing (out of our control). | MED | Acceptable — an unbound camera is exactly the state we want (no dead binding to gate on). |
| 7 | **The core assumption may be wrong: the binding may be server-side.** The campaign's strongest evidence of deadness — `error:3004 "no user to push"` — is **returned by the EseeCloud server**, not the camera. If the bound-account state lives in the cloud account rather than camera NVRAM, a device-side factory reset cannot fix it. This is the plan's biggest logical weakness and the most likely reason it fails. | HIGH | Frame the run as a cheap probe: a 200 response teaches us whether the wipe even lands; the gate re-check after reboot is the verdict. |
| 8 | **Physical button is strictly more reliable.** If operator physical access exists at all, the camera's reset button achieves the same wipe with no SOAP risk (per the exhausted-campaign's remaining-routes table). | — | Only relevant when physical access is possible. |

**Recommendation:** attempt **Soft first** (keeps `.169` reachable, clears app-level
config and likely the binding), verify the gate; escalate to **Hard** only if the gate
is still closed after Soft. Both are one-shot — a second factory default after a failed
first is unlikely to behave differently, so each escalation is a deliberate decision.

**Before approving, weigh risk #7** (the binding may be server-side, making the whole
wipe moot) — and if physical access to the camera exists at all, the reset button is
more reliable than this probe.

---

## 4. Re-enrollment steps (run only after operator approval + a confirmed reset)

Existing tooling chain: `scripts/factory-reset-recovery.sh` already orchestrates
recovery → RTSP → enroll → record → watchdog. The steps below assume it, with the
ONVIF-specific parts called out.

1. **Pre-flight snapshot** (before sending anything): record `.169` gate state, RTSP,
   and note it in the fleet-monitor log (`/tmp/fleet-monitor-*.log`) so the gap is
   traceable.
2. **Send the Soft request** (§2.5), capture the HTTP response verbatim for the record.
3. **Wait out the reboot** (60–120 s): poll with `scripts/camera-recovery.sh 10.0.0.169`
   until `deviceInfo` answers again.
4. **Re-scan ONVIF credentials** with `OnvifCredentialScanner` (API:
   `POST /api/devices/onvif/credential-scan` with `ipAddress: 10.0.0.169`) — the factory
   default restored vendor defaults; confirm `admin:admin` (or blank) before proceeding.
5. **Verify the gate** on `/user/user_list.xml`:
   - **OPEN** → the wipe cleared the dead binding. Proceed to enrollment.
   - **still `"check in falied"`** → the gate is firmware/hardware-level, not config.
     Stop. Only the physical/credential routes of the exhausted-campaign report remain.
6. **Enroll + record**: `scripts/factory-reset-recovery.sh --enroll --record --watchdog 10.0.0.169`
   (or via the UI). Set a real web-plane password immediately after enrollment.
7. **Verify live playback** (HEVC) and snapshot; confirm the recording job is running.
8. **Close the loop**: append the outcome (HTTP response, gate state, new creds) to
   `docs/reports/2026-08-09-unlock-campaign-exhausted.md` or a follow-up report.

**Abort criteria before the request is sent:** gate flips OPEN on its own (fleet
monitor will catch it), `.169` drops off the network for an unrelated reason, or the
operator decides the reboot window is unacceptable.

---

## 5. Deliverables for approval

- [ ] Operator approves **Soft** factory default on `10.0.0.169` (destructive, no rollback).
- [ ] Operator approves the reboot window (camera dark ~60–120 s).
- [ ] Operator confirms escalation to **Hard** is permitted if Soft leaves the gate closed.
- [ ] Post-run outcome appended to this file (§4 step 8) for the audit trail.
