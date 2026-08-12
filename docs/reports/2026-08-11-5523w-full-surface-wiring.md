# 5523-W Full Feature / Setting Surface — Definitive Inventory & Wiring

**Date:** 2026-08-11
**Firmware:** IPCAKV3C_20211221_3_6_60_572010 (anyka_ipc), cross-checked against live 5523-W probes
**Status:** Complete — every firmware-proven setting is now wired into the BossCamSuite backend and surfaces in the SPA.

---

## 1. What this report is

This is the definitive map of **every feature and setting available on the 5523-W cameras**, derived
from the extracted firmware (`anyka_ipc` binary string table, `onvif_config.xml`, the endpoint
catalog, and the EseeCloud/NVR web surfaces mined in prior passes) and **wired end-to-end** into
BossCamSuite:

- **Backend:** `LanDirectNetSdkRestAdapter.ReadEndpoints` now reads every firmware-proven group;
  `EndpointContractCatalogService` seeds a contract for each group with **field SourcePaths taken
  verbatim from the firmware string table** (not SDK-doc guesses).
- **Frontend:** the SPA Features panel is contract-driven and generic — every new seed contract
  renders automatically as a toggle / slider / dropdown / text input with live camera values and
  write-verified state.

---

## 2. Firmware-proven endpoint surface (new in this pass)

Mined verbatim from `anyka_ipc` string runs. All 27 endpoints were appended to
`assets/protocols/endpoint_catalog.json` (catalog now **140 entries**; 109 pre-existing + 27
5523-W-proven + 4 earlier NVR passes).

### System / device plane

| Endpoint | Methods | Proven field keys (firmware strings) |
|---|---|---|
| `/NetSDK/System/ledpwm` | GET, PUT | `$.ledPwm.switch`, `$.ledPwm.project`, `$.ledPwm.strProduct`, `$.ledPwm.nChannelCount`, `$.channelInfo[%d].{type,channel,num,numMotion,schedule[%d]}` |
| `/NetSDK/System/ledpwm/ChannelInfo` | GET | `$.channelInfo` (per-channel array) |
| `/NetSDK/System/deviceInfo/deviceName` | GET, PUT | `$.deviceName` |
| `/NetSDK/System/deviceInfo/deviceAddress` | GET, PUT | `$.deviceAddress` |
| `/NetSDK/System/capabilities` | GET | `$.Capabilities.{SupportFaceDetect,MaxFaceDetectNum,SupportHumanDetect,MaxHumanDetectNum}` |
| `/NetSDK/System/time/rtc` | GET, PUT | `$.rtc`, `bRtc` |
| `/NetSDK/System/time/timeZone` | GET, PUT | `$.timeZone` — 31 observed GMT offsets (`GMT-11:00` … `GMT+13:00`, half-hour + 45-min variants) |
| `/NetSDK/System/time/calendarStyle` | GET, PUT | `$.calendarStyle` (opt[0..1]) |

### Schedules

| Endpoint | Methods | Proven field keys |
|---|---|---|
| `/NetSDK/System/AlarmSchedule` | GET, PUT | `$.AlarmSchedule[%d].{Enabled,nType,Weekday,BeginTime,EndTime}` |
| `/NetSDK/System/AlarmScheduleV2` | GET, PUT | V2 payload (`ScheduleEnabled`/`ScheduleScheme` family) |
| `/NetSDK/System/AlarmTone` | GET, PUT | `$.AlarmTone[%d].*` (per-id 0..2) |
| `/NetSDK/System/RecordSchedule` | GET, PUT | `$.RecordSchedule[%d].{Enabled,RecType(4 opts),Weekday,BeginTime,EndTime}` |

### Smart video / analytics

| Endpoint | Methods | Proven field keys |
|---|---|---|
| `/NetSDK/Video/FaceDetection` | GET, PUT | `$.SupportFaceDetect`, `$.MaxFaceDetectNum` (capability, read-only); `$.enabled` lever |
| `/NetSDK/Video/HumanDetect` | GET, PUT | `$.SupportHumanDetect`, `$.MaxHumanDetectNum`; `$.enabled` lever |
| `/NetSDK/Video/cordon` | GET, PUT | `$.bEnableCordon`, `$.enCordonType`, `$.stCordonLinelist`, `$.stCordonArealist` |

### Network / integrations

