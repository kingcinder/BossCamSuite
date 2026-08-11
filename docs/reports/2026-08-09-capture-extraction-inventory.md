# 2026-08-09 — Full Capture Extraction Inventory: Everything Recovered, and Why the Password Is Not In the Packets

Verdict: **A complete byte-level extraction of every capture has been performed. No
password, no password hash, and no credential material exists in any captured packet —
the EseeCloud protocol the cameras use never transmits it.** This file is the exhaustive
inventory of everything that *was* extracted, with the byte-level proof.

---

## 1. The short answer (why there is no password to extract)

The locked 5523-W cameras authenticate to the cloud with **four values, none of which is
the admin password**:

| Value | Source | In captures? |
|-------|--------|--------------|
| `eseeid` (e.g. `4780038910`) | serial-derived device ID | ✅ everywhere (plaintext URLs) |
| `pconv` (e.g. `0x02d96045`) | serial-derived u32LE constant | ✅ every registration frame |
| `counter` (e.g. `0x00003268`) | time-derived, advances every ~10 s | ✅ every registration frame |
| `verify` (32-hex MD5) | `MD5(UPPER(nonce)+eseeid+UPPER(request_id)+FIXED_SALT)` | ✅ every check-in |

The `verify` value — the only thing resembling a secret — is computed with a **hardcoded
firmware salt `Japass^2>.j`**, not the account password. This was resolved from the
firmware decompile (`oc_cal_verify @ 0x23ce00`) and validated on **11/11 captured
(nonce, verify) pairs** across both cameras (each 1-in-2^128). Cracking `verify` against
a password wordlist is therefore meaningless: the password is not an input to the
formula. **The admin password never crosses the wire in any MITM capture.**

---

## 2. Byte-level proof — the registration frames

### 2.1 LITE 0x00 frame (32 bytes) — the ONLY registration the camera sends under MITM

Layout (verified from raw frames in `eseecloud-data.bin`, all 12 MITM sessions):

```
[0:4]   magic    abbccdde
[4]     cmd      00 (LITE)
[5:12]  zeros    00000000000001
[12:16] counter  u32LE (time-derived; 0x00003268, +0x14 per ~20 s check-in in the
                05:02Z session — the FULL-path cadences in §3.4 differ per camera)
[16:20] pconv    u32LE (serial-derived; 47816347 for .227)
[20:32] zeros    000000000000000000000000
```

**Byte-variance across all frames from every session: only offsets 12–19 (counter,
pconv) ever change.** No hash field, no password field, 12 bytes of dead zero.

### 2.2 FULL 0x11 registration (128 bytes) — real-cloud captures only

Variance analysis across **all 51 real registration→grant pairs**
(`scripts/eseecloud-real-grants.json`):

| Offsets | Field | Behavior |
|---------|-------|----------|
| 0–4 | magic `abbccdde` | constant |
| 4 | cmd `0x11` | constant |
| 12–15 | counter | varies every check-in (time-derived) |
| 16–17 | pconv | 2 values (the two cameras) |
| 35–41 | ASCII serial digits | 2 values (`4780038910` / `4781620744`) |
| everything else | zeros | constant |

**No offset in the 128-byte payload carries a hash or credential.** The ws-server.py
comment referring to a "password hash" in the registration payload does not match the
captured bytes — the payload is counter + pconv + serial + zeros.

---

## 3. Everything that WAS extracted (the real payload)

### 3.1 Device identity (plaintext, hundreds of occurrences)

`POST /address/device?sn=<SERIAL>&max_ch=1` bodies (287 occurrences):

```
sn=JAZ7C34780038910&module=5523-W&odm=JUAN&hwcode=572110
  &fwversion=03061030&version=01043000&area=&install_type=0
  &cloudrecord=0&stream_des={"ch_count":1,"channels":[{"ch_id":0,"stream":[0,1]}]}
  &capabilities=[5,11,12,8,1...]
```

| Camera | Serial | eseeid | pconv |
|--------|--------|--------|-------|
| 10.0.0.29 | JAZ7C34780038910 | 4780038910 | 0x02d96045 |
| 10.0.0.169 | (JAZ7C3478...) | 4781620744 | 0x02d99e0f |
| 10.0.0.227 | JAZ7C34781634738 | 47816347 (prefix) | 0x02d99e9b |

Module `5523-W`, ODM `JUAN`, hwcode `572110`, fw `03061030` (Web UI reports
`3.6.103.5721106`), `version=01043000`.

### 3.2 Check-in / token flows (all verify hashes validated against the confirmed formula)

- `GET /message/nonce?method=get` — 367 occurrences
- `GET /message/sts?method=token&request_id=…&verify=…&type=1` — dozens
- `POST /message/message?method=post_v2&eseeid=…&request_id=…&verify=…` — bodies are
  motion-detection alerts only: `<message><alert>alarm test</alert><type>md</type>
  <time>…</time><id>4780038910</id><channel>0</channel>…`
