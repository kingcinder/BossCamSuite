# Live 5523-W bfd2e86 Deploy + 30-min Soak Acceptance — Report

> **Date:** August 3, 2026 (UTC 05:00–05:40)
> **Commit:** `bfd2e86` (main, pushed) **plus one required build fix** — see §1
> **Target camera:** 5523-W @ `10.0.0.169` — ONVIF port 8888 recorded, RTSP :554 happytimesoft
> Digest plane, eseeID `4781620744`, firmware `3.6.103.5721106`
> **Deployment:** `/opt/bosscam`, systemd unit `bosscam.service` (`User=cody`,
> `WorkingDirectory=/opt/bosscam`, `Restart=on-failure`)
> **Scope:** publish → deploy → restart → digest probe → HEVC sub-stream → **30-minute
> bitrate/crash soak** with drop-injection and restart-bounds assertion

---

## 1. Required one-line build fix (shipped with the deployment)

The pushed `bfd2e86` **did not compile**: `ExtractUrls` in
`src/BossCam.Infrastructure/Video/VideoTransportAdapters.cs` passed a C# 12 collection
expression to `string.Split`:

```csharp
raw.Split(['"', '\r', '\n', ' ', ','], StringSplitOptions.RemoveEmptyEntries)   // CS0121
```

which is ambiguous between `Split(char[]?, …)` and `Split(string?, …)` — the C# 12
collection expression is target-typed to both overloads during overload resolution.
**Fix (one line, behavior-preserving):** explicitly-typed `char[]`:

```csharp
raw.Split(new char[] { '"', '\r', '\n', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
```

The `char[]` overload is the intended one (split on *any* of the 5 separators; the
`string` overload would split on the literal substring — never the intent). This is the
**only** production-code change on top of the pushed commit; it is uncommitted and should
be committed/pushed so the deployed artifact maps to a reproducible commit.

Validation of the fix + tree:

| Check | Result |
|---|---|
| `dotnet build BossCamSuite.Linux.sln -c Release` | ✅ 0 warnings / 0 errors |
| Full unit suite `BossCam.Tests` | ✅ **395/395 green** (2 m 4 s) |
| Code review (deepseek-flash) | ✅ correct, minimal, behavior-preserving |
| Other `Split([` collection expressions in `src/` | ✅ none ambiguous (the `string[]` one in `DiscoveryProviders.cs` binds cleanly) |

---

## 2. Deployment

| Step | Evidence |
|---|---|
| Publish | `dotnet publish src/BossCam.Service -c Release -o /tmp/bosscam-publish` → **EXIT=0** |
| Rsync | `rsync -a --delete /tmp/bosscam-publish/ /opt/bosscam/` → **OK** (dir is `cody`-owned) |
| Config parity | Production `appsettings.json` / `appsettings.Linux.json` / `appsettings.Development.json` **backed up and restored byte-identical** — no production config clobbered (deployed config predates the new `NetSdkProbeCacheTtlMinutes` key; code default applies) |
| New symbols present | `strings` on deployed `BossCam.Infrastructure.dll`: `TryBuildDigestAuthorization` ×2, `NetSdkProbeVerdictCache` ×1 |
| Restart | `systemctl restart bosscam.service` → exit 0, **active** |
| Health | `GET /api/health` → `{"status":"ok","platform":"Ubuntu 24.04.4 LTS","framework":".NET 8.0.29","contentRoot":"/opt/bosscam","ffmpeg":"/usr/bin/ffmpeg"}` |

---

## 3. Live digest probe + source ranking (10.0.0.169)

`GET /api/devices/0654e903-…/sources` (device recorded with port 8888, no creds — probe
cascades `NetSdkPortCandidates` to :80 and applies `admin:`):

| Rank | Kind | URL | Evidence |
|---|---|---|---|
| **0** | Rtsp | `rtsp://admin:@10.0.0.169:554/ch0_0.264` | **main HEVC** 2560×1920, `nativeNetSdk=true`, `auth=digest` |
| 3 | Rtsp | `rtsp://admin:@10.0.0.169:554/11` | main alias |
| 15 | OnvifRtsp | `http://admin@…:8888/onvif/device_service` | existing profile (untouched) |
| 16 | Rtsp | `rtsp://admin@10.0.0.169:554/` | existing profile (untouched) |
| **26** | LanRest | `http://admin:@…:80/NetSDK/Video/encode/channel/101/snapShot` | JPEG snapshot, `port=80` fallback, `nativeNetSdk=true` |
| **50** | Rtsp | `rtsp://admin:@10.0.0.169:554/ch0_1.264` | **sub HEVC** 704×480, `nativeNetSdk=true`, `auth=digest` |
| 51 | Rtsp | `rtsp://admin:@10.0.0.169:554/12` | sub alias |