| Endpoint | Methods | Proven field keys |
|---|---|---|
| `/NetSDK/Network/port`, `/NetSDK/Network/port/[id=1]` | GET, PUT | port JSON form |
| `/NetSDK/Network/wireless/stationSignal` | GET | `$.SignalStrength` / `stationsignal` |
| `/NetSDK/Network/wireless/allStaInfo` | GET | sta info array |
| `/NetSDK/Wireless/ScanApList` | GET, POST | AP list array (scan trigger) |
| `/NetSDK/Network/Wireless/status` | GET | wireless status |
| `/NetSDK/FTP` | GET, PUT | `$.ScheduleEnabled`, `$.schedule` / `$.stFtpSchedule[%d]` |
| `/NetSDK/RTMP` | GET, PUT | `$.rtmpUrl` |
| `/NetSDK/System/gb28181` | GET, PUT | `bGB28181`, `GB28181_Server`, `$.sipServerport`, `$.ServerPort` |
| `/NetSDK/System/gat1400` | GET, PUT | `N1Device_EvtSetGat1400Config` toggle (inferred `$.bGAT1400`) |

---

## 3. Backend wiring (this pass)

### 3a. Seed contracts — `EndpointContractCatalogService.BuildSeedContracts()`

**20 new contracts** added, one per firmware-proven group, all with `SourcePath` copied verbatim
from the firmware string table (four — `system.device.name`, `system.device.address`,
`system.alarm.tone`, `system.alarm.scheduleV2` — plus `network.port` were added in the
review-fix pass after the first review flagged read-but-unmapped writable endpoints):

| ContractKey | GroupKind | Endpoint | Key fields |
|---|---|---|---|
| `system.ledpwm` | VideoImage | `/NetSDK/System/ledpwm` | ledPwmSwitch, ledPwmProject, ledPwmChannelCount, ledPwmChannelInfo |
| `system.ledpwm.channelInfo` | VideoImage | `/NetSDK/System/ledpwm/ChannelInfo` | channelInfo (read-only) |
| `system.alarm.schedule` | MotionPrivacyAlarms | `/NetSDK/System/AlarmSchedule` | alarmScheduleEnabled, Weekday, BeginTime, EndTime |
| `system.record.schedule` | StoragePlayback | `/NetSDK/System/RecordSchedule` | recordScheduleEnabled, RecType enum, Weekday, Begin/End |
| `video.face.detection` | MotionPrivacyAlarms | `/NetSDK/Video/FaceDetection` | supported (RO), maxNum (RO), enabled |
| `video.human.detect` | MotionPrivacyAlarms | `/NetSDK/Video/HumanDetect` | supported (RO), maxNum (RO), enabled |
| `video.cordon` | MotionPrivacyAlarms | `/NetSDK/Video/cordon` | cordonEnabled, cordonType, cordonLines, cordonAreas |
| `system.time.rtc` | UsersMaintenance | `/NetSDK/System/time/rtc` | rtc |
| `system.time.timezone` | UsersMaintenance | `/NetSDK/System/time/timeZone` | timeZone (31-offset enum from firmware) |
| `system.time.calendarStyle` | UsersMaintenance | `/NetSDK/System/time/calendarStyle` | calendarStyle (Gregorian/Lunar) |
| `network.gb28181` | NetworkWireless | `/NetSDK/System/gb28181` | gb28181Enabled, Server, SipPort, ServerPort |
| `network.gat1400` | NetworkWireless | `/NetSDK/System/gat1400` | gat1400Enabled (inferred) |
| `network.ftp` | NetworkWireless | `/NetSDK/FTP` | ftpScheduleEnabled, ftpSchedule |
| `network.rtmp` | NetworkWireless | `/NetSDK/RTMP` | rtmpUrl |
| `network.wireless.signal` | NetworkWireless | `/NetSDK/Network/wireless/stationSignal` | SignalStrength (RO), stationsignal (RO) |
| `system.device.name` | UsersMaintenance | `/NetSDK/System/deviceInfo/deviceName` | deviceName |
| `system.device.address` | UsersMaintenance | `/NetSDK/System/deviceInfo/deviceAddress` | deviceAddress |
| `system.alarm.tone` | MotionPrivacyAlarms | `/NetSDK/System/AlarmTone` | AlarmTone[0].{Enabled,tone} |
| `system.alarm.scheduleV2` | MotionPrivacyAlarms | `/NetSDK/System/AlarmScheduleV2` | ScheduleEnabled, ScheduleScheme |
| `network.port` | NetworkWireless | `/NetSDK/Network/port` | httpPort, rtspPort, onvifPort |

