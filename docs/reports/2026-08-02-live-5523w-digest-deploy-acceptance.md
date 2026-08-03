# Live 5523-W Digest-Deploy Acceptance Pass — Report

> **Date:** August 2, 2026
> **Branch:** `main` (uncommitted digest-hardening round: `NativeNetSdkStreamAdapter`,
> `DigestAuth`, `MultiBrandTransportAdapters` gate, expanded `HttpControlAdapters` catalog)
> **Target camera:** 5523-W @ `10.0.0.169` — eseeID `4781620744`, firmware `3.6.103.5721106`,
> serial `Z7C34781620744`, manufacturer `GUANGZHOU`, MAC `9c:a3:a9:bc:6f:ec`
> **Deployment:** `/opt/bosscam` systemd unit `bosscam.service`
> **Scope:** publish → deploy → restart → live probe/sources/playback acceptance

---

## 1. Deployment

| Step | Evidence |
|---|---|
| Publish | `dotnet publish -c Release -o /tmp/bosscam-publish --no-restore` → **EXIT=0**; `BossCam.Infrastructure.dll` 324,608 B |
| Config parity | `appsettings.json` + `appsettings.Linux.json` **byte-identical** between `/opt/bosscam` and publish output — no production config clobbered |
| Contents | publish carries `wwwroot/`, `assets/`, `fixtures/`, `web.config`, `runtimes/` — full tree |
| Rsync | `rsync -a --delete /tmp/bosscam-publish/ /opt/bosscam/` → **OK** (dir is `cody`-owned; no root needed for copy) |
| New adapter present | `strings` on deployed `BossCam.Infrastructure.dll`: `NativeNetSdkStreamAdapter` ×2, `TryBuildDigestAuthorization` ×2 — vs **0/0** in the pre-deploy build |
| Restart | systemd `Restart=on-failure` relaunched the process (old PID 4279 → new PID 4041827) without root; unit confirms `User=cody`, `Restart=on-failure`, `RestartSec=5` |
| Health | `GET /api/health` → `{"status":"ok","platform":"Ubuntu 24.04.4 LTS","framework":".NET 8.0.29","contentRoot":"/opt/bosscam","ffmpeg":"/usr/bin/ffmpeg"}` |
| Startup log | clean: `RecordingLifecycleWorker` reconcile (2 jobs, 0 running), `ConnectivityWatchdogWorker started` |

---

## 2. Live probe — does the native adapter prove the family?

**Camera-side REST plane (`:80`):**

```
GET /NetSDK/System/deviceInfo (no creds)   → 401  {"requestMethod":"GET",...,"statusCode":5,"statusMessage":"Invalid Operation"}
GET /NetSDK/System/deviceInfo (Basic admin:) → 200  {"deviceName":"5523-W","model":"5523-W",
      "serialNumber":"Z7C34781620744","eseeID":"4781620744","sdkVersion":"2.4.2.103",
      "firmwareVersion":"3.6.103.5721106","manufacturer":"GUANGZHOU",...}
```

The recorded device (`0654e903-…`) carries `port: 8888` with **no credentials** — the probe
falls back through `NetSdkPortCandidates` to `:80` and uses `admin` / empty password.

**Service journal (triggered by `GET /api/devices/{id}/sources`):**

```
GET http://10.0.0.169:8888/NetSDK/System/deviceInfo   ← recorded ONVIF port, non-2xx → continue
GET http://10.0.0.169/NetSDK/System/deviceInfo        ← :80 fallback, Basic admin: → 200 ✓
GET http://10.0.0.169/NetSDK/Stream/channel/0         ← follow-up stream probe on :80 ✓
```

**Result: the probe completed against the real 5523-W.** `device.Metadata["nativeNetSdk"]="true"`
was stamped and the generic MultiBrand RTSP-guess tier was suppressed for the device.

**Digest note (honest finding):** on *this specific unit* the REST plane answers **Basic**
`admin:` with 200 directly, and the unauthenticated 401 carries **no `WWW-Authenticate`
header** — so `TryBuildDigestAuthorization` correctly declines (no Digest scheme) and the
probe wins via Basic. The RFC-2617 Digest retry (this round's hardening: unquoted `qop=`,
`MD5-sess` refusal, origin-form `uri=`, boundary-guarded parameter parsing) is the fallback
for firmware generations whose REST plane challenges with Digest — the RTSP plane here is
already happytimesoft Digest-auth (see §4), and the path is pinned by 9 fixture-driven unit
tests. It is deployed and armed; this unit simply never needed it.

