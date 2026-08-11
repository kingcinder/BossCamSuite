# 2026-08-09 ONVIF `admin:admin` unlock + attack-vector matrix (5523-W fleet)

**Date:** 2026-08-09 · **Status:** VECTOR 1 **SUCCESS** (both live 5523-W units readable via ONVIF default creds on `:8888`); SetUser reset **protocol-accepted but functionally inert**; web-plane unlock still gated on .169/.29.

---

## 1. Executive summary

The most promising attack vector — **ONVIF default credentials** — paid off immediately. Both live 5523-W cameras (`10.0.0.169`, `10.0.0.227`) expose the ONVIF device service on **`:8888`** and answer a `GetDeviceInformation` / `GetUsers` / `GetProfiles` / `GetStreamUri` sweep with **`admin:admin`** (Basic auth), yielding full device identity, user enumeration, system log, media profiles, and live RTSP stream URIs. Live **HEVC 2560×1920 playback** was confirmed from `.227` with those credentials.

Two nuances shaped the rest of the work:

1. **ONVIF on these units does not actually validate credentials.** `GetUsers` returns the user list for *any* Basic pair (verified with `admin:garbage123`). "`admin:admin` works" is really "ONVIF answers to any pair" — so it is a read oracle, not an auth oracle.
2. **The ONVIF user store is separate from the web/NetSDK/RTSP planes.** A `SetUser` password reset is protocol-accepted (`<SetUserResponse/>`) but does not change the web/NetSDK/RTSP credential store: `.227`'s web plane remained **blank-password** (its pre-existing state) and `.169`'s web plane remained **locked to every credential tried**.

`.227` is now fully enrolled in BossCam with blank-password NetSDK and is **recording** (direct HEVC job `b5c3ef83-1a76-462e-a0fd-b9ea445f4348` was later superseded by snapshot-mode job `e38aee4c…` — see §8). `.169` and `.29` remain web-gated behind the EseeCloud check-in state (`check in falied`), which is the subject of the parallel MITM work.

---

## 2. Attack-vector matrix (statuses)

| # | Vector | Target surface | Status | Notes |
|---|---|---|---|---|
| 1 | **ONVIF default credentials** | `:8888/onvif/device_service` + `media_service` | ✅ **SUCCESS** | `admin:admin` grants full read control on both live 5523-W units; stream URIs + RTSP playback confirmed |
| 1b | ONVIF `SetUser` password reset | `:8888/onvif/device_service` | ⚠️ **Inert** | `<SetUserResponse/>` accepted on both, but operates on the unenforced ONVIF user store — web/NetSDK/RTSP stores do not follow |
| 2 | CGI `/user/*.xml` gate bypass | `:80/user/user_list.xml` etc. | ✗ **No bypass** | `check in falied` gate held against method fuzz (POST/PUT/OPTIONS), header spoofing (`X-Forwarded-For`, `X-Real-IP`, `Referer`, `X-Requested-With`), and empty-credential `Authorization`. Gate is driven by cloud check-in state, not HTTP auth |
| 3 | HiSilicon telnet backdoor (`OpenTelnet:OpenOnce` on `:9530`) | `:9530`, `:23` | ✗ **Not applicable** | Ports closed on all three live units; PoC (`hs-dvr-telnet.py`) retained for the offline units if they return with the backdoor service exposed |
| 4 | EseeCloud check-in MITM → gate open → add admin | `:19000` WS check-in + `/message/` HTTP chain | ⏸ **In progress** | Tooling fixed (per-camera counter cadence, 2026-08-09); run against `.29` executed but gate stayed closed — camera sends only the LITE 0x00 (32B) registration form under MITM, which is never adopted. `/message/` chain advanced for the first time on `.29` (see §7) |

Fleet as of 2026-08-09: `10.0.0.169` ("Driveway", 5523-W), `10.0.0.227` (5523-W), `10.0.0.29` (5523-W). Units `10.0.0.2–8` offline.

---

## 3. Vector 1 — ONVIF default-credential sweep (detail)