Truth states are `Inferred` with `Source = "firmware-string"` and the exact firmware log/descriptor
string in `Notes` — so a live read/verify pass can promote them to `Proven` via the existing
fixture-driven promotion pipeline. No new `GroupKind` enum values were needed; the 7 existing kinds
cover the whole surface.

### 3b. Read surface — `LanDirectNetSdkRestAdapter.ReadEndpoints`

Expanded so the snapshot read covers every new group:

- **Device:** `+deviceName`, `+deviceAddress`, `+capabilities`, `+time/rtc`, `+time/timeZone`,
  `+time/calendarStyle`
- **Video:** `+FaceDetection`, `+HumanDetect`, `+cordon`
- **Schedule:** `+AlarmSchedule`, `+AlarmScheduleV2`, `+AlarmTone`, `+RecordSchedule`
- **Wireless:** `+ScanApList`, `+stationSignal`, `+allStaInfo`, `+Wireless/status`
- **Network:** `+port`, `+port/1` (firmware-proven lowercase form)
- **New group `LedPwm`:** `ledpwm`, `ledpwm/ChannelInfo`
- **New group `Integrations`:** `FTP`, `RTMP`, `gb28181`, `gat1400`

### 3c. Frontend wiring

No SPA code changes required — the Features panel consumes `ControlPointInventoryReport` +
`TypedSettingsSnapshot` (both contract-driven). The 15 new contracts appear as control points
automatically:

- Booleans → toggle switches (`ledPwmSwitch`, `cordonEnabled`, `gb28181Enabled`, …)
- Enums → dropdowns (`timeZone`, `calendarStyle`, `recordScheduleType`)
- Numerics → sliders / numeric inputs (`ledPwmProject`, `cordonType`, …)
- Read-only capability fields → read-only rows (`faceDetectionSupported`, `stationSignal`)
- Array fields (`channelInfo`, `cordonLines`, `cordonAreas`) → expert/structured rows

`ControlPointInventoryService` already maps the common widget kinds; no `CurrentWidgetByFieldKey`
additions were needed for the new field keys (classifier picks toggle/slider/dropdown from field
kind + enum values).

---

## 4. Validation

| Check | Result |
|---|---|
| `dotnet build BossCamSuite.Linux.sln` | ✅ 0 warnings, 0 errors |
| `dotnet test BossCam.Tests` (full suite) | ✅ 413/413 passed |
| `npm run check` (svelte-check) | ✅ 0 errors, 0 warnings |
| `NativeNetSdkAdapterTests.LanDirectNetSdkRestAdapter_ReadEndpoints_Covers_Full_Catalog` | ✅ extended with 21 new asserts covering every new group |

Two review passes (code-reviewer-deepseek-flash) tightened the change: pass 1 caught a real bug —
the coverage test asserted the firmware-proven lowercase `/NetSDK/Network/port` which was missing
from the read group (only capital-P `Ports`/`Port/1` existed) — fixed by adding `port` + `port/1`
to the Network group — plus two provenance over-reaches (timezone enum included offsets absent
from the binary; `$.bGAT1400` presented as verbatim), both corrected. Pass 2 flagged four writable
read-but-unmapped endpoints (`deviceName`, `deviceAddress`, `AlarmTone`, `AlarmScheduleV2`) and
the unmapped `Network/port` — all five now have seed contracts, and the `RecType` enum labels
carry an inferred-label caveat.

---

## 5. Honest caveats

1. **Field names are firmware-string-proven, not live-write-proven.** `SourcePath` values come
   from `$.field` descriptors and `[%s:%d]` log formats in the binary — the strongest evidence
   short of a live round-trip. A **live 5523-W read/verify pass** (`/api/devices/{id}/normalize`
   + `probe`) will promote the matching fields to `Proven` automatically via fixture evidence.
2. **Two inferred levers:** `video.face.detection`/`video.human.detect` `$.enabled` and
   `network.gat1400` `$.bGAT1400` are inferences (mirroring `bGB28181` convention) — flagged in
   `Notes`, not presented as verbatim.
3. **Timezone enum** reflects the *observed* firmware set (31 offsets, no GMT±00:00 / GMT-12:00).
4. **`network.port` field keys** (`$.httpPort`/`$.rtspPort`/`$.onvifPort`) are inferred from the
   "Port JSON form" catalog description — the endpoint is proven, the key spellings need a live
   round-trip to confirm (same for `AlarmTone[0].tone`).
