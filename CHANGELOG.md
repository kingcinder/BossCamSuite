# Changelog

All notable changes to BossCamSuite (Linux/Ubuntu edition) are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-08-11

### 5523-W full-surface wiring (firmware-proven)

- **27 firmware-proven `/NetSDK` endpoints appended to the endpoint catalog** — mined
  from the 5523-W firmware binary (`anyka_ipc` string table + `onvif_config.xml`),
  each with provenance annotations: ledpwm, alarm/record schedules, time/RTC/timezone,
  face detection, human detect, cordon, GB28181, GAT1400, FTP, RTMP, wireless signal,
  and network ports. Catalog now covers 140 endpoints.
- **20 new seed contracts** (`EndpointContractCatalogService`) with `SourcePath` fields
  copied verbatim from the firmware schema descriptors (`$.ledPwm.switch`,
  `$.AlarmSchedule[%d].Enabled`, `$.bEnableCordon`, `$.ScheduleEnabled`,
  `$.httpPort`/`$.rtspPort`/`$.onvifPort`, …) so the settings surface renders every
  evidence-backed control without guesses.
- **Expanded `LanDirectNetSdkRestAdapter.ReadEndpoints`** — Device +5, Video +3,
  Schedule +4, Wireless +4, Network +2, plus new LedPwm and Integrations groups.
- **Catalog-coverage tests extended** (21 new assertions; suite green at 426 tests).
- Research reports shipped: `docs/reports/2026-08-11-5523w-full-surface-wiring.md` and
  `docs/reports/2026-08-11-kp2p-ptz-action-enum.md` (full `kp2p_ptz_ctrl` action-enum
  recovery across the P2P wire plane, both HTTP CGI planes, and NVR firmware).

### Camera recovery & resilience

- **Autonomous factory-reset camera recovery** — AP hotspot → LAN → Suite pipeline with
  watchdog scripts (`scripts/install-recovery-watchdog.sh`, `scripts/recovery-watchdog.sh`).
- **Automatic seamless WAN loss/recovery** with recording-first preservation.
- **Passwordless bubble/live H.265 recording pipeline** for locked 5523-W cameras;
  explicitly-blank camera passwords now accepted during enroll.

### Operator console (Svelte 5 + Vite + TS)

- **Global force-apply mode** — every camera settings control is editable.
- **Recovery tab** and rebuilt SPA bundle (0 svelte-check errors/warnings).

### Protocol & playback

- **RFC 7616 digest-auth**, RTSP credential handshake, and NetSDK probe verdict cache for
  5523-W live playback; hardened shared media sessions with HEVC sub-source and
  authenticated fallbacks.
- NetSDK REST endpoints surfaced for SD playback, schedule, wireless, and alarm.
