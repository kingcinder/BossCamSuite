# Fleet Auth-State Snapshot — ONVIF / RTSP / NetSDK (2026-08-09)

**Date:** 2026-08-09 · **Author:** Buffy (BossCamSuite)
**Scope:** Re-enroll `10.0.0.227` with its blank web password and confirm BossCam stores the credential correctly; snapshot the ONVIF / RTSP / NetSDK authentication state of every live camera in the fleet.
**Related reports:** [2026-08-09-onvif-admin-admin-and-attack-vector-matrix.md](./2026-08-09-onvif-admin-admin-and-attack-vector-matrix.md) · [2026-08-09-netsdk-rest-auth-surface-locked-5523w.md](./2026-08-09-netsdk-rest-auth-surface-locked-5523w.md)

---

## 1. Executive summary

- **`.227` re-enrolled with its blank web password.** The enroll request (with `StartContinuousRecord`) merged into the existing device record `ccb89577-127a-4ac4-a63d-d0457dcc1f6` (`loginName: admin`, `displayName: 5523-W`) and the blank credential is **stored correctly**: `passwordCiphertext` is `null`, which is the canonical at-rest representation of an empty password in the BossCam store.
- **The stored blank credential works end-to-end.** Recording job `e38aee4c-21ed-42aa-bbd0-c759b48bfac0` is running with `sourceUrl` `http://admin:***@10.0.0.227/NetSDK/Video/encode/channel/101/snapShot` — BossCam is actively authenticating with the blank password it stored.
- **Fleet auth state is a three-way split:** `.169` and `.29` are **fully locked** (NetSDK 401 for every pair tried), `.227` is **partially open** (NetSDK answers 200 to blank only), and all three cameras' web user-management gates are **closed** (`/user/user_list.xml` → `ret="sorry" mesg="check in falied"`).
- **New observation:** `.29` has degraded since the last session — only `:80` responds now; ONVIF `:8888`/`:8899` and RTSP `:554` are closed. Its BossCam job is running on the snapshot fallback.

---

## 2. .227 re-enroll — request and storage verification

### 2.1 Enroll request — contract shape used

`POST /api/devices/enroll` with the `EnrollDeviceRequest` contract (shape shown; the exact wire body matches these fields):

```json
{
  "ipAddress": "10.0.0.227",
  "port": 80,
  "loginName": "admin",
  "password": "",
  "displayName": "5523-W",
  "startContinuousRecord": true
}
```

Result: enroll accepted, merged into the existing record (same `deviceId` `ccb89577`), the blank-password NetSDK identity probe returned **200**, and the continuous recording job started.

### 2.2 How BossCam stores a blank password (verified in code + live API)

`SqliteApplicationStore.SaveDevicesAsync` (src/BossCam.Infrastructure/Persistence/SqliteApplicationStore.cs):

```csharp
var ciphered = string.IsNullOrEmpty(device.Password)
    ? device
    : device with { PasswordCiphertext = _cipher.Encrypt(device.Password) };
```

- **Empty/null password → device persisted as-is → `passwordCiphertext` stays `null`.** No ciphertext is written for a blank credential (nothing worth protecting).
- On load, `ResolvePlaintextPassword` leaves `Password` as `null` when ciphertext is empty, and every consumer reads `device.Password ?? string.Empty` — so a stored `null` round-trips to an **empty (blank)** password.

Live API confirmation (`GET /api/devices`):

| ipAddress | deviceId | loginName | passwordCiphertext |
|---|---|---|---|
| 10.0.0.227 | `ccb89577-…` | `admin` | `null` ✅ |

**Conclusion:** BossCam stores the blank web password correctly — `loginName: admin` plus an explicit `null` ciphertext, which is the canonical "blank password" at-rest shape, and the running job proves the blank credential is the one being used.

---

## 3. Fleet auth-state matrix (live probes, 2026-08-09)

Probes: NetSDK REST `:80/NetSDK/System/deviceInfo` with Basic `admin:` and `admin:admin`; web gate `GET /user/user_list.xml`; ONVIF `:8888/onvif/device_service` GetUsers with `admin:admin`; RTSP `:554` DESCRIBE/SETUP on `ch0_main.h264`.