### 3.1 Discovery path

ONVIF `GetCapabilities` answers **unauthenticated** on `:8888` (no SOAP fault), which identified the device service endpoint. Authenticated calls (device info, users, profiles) return a SOAP **Fault** without credentials and succeed with `admin:admin`.

### 3.2 What `admin:admin` unlocked (both units)

| Call | Result |
|---|---|
| `GetDeviceInformation` | **GUANGZHOU 5523-W**, firmware **3.6.103.0** (.169; `.227` reports `3.6.103.5721106` via NetSDK) |
| `GetUsers` | `admin` / Administrator (both units) — see §5 for the credential-validation caveat |
| `GetSystemLog` | system log readable |
| `GetProfiles` | `PROFILE_000` |
| `GetStreamUri` | `rtsp://<ip>:554/ch0_0.264` |
| RTSP playback | **confirmed live**: ffprobe pulled **HEVC 2560×1920** from `.227` with `admin:admin` |

### 3.3 Bonus on `.227` — blank-password NetSDK

The NetSDK REST surface on `:80` answers to **`admin:` (blank password)** with full `deviceInfo` — serial, eseeID `4781634738`, MAC, firmware date. This is the "passwordless" plane BossCam's pipeline already leans on, and is why `.227` enrolled cleanly.

---

## 4. Exact auth payloads that worked

### 4.1 Working request envelope (Basic auth on ONVIF device service)

The scanner (and live probes) POST `application/soap+xml` to `http://<ip>:8888/onvif/device_service` with a WS-Security UsernameToken **and** a Basic `Authorization` header. The **Basic `admin:admin` pair is what the live curl probes used successfully**:

```bash
curl -s -u admin:admin -X POST \
  -H 'Content-Type: application/soap+xml' \
  --data '<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
  <s:Body>
    <tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
  </s:Body>
</s:Envelope>' \
  http://10.0.0.169:8888/onvif/device_service
```

- **Endpoint:** `http://<ip>:8888/onvif/device_service` (device service) and `http://<ip>:8888/onvif/media_service` (media service).
- **Auth header (worked):** `Authorization: Basic YWRtaW46YWRtaW4=` (base64 of `admin:admin`).
- **Content-Type:** `application/soap+xml`.

### 4.2 WSSE UsernameToken header (used by the scanner's `PostSoapAuthenticatedAsync`)

The production scanner (`src/BossCam.Infrastructure/Video/OnvifCredentialScanner.cs`, `OnvifWsse.BuildSecurityHeader`) sends both a WSSE UsernameToken **and** the Basic header. The WSSE header shape:

```xml
<wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
               xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
  <wsse:UsernameToken>
    <wsse:Username>admin</wsse:Username>
    <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">BASE64(SHA1(rawNonce || Created || password))</wsse:Password>
    <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">BASE64(16 random bytes)</wsse:Nonce>
    <wsu:Created>2026-08-09T00:00:00Z</wsu:Created>
  </wsse:UsernameToken>
</wsse:Security>
```

Digest detail (pinned by unit tests): `PasswordDigest = Base64( SHA1( rawNonceBytes || UTF8(Created) || UTF8(password) ) )` — the **raw nonce bytes**, not the base64 nonce string.

### 4.3 SOAP bodies used