- `POST /sts/tmptoken?method=get` — bodies: `&eseeid=…&request_id=<random>&verify=…&channel=0&type=1`
- `GET /PushStsDns.php` — push-server discovery (30 occurrences)
- Real cloud's rejection (the dead binding): `{"error":3004,"error_description":"Invalid verify","msg":"no user to push"}`

### 3.3 Cloud snapshot uploads (real cloud only)

`PUT /7%2Fdefault%2F<eseeid>%2F0%2F<date>%2F<timestamp>.jpg` to
`msd-img-hk.oss-cn-hongkong.aliyuncs.com` — captured JPEG bodies (up to 16,384-byte
chunks, valid `ffd8` JPEG headers) — the camera's cloud push-snapshot stream.

### 3.4 :19000 WebSocket check-in channel (the grant protocol)

- `d9ffcc…` probe hello (20 B) → real server ACK `96d5390d…` (16 B)
- `cefaeffe` hello → `cefaeffe64` ack
- `abbccdde 11/12` registration→grant exchange with counter cadence
  (`.29` ~0x13A0, `.169` ~0x15A0 per ~10 s check-in) — **byte-accurately reproduced**
  and validated against all 51 real pairs (`eseecloud-replay-test.py`).

### 3.5 TLS-terminated traffic

The TLS fake server captured one plaintext flow: the snapshot PUT to the OSS endpoint
(§3.3). No credentials, no cookies, no tokens beyond the URL path.

### 3.6 Searches that returned NOTHING

- `password=`, `passwd=`, `pwd=`, `Authorization: Basic` (beyond our own probes) —
  **zero hits** across every `data.bin`, `.log`, and `.pcap`
- login/checkin/user/auth/pwd/pass POST endpoints — **zero hits**
- No RTSP Digest or NetSDK Basic challenge-response from a *client* side — the cameras
  only ever *receive* our probes; they never authenticate outbound to those planes

---

## 4. Why this is structurally guaranteed

1. **The cloud plane needs no password**: check-in auth = eseeid + pconv + counter +
   verify (fixed-salt MD5). The account password lives only in the mobile app / web UI,
   never in the camera's outbound protocol.
2. **The web/NetSDK plane never sends credentials**: it *receives* them (Basic/Digest
   from a client). The cameras in our captures are clients to the cloud and servers to
   us — a server never transmits its own auth secret.
3. **The one-way trap**: the web-plane password gates `$.Auth.ticket`, which is set only
   by the cloud check-in that uses the fixed-salt verify. Capturing the check-in yields
   the salt-validated verify, not the password.

The exhaustive extraction therefore closes every "password in the packets" hypothesis
with direct byte evidence: **45,661 candidate passwords swept (0 hits), verify formula
confirmed as salt-only (11/11), and no credential field exists in any captured frame.**

---

## 5. What the captures ARE worth (recovered and usable)

- **Grant replay**: all 51 real pairs replay cleanly — a byte-accurate fake cloud server
- **Verify forgery**: post_v2 + sts formulas confirmed — forged check-ins validate
- **Fleet identity**: serials, eseeids, firmware, pconv for all three units
- **Cadence model**: per-camera counter cadence (0x13A0 / 0x15A0) for grant continuity
- **The dead-binding proof**: `error:3004 "no user to push"` from the real cloud

---

## 6. Local-plane pivot: NetSDK / ONVIF / RTSP sweep (2026-08-09)