| Plane | 10.0.0.169 (Driveway) | 10.0.0.227 | 10.0.0.29 |
|---|---|---|---|
| **NetSDK `:80` — `admin:` (blank)** | 401 | **200 ✅** | 401 |
| **NetSDK `:80` — `admin:admin`** | 401 | 401 | 401 |
| **Web gate `/user/user_list.xml`** | 🔒 `check in falied` | 🔒 `check in falied` | 🔒 `check in falied` |
| **ONVIF `:8888` GetUsers `admin:admin`** | ✅ `admin`/Administrator | ✅ `admin`/Administrator | ⛔ port closed |
| **RTSP `:554` DESCRIBE (no auth)** | 200 OK, empty body | 200 OK, empty body | ⛔ port closed |
| **RTSP `:554` SETUP (no auth)** | 200 OK | 200 OK | ⛔ port closed |
| **BossCam job** | — (no recording job) | `e38aee4c` snapshot, running | `6b4699df` snapshot, running |

### 3.1 Notes on the rows

- **`.227` is the only NetSDK-open camera** — `admin:` blank returns 200 with full deviceInfo. `admin:admin` returns 401. This matches the healthy-cloud-binding explanation from the previous report: `.227`'s cloud grant is adopted, so the NetSDK gate passes for the blank web credential.
- **All three web user-management gates are closed** (`check in falied`) — consistent with the previous finding that `/user/*.xml` unlocks only via `$.Auth.ticket` from the `:19000` cloud check-in (FULL 0x11 grant).
- **RTSP answers 200 unauthenticated but delivers no SDP** — DESCRIBE returns `200 OK` (Server header `happytime rtsp server 2.2`, captured on `.169`) with an **empty body**; SETUP also 200. This is why the BossCam jobs show `degradedReason: "Main RTSP unreachable — using snapshot pipeline"` even though the port answers: the firmware's RTSP surface is present but not actually serving playable streams to these credentials/paths. The empty DESCRIBE body (no `a=control:` lines) means no real session can be established — the snapshot pipeline is the correct fallback.
- **`.29` degraded to HTTP-only.** The port sweep shows `:80` open, `:554`/`:8888`/`:8899`/`:19000` closed (previous sessions showed `:8888` ONVIF and `:554` answering). Its NetSDK `:80` still answers 401 for both pairs. The snapshot job keeps running via the `:80` NetSDK snapshot endpoint.
- **ONVIF GetUsers remains promiscuous on the two reachable cameras** — `.169` and `.227` answer `admin`/Administrator to `admin:admin` (as established previously, the ONVIF store accepts *any* Basic pair and is separate from the web/NetSDK/RTSP planes).

---

## 4. Auth-state summary by camera

| Camera | Verdict | NetSDK | ONVIF | RTSP | Web gate |
|---|---|---|---|---|---|
| **10.0.0.169** | 🔒 **Locked** (web plane) | 401 all pairs | reachable, `admin:admin` answers | 200/no-SDP | closed |
| **10.0.0.227** | ✅ **Semi-open** | **200 blank** | reachable, `admin:admin` answers | 200/no-SDP | closed |
| **10.0.0.29** | 🔒 **Locked + degraded** | 401 all pairs | port closed | port closed | closed |

---

## 5. Recommendations

1. **Keep `.227`'s stored blank credential as-is** — it is stored correctly (`null` ciphertext) and the recording job authenticates with it. Do not overwrite with a guessed password.
2. **For `.169`/`.29` unlocking** — no HTTP request shape unlocks their NetSDK 401 (see the NetSDK REST auth-surface report); the only known paths are the EseeCloud check-in MITM (FULL 0x11 grant, `:19000`) or the physical factory-reset recovery. Both are already staged.
3. **Re-check `.29`'s ports on the next pass** — the ONVIF/RTSP ports closing is a behavioral change worth confirming as a persistent state vs. a transient WiFi drop (5523-W is a WiFi unit with loose watchdog handling).
4. **Next auth-snapshot cadence:** re-run the §3 matrix whenever a camera's grant state changes (MITM adoption, factory reset, or operator password change).

---

## 6. Method & evidence

- All live probes run from the BossCamSuite host against the LAN (`10.0.0.x`, 5523-W units).
- NetSDK/ONVIF probes: `curl` with explicit Basic auth pairs; SOAP `GetUsers` against `/onvif/device_service`.
- RTSP probes: `curl -X DESCRIBE/SETUP` against `rtsp://<ip>:554/ch0_main.h264` — note the firmware answers 200 with an empty body (no SDP), which is the "present but not playable" signature.
- BossCam state: `GET /api/devices` and `GET /api/recordings/jobs` against the local service.
- Storage semantics verified against `SqliteApplicationStore.cs` (`string.IsNullOrEmpty(device.Password) ? device : device with { PasswordCiphertext = … }`).