**Digest probe verdict:** the probe completed against the live unit — the REST plane answers
**Basic `admin:` with 200** on :80 and its 401 carries no `WWW-Authenticate`, so
`TryBuildDigestAuthorization` correctly declines (no Digest scheme) and the probe wins via
Basic. The RTSP plane is happytimesoft **Digest-auth on :554** (see §4), and the digest
code path is armed + unit-tested (9 fixture-driven tests) for firmware generations that do
challenge with Digest. `nativeNetSdk` stamped; ranking correct (main 0 < alias 3 < snap 26
< sub 50 < alias 51).

---

## 4. HEVC sub-stream identification

| Check | Result |
|---|---|
| `GET /api/devices/{id}/live-info` | `mainRtsp: …/ch0_0.264`, `subRtsp: …/ch0_1.264`, `preferredLive: …/ch0_1.264` |
| Snapshot via service | **HTTP 200, 24,785 B, JPEG 704×480** |
| Direct RTSP DESCRIBE digest handshake (`ch0_1.264`, TCP) | ✅ **`codec_name=hevc` 704×480** + `codec_name=pcm_alaw` — happytimesoft Digest-auth plane accepts the computed credentials |

---

## 5. 30-minute bitrate / crash soak

Harness `/tmp/bosscam-soak2.sh` (detached via `setsid`): consumed the service's shared
`live.ts?quality=sub` session for **31:08 wall (1868s)**, sampled bitrate every 20s,
injected a **consumer kill at t+480s** and a **bitrate spike (extra main-stream consumer)
at t+902s**, auto-restarted on any drop with a latency bound (<30s), and asserted service
`MainPID` stability across the whole window.

| Metric | Result |
|---|---|
| Wall clock | **1868 s** (≥ 30 min requirement ✅) |
| Bytes captured | **109,445,716 B (~109 MB)** |
| Average bitrate | **468,718 bps (~470 kbps)** |
| Peak sample bitrate | **524,288 bps** |
| Samples | 91 |
| **Injected connection drop** (t+480s) | ffmpeg killed; **restart latency 2 s** (bound <30s) **PASS**; health 200 after |
| **Injected bitrate spike** (t+902s) | extra main-stream consumer for 45s; service kept streaming, no stall, **0 errors** |
| Unexpected drops | **0** (only the injected one) |
| ffmpeg errors in harness log | **0** |
| Service `MainPID` | 1442854 → 1442854 **STABLE** across entire soak |
| Service state at end | **active** |

The streamed sub-session held a rock-steady cadence (419/524 kbps alternating 20s samples)
with **zero involuntary drops over 31 minutes**, one injected drop recovered in 2s, and a
bitrate spike absorbed without service disruption. **"Rock solid" is now a measured,
regression-verifiable property of this deployment.**

---

## 6. Verification summary

| Check | Result |
|---|---|
| Release publish | ✅ EXIT=0 |
| Full solution build | ✅ 0/0 |
| Unit suite | ✅ 395/395 |
| Config preserved | ✅ appsettings byte-identical after deploy |
| Digest probe vs live unit | ✅ completes; Basic wins on :80; Digest armed for RTSP/Digest-challenging firmware |
| HEVC sub-stream | ✅ DESCRIBE `hevc` 704×480 + `pcm_alaw`; live-info picks sub |
| Snapshot | ✅ 200, valid JPEG |
| 30-min soak | ✅ 31:08, 0 involuntary drops, injected drop +2s restart, spike absorbed, service PID stable |

**Notable follow-ups (non-blocking):** (1) commit+push the one-line `Split` fix so the
deployed tree is reproducible; (2) the `NetSdkProbeCacheTtlMinutes` key should be added to
`/opt/bosscam/appsettings*.json` on the next config-touch to pin the verdict-cache TTL
explicitly instead of relying on the code default.