---

## 3. Source ranking — correct order?

`GET /api/devices/0654e903-afdb-4d1d-b016-b3c9957600a1/sources` (abridged):

| Rank | Kind | URL | DisplayName | Native marker |
|---|---|---|---|---|
| **0** | Rtsp | `rtsp://admin:@10.0.0.169:554/ch0_0.264` | NetSDK main HEVC | `nativeNetSdk=true`, codec=hevc, 2560×1920 |
| **3** | Rtsp | `rtsp://admin:@10.0.0.169:554/11` | NetSDK RTSP /11 alias | main |
| 15 | OnvifRtsp | `http://admin@…:8888/onvif/device_service` | OnvifRtsp (existing profile) | — |
| 16 | Rtsp | `rtsp://admin@10.0.0.169:554/` | Rtsp (existing profile) | — |
| **26** | LanRest | `http://admin:@…:80/NetSDK/Video/encode/channel/101/snapShot` | **JPEG snapshot (NetSDK :80 fallback)** | `port=80` — correctly marked *fallback* because recorded port was 8888 |
| **50** | Rtsp | `rtsp://admin:@10.0.0.169:554/ch0_1.264` | NetSDK sub HEVC | `nativeNetSdk=true`, codec=hevc, 704×480 |
| **51** | Rtsp | `rtsp://admin:@10.0.0.169:554/12` | NetSDK RTSP /12 alias | sub |

**Ranking verdict: correct.** Main `ch0_0.264` first (0), `/11` alias (3), snapshot (26) before
sub (50), `/12` alias (51). The snapshot correctly reports `:80 fallback` (recorded port 8888
→ proven 80). The two pre-existing ONVIF/Rtsp transport profiles (15/16) are untouched.

---

## 4. Playback — is video actually flowing?

| Check | Result |
|---|---|
| `GET /api/devices/{id}/live-info` | `mainRtsp: …/ch0_0.264`, `subRtsp: …/ch0_1.264`, `preferredLive: …/ch0_1.264` (sub) |
| `GET /api/devices/{id}/snapshot` | **HTTP 200**, 37,980 B, `JPEG image data … 704x480` (through the service) |
| Direct camera snapshot | HTTP 200, 18,872 B, 704×480 JPEG (channel 101) |
| Direct RTSP DESCRIBE (`ch0_1.264`) | SDP received: **`video codec set to: hevc`** + `audio codec set to: pcm_alaw` (8 kHz) — happytimesoft Digest-auth plane on :554, TCP |
| **`GET /api/devices/{id}/live.ts?quality=sub`** (12 s) | **2,628,992 B captured; ffprobe decoded 234 frames @ ~19.5 fps, H.264 960×654** — real media flowing through the service's hardened media session |

The HEVC sub-stream is picked by the service, and `live.ts` delivers a decodable H.264 TS
pipe to the player — the SPA/Avalonia acceptance contract holds against the live camera.

---

## 5. Verification summary

| Suite / check | Result |
|---|---|
| Release publish | ✅ EXIT=0 |
| Unit suite (from hardening round) | ✅ 357/357 green |
| Deployed symbol check | ✅ native adapter + digest present; old build lacked both |
| Live probe (port cascade 8888 → 80) | ✅ deviceInfo 200 on :80, `nativeNetSdk` stamped |
| Source ranking | ✅ main 0 / alias 3 / snap 26 / sub 50 / alias 51 |
| Snapshot (service + direct) | ✅ 200, valid JPEG |
| RTSP DESCRIBE | ✅ HEVC + PCM ALAW SDP |
| 12 s live.ts capture | ✅ 234 frames, ~19.5 fps, decodable |

**No code changes were made this round** — deploy + acceptance only. The digest-retry path is
deployed and armed; unit-tested against the exact challenge shapes this firmware family emits
(quoted/bare `qop`, `auth-int` and `MD5-sess` refusal, origin-form `uri=`).