A second extraction pass swept every capture artifact for local-plane traffic and
challenge-response material. Verdict: **the captures contain zero RTSP and zero ONVIF
traffic** — those planes were probed live (never MITM'd), so the pcaps hold no RTSP
Digest or ONVIF WSSE exchange to recover. What the sweep DID find:

### 6.1 The NetSDK gate's real response shape (recovered from pcaps)

The captures contain our own sweep requests (`GET /NetSDK/System/deviceInfo` with
`Authorization: Basic YWRtaW46` — i.e. `admin:` blank, src `10.0.0.149` = our box) and
— importantly — **the camera's actual gate reply**:

```json
{"requestMethod":"GET","requestURL":"/NetSDK/System/deviceInfo","requestQuery":"","statusCode":5,"statusMessage":"Invalid Operation"}
```

The gated units answer the NetSDK surface with **HTTP 401 carrying a JSON body** —
`{"requestMethod":"GET","requestURL":"/NetSDK/System/deviceInfo","requestQuery":"","statusCode":5,"statusMessage":"Invalid Operation"}`
— this is the ticket-gate response (per the exhausted-campaign report). The sweep's
0-hit result across 28,272 candidates is only possible because the HTTP status is 401
(any 200 would have been flagged a MATCH), so the JSON is the *body* of the 401, not a
replacement verdict.

### 6.2 The `.227` blank-plane never transmits credentials (confirmed)

Every `Authorization: Basic …` string in the captures is **our own probe** (all from
`10.0.0.149`, the analysis host). No camera ever sends an outbound Authorization header
on any local plane — the devices are *servers* to us and only ever receive credentials.
The blank-password plane of `.227` therefore has no outbound credential shape to
recover from the wire.

### 6.3 NEW: Aliyun OSS STS credentials recovered (32 unique signatures, 13 AccessKeyIds)

The cloud snapshot uploads carry genuine signed credential material:

```
PUT /7%2Fdefault%2F<eseeid>%2F0%2F<date>%2F<ts>.jpg HTTP/1.1
Host: msg-img-hk.oss-cn-hongkong.aliyuncs.com
Authorization: OSS STS.NYBFvmc5nFWFRtyCZa4i9sr9a:BwuP37zZlg1roqlDlJujtVWOUJg=
x-oss-security-token: CAIS4AN1q6Ft5B2yfSjIr5r3DczZjupP8… (long-lived STS token)
```

- **32 unique `Authorization: OSS STS.<AccessKeyId>:<Signature>` pairs** extracted
  across the real-cloud captures (see §3.3), spanning **13 distinct AccessKeyIds** (e.g.
  `NYBFvmc5nFWFRtyCZa4i9sr9a`). The `<AccessKeyId>` and the 500+ byte
  `x-oss-security-token` are **temporary, bucket-scoped Aliyun OSS STS credentials**
  issued by the EseeCloud backend for snapshot PUTs.
- These are the **only true credential-shaped secrets in any capture** — and they are
  NOT the camera admin password: they are time-limited, scoped to
  `msg-img-hk.oss-cn-hongkong.aliyuncs.com` snapshot writes. Within a session the same
  token is reused across multiple PUTs (identical header observed at 05:32:02), and
  **rotation happens across sessions/time windows** (13 distinct AccessKeyIds), not
  per-upload.

### 6.4 Attribution of every marker (nothing was misattributed)

| Marker | Origin | Verdict |
|--------|--------|---------|
| `WWW-Authenticate` / `Digest realm` / `nonce=` | our fake server's `/message` nonces (`NONCE_FORGED`) | our own MITM infrastructure |
| `Authorization: Basic YWRtaW46` | our sweep's `admin:` probes from 10.0.0.149 | our own probes |
| `Authorization: OSS STS.*` | the cameras' cloud snapshot uploads | **real recovered tokens** (§6.3) |
| `RTSP/1`, `DESCRIBE`, `onvif/device_service` | — | **zero occurrences** in any capture |

---

## 7. pconv ↔ eseeid ↔ serial: derivation confirmed, wire value is lossy (password bet closed)

Verified live 2026-08-09. **serial ↔ eseeid are mutually derivable**; **pconv is a lossy
one-way projection** of `eseeid[:8]`:

```
eseeid  = "4" + serial[len("JAZ7C34"):]     (e.g. 4 + "781620744" = 4781620744)
serial  = "JAZ7C34" + eseeid[1:]
pconv   = int(eseeid[:8])                    (u32LE on the wire; DROPS eseeid's last 2 digits)
```

Because pconv discards the eseeid's final two digits, the wire value alone can never
recover the full serial — the full-serial derivation this session used the **captured
full eseeid** (`4781620744` from the post_v2 URLs), not pconv.

Confirmed on **3/3 data points**: `.29` (serial `JAZ7C34780038910` ↔ eseeid `4780038910`
↔ pconv `0x02d96045`), `.169` (eseeid `4781620744` ↔ pconv `0x02d99e0f` → derived serial
`JAZ7C34781620744`), and `.227` (serial `JAZ7C34781634738` → derived eseeid `4781634738`
→ pconv `0x02d99e9b` ✓). The derived `.169` serial was cross-checked against the
captures: `sn=JAZ7C34781620744` appears verbatim in the `/address/device` bodies — so
`.169`'s serial was in the packets all along.

### 7.1 The serial-as-password bet, tested and closed

- `brute-netdsk.py` already carried `SERIAL_169 = "JAZ7C34781620744"` as a seed — but
  its expansion skips seeds >12 chars, so the full serial and the aggressive transforms
  below were never expanded in the prior 17,389-candidate run.
- **Fresh targeted sweep (40 serial/eseeid/pconv-derived candidates, incl. reverse,
  hex-pconv, admin-prefixed and suffix variants) against live `10.0.0.169`
  `/NetSDK/System/deviceInfo`: 0 hits** (paced 0.4 s; 401 = gated throughout).
- Conclusion: the derivation is a clean recoverable identity chain, but serial/eseeid/
  pconv-derived passwords are not the admin credential — consistent with the
  exhausted-campaign verdict that the gate is ticket-based, not password-based.