GetCapabilities / GetDeviceInformation / GetProfiles bodies below are byte-exact from the scanner code; the GetStreamUri body is the **representative standard form** (the scanner's version and the live curl probe both resolved `PROFILE_000` → `rtsp://<ip>:554/ch0_0.264`).

```xml
<!-- GetCapabilities (unauthenticated, phase-1 manufacturer detection) -->
<tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl"><tds:Category>All</tds:Category></tds:GetCapabilities>

<!-- GetDeviceInformation -->
<tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>

<!-- GetProfiles (media service) -->
<trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>

<!-- GetStreamUri (media service) -->
<trt:GetStreamUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:ProfileToken>PROFILE_000</trt:ProfileToken>
  <trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>
</trt:GetStreamUri>
```

### 4.4 Credential sweep order (scanner's `GenericCredentials`)

`admin:admin` → `admin:12345` → `admin:123456` → `admin:password` → `admin:` → `service:service` → `operator:operator` → `root:root` → `user:user` → `Admin:Admin` → `admin:Admin123`, with vendor-specific pairs (wansview/esee: `admin:admin`, `admin:`, `admin:123456`) prepended when `GetCapabilities` reveals the manufacturer. Port candidates: recorded ONVIF/media port first, then `OnvifProbePorts`, then control/HTTP ports, then `80, 8080, 8000, 8899, 8888`.

---

## 5. SetUser reset experiment — protocol-accepted, functionally inert

Both cameras accepted the ONVIF `SetUser` reset (`<SetUserResponse/>`) setting `admin` → `BossCam2026!`. The full verification matrix exposed the store separation:

| Surface | 10.0.0.169 | 10.0.0.227 |
|---|---|---|
| ONVIF `:8888` GetUsers | ✅ responds (**any** Basic pair, incl. garbage) | ✅ responds (any pair) |
| NetSDK REST `:80` deviceInfo | ❌ 401 for every credential incl. new | ✅ `admin:` (blank) — unchanged |
| RTSP `:554` ch0_0.264 | ❌ 401 | ✅ any credential streams HEVC 2560×1920 |

Findings:

1. **ONVIF `GetUsers` on these cameras validates nothing** — it returned the user list for `admin:garbage123` on both units. The earlier "`admin:admin` works" is best read as "ONVIF answers to any Basic pair"; it is not a real auth oracle, so the `SetUser` reset can be neither confirmed nor denied through it.
2. **The web/NetSDK plane is a separate store** (eseecloud/NetSDK user DB): `.227`'s web password is still **blank** (pre-reset state), `.169`'s web plane rejects every password including the new one.
3. **`.227`'s RTSP plane accepts any credential** — blank, the new password, and `wrongpw123` all stream. It is the "passwordless" plane BossCam's pipeline relies on; the reset neither broke nor changed it.
4. **Recording unaffected** — job `b5c3ef83…` remained `running: True`, latest segment `hevc,2560,1920`.

**Bottom line:** the ONVIF `SetUser` reset is **protocol-accepted but functionally inert** on these 5523-W units. **2026-08-09 update: both cameras' ONVIF passwords have been reverted to `admin:admin` via SetUser** (`<tds:SetUserResponse/>` on both, `GetUsers` answering with `admin`/Administrator) so the discovered credential stays stable for future scans — moot functionally (the store is unenforced), but the canonical credential is restored.

---

## 6. Vector 2 — CGI `/user/*.xml` gate (no bypass)

The `/user/*.xml` gate (`check in falied`) withstood every mutation:

- **Method fuzz** — `POST`, `PUT`, `OPTIONS` on `user_list.xml` / `set_pass.xml`
- **Header spoofing** — `X-Forwarded-For`, `X-Real-IP`, `Referer`, `X-Requested-With`
- **Empty-credential `Authorization`** headers

The gate is driven by the camera's **EseeCloud check-in state** (`$.Auth.ticket` in `cgi_user.c`), not HTTP authentication — it only opens via the cloud-path vector (Vector 4). Same gate protects `set_pass.xml` (live-proven 2026-08-09: `set_pass.xml` returns `mesg="check in falied"` before any password validation). See `2026-08-09-controlled-verify-experiment-protocol.md` for the full controlled-reset protocol and the 397-attempt password sweep (0 hits) that preceded it.

---

## 7. Vector 4 — EseeCloud check-in MITM (status)

Two workstreams landed on 2026-08-09:

1. **Per-camera counter-cadence fix (done, validated).** The grant's `next-counter` was computed with a fixed fleet constant (`+0x13A0`), but the real server's cadence is **per-camera** — pconv `0x02d96045` (.29) advances ~0x13A0 per check-in, pconv `0x02d99e0f` (.169) ~0x15A0, each jittering with the actual interval. `scripts/eseecloud-ws-server.py` now seeds `CALIBRATED_CADENCE` and **learns cadence live** from the measured per-check-in counter advance; `scripts/eseecloud-replay-test.py` judges deltas per-camera (band + median-exact strict + live-seed cross-check). Suite: 51/51 pairs pass default and `--strict`; `plus1` fails as designed (A/B proof). Also fixed a `shift 2` arg-parsing bug in `eseecloud-mitm-capture.sh` and a missing `import time` crash.
2. **Live MITM run vs 10.0.0.29 (20260809T030607Z, 300s).** The camera dialed our fake servers heavily (58 connections, 62 data chunks) but sent **only the LITE 0x00 (32B)** registration form over HTTP-upgrade — **zero FULL 0x11 (128B)** frames, the form that actually adopts the granted counter. Gate stayed closed; no admin added. The `/message/` HTTP chain **advanced for the first time on .29**: 3 nonces forged, 2 sts tokens, 1 post_v2 with a camera-computed `verify=`. That post_v2 pair **matches the recovered formula** `MD5hex(TOUPPER(nonce)+eseeid+TOUPPER(request_id)+"Japass^2>.j")` exactly (the two sts pairs do not match — expected, the sts step uses the structurally different 2-field `oc_cal_message_verify`). Since the formula is password-independent (firmware salt), the pairs confirm the chain but do not crack the password.

---

## 8. State left on cameras (as of 2026-08-09)

| Camera | Web/NetSDK | ONVIF :8888 | RTSP | BossCam | Notes |
|---|---|---|---|---|---|
| 10.0.0.169 | 🔒 locked (401 for all tried creds) | open (any Basic pair; pw reverted to `admin:admin`) | 🔒 401 | not enrolled | web plane behind EseeCloud gate; ONVIF read-only access available |
| 10.0.0.227 | ✅ `admin:` blank works | open (any pair; pw reverted to `admin:admin`) | ✅ any cred streams HEVC | **enrolled + recording** (job `e38aee4c…` snapshot-mode, running) | fully manageable; recovery pipeline target |
| 10.0.0.29 | 🔒 locked | not exposed | not exposed | not enrolled | requires EseeCloud check-in path or operator's real web password |

---

## 9. Recommendations / next steps

1. **Point BossCam's enrollment at the discovered ONVIF plane** for `.169` (read-only mgmt) and keep `.227` on its working blank-password enrollment.
2. **EseeCloud MITM continuation:** the gate opens only if the camera's **FULL 0x11** registration adopts our grant; the LITE-only behavior under MITM is the current blocker (documented in the controlled-verify protocol). The power-cycle boot-time capture and the `eseecloud-add-admin.sh` handoff remain the next moves.
3. **On `.169`/`.29`, a real web password from the operator** (`export BOSSCAM_JUAN_PASSWORD=…` and re-enroll) would bypass the whole gate question.
4. ~~Revert ONVIF passwords from `BossCam2026!` back to `admin:admin`~~ — **DONE 2026-08-09**: both cameras reverted via SetUser; `admin:admin` is the stable credential for future scans.

---

## 10. References

- `src/BossCam.Infrastructure/Video/OnvifCredentialScanner.cs` — scanner + `OnvifWsse.BuildSecurityHeader` (WSSE digest formula)
- `src/BossCam.Infrastructure/Video/MultiBrandTransportAdapters.cs` — GetDeviceInformation/GetProfiles/GetStreamUri SOAP bodies + `SystemReboot`
- `docs/reports/2026-08-09-controlled-verify-experiment-protocol.md` — verify formula recovery, gate caveat, password sweep
- `scripts/eseecloud-ws-server.py`, `scripts/eseecloud-replay-test.py`, `scripts/eseecloud-mitm-capture.sh`, `scripts/eseecloud-add-admin.sh`
- Session: `captures/eseecloud-mitm-20260809T030607Z/`
