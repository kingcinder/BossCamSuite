# BossCamSuite — fleet operator notes (7-camera home LAN)

These notes cover the real 7-camera fleet: 4× Juan/GUANGZHOU **5523-W**, a **Wansview** Wi-Fi cam,
a **Netview** Wi-Fi cam, and a **Generic Temu PTZ** Wi-Fi cam. No secrets live in the repo —
passwords come from the enroll request, per-profile env vars, or per-brand env vars.

## 1. Credential profiles

Passwords are resolved in this order by `POST /api/devices/enroll` (and the SPA **Add & Record** form):

1. `password` supplied in the request body, else
2. `BOSSCAM_CRED_<PROFILE>_PASSWORD` (profile name uppercased, `-`/space → `_`), else
3. brand env var by hardware model:
   - 5523-W / Juan / Guangzhou → `BOSSCAM_JUAN_PASSWORD` or `BOSSCAM_LOREX_PASSWORD`
   - WVC / Wansview → `BOSSCAM_WVC_PASSWORD`
4. generic `BOSSCAM_PASSWORD`

Example shell setup (do not commit these):

```bash
export BOSSCAM_CRED_DEFAULT_PASSWORD='…'   # shared admin password for most cameras
export BOSSCAM_JUAN_PASSWORD='…'
export BOSSCAM_WVC_PASSWORD='…'
export BOSSCAM_PASSWORD='…'
```

A missing password never loops or hangs — enroll returns a clear `credentials` step failure.

## 2. Enrolling the fleet

**One camera (curl):**

```bash
curl -X POST http://<host>:5317/api/devices/enroll \
  -H 'Content-Type: application/json' \
  -d '{"ipAddress":"10.0.0.5","loginName":"admin","credentialProfile":"default","startContinuousRecord":true}'
```

**The whole fleet (enroll-batch), or in the SPA:** hit **Enroll All Discovered** (idempotent —
re-enrolling merges by MAC/IP and re-probes) or **Add & Record** per camera. Enroll-batch body:

```json
[
  {"ipAddress":"10.0.0.2","loginName":"admin","credentialProfile":"default","startContinuousRecord":true},
  {"ipAddress":"10.0.0.3","loginName":"admin","credentialProfile":"default","startContinuousRecord":true},
  {"ipAddress":"10.0.0.4","loginName":"admin","credentialProfile":"default","startContinuousRecord":true},
  {"ipAddress":"10.0.0.5","loginName":"admin","credentialProfile":"default","startContinuousRecord":true},
  {"ipAddress":"10.0.0.6","loginName":"admin","credentialProfile":"default","startContinuousRecord":true},
  {"ipAddress":"10.0.0.7","loginName":"admin","credentialProfile":"default","startContinuousRecord":true},
  {"ipAddress":"10.0.0.8","loginName":"admin","credentialProfile":"default","startContinuousRecord":true}
]
```

Enroll returns a structured result: `enrolled`, `httpControlPort` (learned from the probe),
per-step `steps` (identity / netsdk-probe / auth / sources / continuous-record), a redacted
`chosenSourceUrl`, `sourceRole` (main / sub / snapshot), `degradedReason` when no RTSP answered,
and `continuousJobId` when recording started.

**Boot policy:** devices flagged `continuousRecord` are (re)started automatically after the
startup reconcile and every housekeeping cycle — a camera whose recorder died comes back on its
own. `hasAudio` is set on the recording index when the stream carries audio.

## 3. Per-brand resolution

| # | Brand | Control port | RTSP main / sub | Audio |
|---|-------|-------------|-----------------|-------|
| 1–4 | Juan **5523-W** (×4) | NetSDK REST **:80** (discovery may record ONVIF 8888/8899; probe falls back to :80) | `/ch0_0.264` (HEVC 2560x1920) / `/ch0_1.264` (H264 704x480) | yes (main stream) |
| 5 | **Wansview** Wi-Fi | ONVIF (WVC, probe 8899→8888→80) | ONVIF GetStreamUri preferred; fallback `/stream1`, `/live`, `/h264`, `/videoMain`, `/cam/realmonitor?channel=1&subtype=0`, … | yes when present (indexed) |
| 6 | **Netview** Wi-Fi | ONVIF / generic | GetStreamUri; fallback generic paths | yes when present |
| 7 | **Temu PTZ** Wi-Fi | weak ONVIF / generic | generic RTSP paths; PTZ control optional follow-up | yes when present |

Key mechanics: `NetSdkPortCandidates` drives every NetSDK REST probe (recorded port → :80), the
multi-brand adapter emits the generic RTSP paths below its brand-proven/ONVIF tiers (probe-playable,
never assumed), and Wi-Fi devices (`LinkHint=Wifi`) get the same self-healing record policy —
longer reconnects are absorbed by the cycle-based restart cadence.

## 4. Verification checklist (run on the real LAN)

- Discover shows all 7 (multicast + subnet scan; ONVIF WS-Discovery).
- Enroll each 5523-W → `httpControlPort: 80`, continuous record starts on the main RTSP.
- Enroll Wansview / Netview / Temu → at least one playable RTSP or a degraded snapshot pipeline; no crash.
- Restart BossCam → continuous jobs resume/re-attach; no duplicate runaway ffmpeg.
- Kill one ffmpeg → job restarts on the next cycle or recovers from a stall.
- Playback/export a time range on one camera while all continuous jobs keep running.
- 5523-W Features toggles still apply (no regression).
