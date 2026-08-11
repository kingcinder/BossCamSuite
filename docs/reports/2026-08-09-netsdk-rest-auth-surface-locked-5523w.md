# 2026-08-09 NetSDK REST auth surface — locked 5523-W (`deviceInfo` unlock hunt)

**Date:** 2026-08-09 · **Status:** COMPLETE — the locked 5523-W's `deviceInfo` 401 is **not negotiable HTTP auth**; the gate is the EseeCloud check-in ticket state, unreachable by any HTTP request shape.

---

## 1. Camera availability note

`10.0.0.169` (the originally requested target) and `10.0.0.227` were **unreachable** for the entire probe window (ARP `INCOMPLETE`, all of `:80/:8888/:554` refused; full-subnet scan found only `.29` answering HTTP). All live probes below ran against **`10.0.0.29` — the identical locked 5523-W** (same firmware family, same `check in falied` gate, same nginx-fronted NetSDK REST plane). The exact 401 body shape was previously confirmed on `.169` in earlier sessions (identical `statusCode:5 "Invalid Operation"` JSON), so the findings transfer 1:1. Re-run against `.169` when it returns to confirm byte-for-byte.

---

## 2. The exact challenge (the headline finding)

`GET http://10.0.0.29/NetSDK/System/deviceInfo` (no auth) returns:

```
HTTP/1.1 401 Unauthorized
SERVER: nginx
CONNECTION: close
CONTENT-TYPE:                 ← empty
CONTENT-LENGTH: 133
CACHE-CONTROL: no-cache

{"requestMethod":"GET","requestURL":"/NetSDK/System/deviceInfo","requestQuery":"","statusCode":5,"statusMessage":"Invalid Operation"}
```

**There is no `WWW-Authenticate` header.** This is the decisive fact:

- **No `WWW-Authenticate: Basic realm=…`** → the server is not asking for Basic; a browser/`curl -u` client has nothing to answer.
- **No `WWW-Authenticate: Digest realm=…, nonce=…`** → Digest negotiation (the RTSP plane's mechanism, per `RtspDigestHandshake`) cannot even start on the REST plane.
- The 401 is an **application-level verdict** (`statusCode:5 "Invalid Operation"` JSON), not an RFC-7235 challenge.

This matches the codebase's own observation in `ApiDevicesStreamingEndpoints.cs` ("401 carries no WWW-Authenticate challenge and therefore can never be negotiated") — the flow exists to handle firmware that *does* challenge, and this firmware is not one of them.

---

## 3. Auth variants tried — all 401, none negotiated

| Variant | HTTP | WWW-Authenticate |
|---|---|---|
| No auth header | 401 | (none) |
| `-u admin:admin` (Basic) | 401 | (none) |
| `-u admin:` (Basic blank) | 401 | (none) |
| `--digest -u admin:admin` | 401 | (none) |
| `Authorization: Bearer abc` | 401 | (none) |
| `X-Forwarded-For: 8.8.8.8` | 401 | (none) |

(The Bearer row was re-probed with proper quoting — the earlier run's `000` was a shell-quoting artifact, not a camera response — and a clean `X-Forwarded-For: 8.8.8.8` probe was added. Every header probe returns the identical `statusCode:5` body with no challenge, confirming the gate is checked before any header/credential is inspected.)

Because no challenge is ever emitted, the camera **never transitions** to an authenticated state regardless of what header or credential the client sends. Credentials are irrelevant — the 401 is emitted before any credential check.

---

## 4. EseeCloud ticket / cookie / checkin flow — does not exist on this surface

The hypothesis to test: maybe the web plane is gated by an EseeCloud ticket/cookie that a `/user/checkin` handshake would mint, then replay to `deviceInfo`. Probe results:

| Endpoint | GET | POST (JSON) | POST (form) |
|---|---|---|---|
| `/user/checkin` | 404 | 404 | 404 |
| `/user/checkin.xml` | 404 | 404 | 404 |
| `/user/checkin?user=admin&password=admin` | 404 | — | — |
| `/user/checkin?method=checkin` / `?action=checkin` / `?cmd=checkin` | 404 | — | — |
| `/user/checkin?sn=JAZ7C34780038910` | 404 | — | — |
| `/user/login` / `/user/login.xml` | 404 | 404 | 404 |
| `/NetSDK/System/login` / `.xml` | 404 | 404 | 404 |
| `/user/get_sn_num` | 404 | — | — |
| `/message/nonce` | (no Set-Cookie) | — | — |
| `/` | (no Set-Cookie) | — | — |

**No response on any probed path sets a `Set-Cookie`, `Location`, or `WWW-Authenticate` header** (checked: `/user/checkin`, `/NetSDK/System/login`, `/user/user_list.xml`, `/message/nonce`, `/`). There is no cookie to capture and replay.

Ticket/session-parameter replay against `deviceInfo` — all still 401 with the identical body:

- `?ticket=1`, `?ticket=abc123`, `?st=abc123`, `?session=abc123`, `?token=abc123`
- `?userName=admin&password=admin`, `?username=admin&password=`
- `Cookie: ticket=abc123; st=xyz`, `Cookie: ESESESSIONID=xyz`

The 401 body **echoes the query string** (`"requestQuery":"ticket=abc123"`) but always returns `statusCode:5`. The camera is not looking at any of these parameters; the gate is checked first.

### What `/user/checkin` really is

`/user/checkin` exists in the CgiFuzzer endpoint list (`src/BossCam.Infrastructure/Video/CgiFuzzer.cs`) but **returns 404 on this firmware** — it is not a live route on the locked 5523-W's nginx. The EseeCloud "check-in" that drives the gate is **not an HTTP endpoint**: per `eseecloud-dns-server.py` and the controlled-verify report, the gate is `$.Auth.ticket`, set only by the **WebSocket cloud check-in on `:19000`** (`/address/device` → P2P relay → WS `abbccdde 0x11` registration → grant). That is the Vector-4 MITM path, not an HTTP request shape.

---

## 5. The gate semantics (why nothing works)

`/user/user_list.xml` returns **HTTP 200** with the gate state inside the body:

```xml
<user ver="1.0" you="" add_user="no" ret="sorry" mesg="check in falied"></user>
```

The user-management plane answers 200 but refuses (`ret="sorry"`, `mesg="check in falied"`); the NetSDK control plane answers 401 `statusCode:5`. Both are **front-ends of the same `$.Auth.ticket` gate** (firmware `cgi_user.c`), which is:

- **OPEN** only after a successful cloud check-in (real server or our MITM grant being **adopted** — the FULL 0x11 form, never the LITE form the camera sends under MITM).
- Independent of HTTP authentication — credentials are never consulted while gated (the ONVIF `SetUser` reset proved the web/NetSDK store is separate and unaffected).

So: **no HTTP request shape (header, credential, cookie, ticket param, method, or body) unlocks `deviceInfo` on the locked 5523-W.** The 401 is a gate verdict, not an auth challenge.

---

## 6. Request shapes that DO return deviceInfo (for contrast)

| Camera | Shape | Result |
|---|---|---|
| `.227` (unlocked web plane) | `GET /NetSDK/System/deviceInfo` with **`admin:` blank Basic** | **200** — full JSON deviceInfo (serial, eseeID `4781634738`, MAC, firmware date) |
| `.29` / `.169` (gated) | any of the above | 401 `statusCode:5` |
| `.29` / `.169` | `GET /user/user_list.xml` | 200 `mesg="check in falied"` |

The only "passwordless" plane is a camera whose cloud binding is healthy (`.227`); the gated units need the ticket set — the Vector-4 MITM's job.

---

## 7. Conclusions

1. **The locked 5523-W's `deviceInfo` is unlockable by no HTTP request shape** — the 401 carries no `WWW-Authenticate`, so Basic/Digest/Bearer/cookie/ticket replay is structurally impossible.
2. **The `/user/checkin` ticket/cookie flow is a dead end on this firmware** — the endpoint 404s and no path issues a cookie.
3. **The gate is cloud-check-in state** (`$.Auth.ticket` via `:19000` WS), which only the Vector-4 EseeCloud MITM (with an **adopted** FULL 0x11 grant) or a healthy real-cloud binding can set.
4. Re-verify against `.169` on its return (expect identical 401 bytes); `.227` remains the working "passwordless" reference.

---

## 8. References

- Live session: probes against `10.0.0.29` (2026-08-09), all response headers/body captured inline
- `src/BossCam.Service/ApiDevicesStreamingEndpoints.cs` — 401-without-challenge handling (Digest-retry path)
- `src/BossCam.Infrastructure/Video/RtspDigestHandshake.cs` — the RTSP plane's (working) Digest mechanism, for contrast
- `src/BossCam.Infrastructure/Video/CgiFuzzer.cs` — `/user/checkin` in the endpoint list
- `scripts/eseecloud-dns-server.py`, `scripts/eseecloud-ws-server.py` — `$.Auth.ticket` / WS check-in mechanics
- `docs/reports/2026-08-09-controlled-verify-experiment-protocol.md` — gate driven by check-in state; `set_pass.xml` gate proof
- `docs/reports/2026-08-09-onvif-admin-admin-and-attack-vector-matrix.md` — Vector 1/2/4 statuses and store-separation findings
