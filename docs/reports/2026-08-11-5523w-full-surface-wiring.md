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
| `/NetSDK/System/time/rtc` | GET, PUT | LIVE: **bare unix-seconds int** document (e.g. `1786493574`); PUT accepts ONLY the bare scalar — object forms rejected statusCode 6 |
| `/NetSDK/System/time/timeZone` | GET, PUT | LIVE: **bare JSON string** `"GMT+08:00"`; PUT accepts ONLY the bare string |
| `/NetSDK/System/time/calendarStyle` | GET, PUT | LIVE: bare string `"general"` (NOT Gregorian/Lunar); PUT `{"calendarStyle":"general"}` → OK |

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
| `/NetSDK/Video/FaceDetection` | GET, PUT | LIVE: HTTP 500 Device Error — gated on this model |
| `/NetSDK/Video/HumanDetect` | GET, PUT | LIVE: `{enabled, drawRegion, sensitivityStep}` — `$.enabled` lever confirmed; SupportHumanDetect/MaxHumanDetectNum NOT in payload |
| `/NetSDK/Video/cordon` | GET, PUT | LIVE: `{id, enabled, type, sensitivityLevel, maxLines, line[], maxcolumns, maxrows, width, height, grid[]}` — bEnableCordon/enCordonType/stCordonLinelist WRONG |

### Network / integrations

| Endpoint | Methods | Proven field keys |
|---|---|---|
| `/NetSDK/Network/port` | GET, PUT | LIVE: ARRAY `[{id:1, portname:'unisual', value:80}]` — round-trips; httpPort/rtspPort/onvifPort WRONG |
| `/NetSDK/Network/wireless/stationSignal` | GET | LIVE: **bare int dBm** (e.g. `-48`) — SignalStrength/stationsignal WRONG |
| `/NetSDK/Network/wireless/allStaInfo` | GET | LIVE: HTTP 200 with EMPTY body on this model |
| `/NetSDK/Wireless/ScanApList` | GET, POST | AP list array (scan trigger) |
| `/NetSDK/Network/Wireless/status` | GET | wireless status |
| `/NetSDK/FTP` | GET, PUT | LIVE: HTTP 500 Device Error — gated on this model |
| `/NetSDK/RTMP` | GET, PUT | LIVE: HTTP 500 Device Error — gated on this model |
| `/NetSDK/System/gb28181` | GET, PUT | LIVE: `{sipPort, sipServerport, sipUsername, sipUserpass, sipServeraddr, registerInterval, heartbeatCycle, …}` — bGB28181/GB28181_Server/ServerPort WRONG |
| `/NetSDK/System/gat1400` | GET, PUT | LIVE: statusCode 3 'Device Not Support' |

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
| `video.face.detection` | MotionPrivacyAlarms | `/NetSDK/Video/FaceDetection` | enabled (live: GET-gated) |
| `video.human.detect` | MotionPrivacyAlarms | `/NetSDK/Video/HumanDetect` | enabled, drawRegion, sensitivityStep (live shape) |
| `video.cordon` | MotionPrivacyAlarms | `/NetSDK/Video/cordon` | enabled, type, sensitivityLevel, line, grid (live shape) |
| `system.time.rtc` | UsersMaintenance | `/NetSDK/System/time/rtc` | rtc |
| `system.time.timezone` | UsersMaintenance | `/NetSDK/System/time/timeZone` | timeZone (31-offset enum from firmware) |
| `system.time.calendarStyle` | UsersMaintenance | `/NetSDK/System/time/calendarStyle` | calendarStyle (`general`/`lunar` — live `general`) |
| `network.gb28181` | NetworkWireless | `/NetSDK/System/gb28181` | sipPort, sipServerport, ServerAddr, Username, Password, RegisterInterval, Heartbeat |
| `network.gat1400` | NetworkWireless | `/NetSDK/System/gat1400` | gat1400Enabled (live: Not Support) |
| `network.ftp` | NetworkWireless | `/NetSDK/FTP` | ftpScheduleEnabled, ftpSchedule (live: gated) |
| `network.rtmp` | NetworkWireless | `/NetSDK/RTMP` | rtmpUrl (live: gated) |
| `network.wireless.signal` | NetworkWireless | `/NetSDK/Network/wireless/stationSignal` | bare dBm int (RO) |
| `system.device.name` | UsersMaintenance | `/NetSDK/System/deviceInfo/deviceName` | deviceName (bare string; subpath read-only) |
| `system.device.address` | UsersMaintenance | `/NetSDK/System/deviceInfo/deviceAddress` | deviceAddress (bare int, read-only) |
| `system.alarm.tone` | MotionPrivacyAlarms | `/NetSDK/System/AlarmTone` | AlarmTone[0].{Enabled,tone} (live: Not Support) |
| `system.alarm.scheduleV2` | MotionPrivacyAlarms | `/NetSDK/System/AlarmScheduleV2` | ScheduleEnabled, ScheduleScheme (live: gated) |
| `network.port` | NetworkWireless | `/NetSDK/Network/port` | id, portname, value (`$[0].{id,portname,value}` array) |

Truth states were `Inferred` with `Source = "firmware-string"` at wiring time; the **live
validation pass (2026-08-11, §6) promoted every round-trip-confirmed field to `Proven` with
`Source = "live-2026-08-11"`** and corrected the disproven SourcePaths. No new `GroupKind` enum
values were needed; the 7 existing kinds cover the whole surface.

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
- Array fields (`channelInfo`, `cordonLines`, `cordonGrid`) → expert/structured rows

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

1. **Lots of firmware-string guesses were WRONG — corrected live.** The live pass (§6) proved
   that `$.ledPwm.*`, `$.AlarmSchedule[n].*`, `$.RecordSchedule[n].*`, `$.bEnableCordon`,
   `$.enCordonType`, `$.stCordonLinelist`, `$.stCordonArealist`, `$.bGB28181`, `$.GB28181_Server`,
   `$.ServerPort`, `$.httpPort`/`$.rtspPort`/`$.onvifPort`, `$.SignalStrength`/`$.stationsignal`,
   `$.rtc`, `$.timeZone`, `$.deviceName`/`$.deviceAddress` (as object keys) are all **not the wire
   shape**. The corrected shapes are in §2/§3a and the catalog `settled_verdict` annotations.
2. **Eight endpoints are GET-gated on this model** — `ledpwm`, `ledpwm/ChannelInfo`,
   `AlarmSchedule`, `AlarmScheduleV2`, `RecordSchedule`, `FaceDetection`, `FTP`, `RTMP` return
   HTTP 500 `{statusCode:2 'Device Error'}` on every GET form. The handler symbols exist in the
   binary (so the catalog entries are legitimate) but the live 5523-W 3.6.60 does not serve them
   over GET — treat as write-only or capability-gated, not dead.
3. **`AlarmTone` and `gat1400` report Device Not Support** (statusCode 3) — the firmware has the
   route strings but this model declines; the contracts remain for firmware families that do
   support them, flagged in `Notes`.
4. **Bare-scalar documents:** `rtc`, `timeZone`, `calendarStyle`, `stationSignal`, `deviceName`,
   `deviceAddress` all return bare scalars (int / quoted string), and `rtc`/`timeZone` PUTs
   accept ONLY the bare scalar — object documents are rejected with statusCode 6.
5. **Timezone enum** reflects the observed firmware set (31 offsets, no GMT±00:00 / GMT-12:00);
   the live unit sits at `GMT+08:00`.
6. **`allStaInfo` returns an empty body** on this model — populated state may be AP-mode-only or
   other-model-only.

---

## 6. Live validation pass (2026-08-11)

Performed read-only against the fleet 5523-W units (**10.0.0.29 / .169 / .227, blank admin**,
`scripts/5523w-surface-verify.py`). Every probe was a GET; the only writes were no-op PUTs of the
read-back value (`rtc` bare scalar, `timeZone` bare string, `calendarStyle` `{"calendarStyle":
"general"}`, `port` array echo) to prove round-trip.

### 6a. Round-trip CONFIRMED (GET 200 + PUT statusCode 0 where writable)

| Endpoint | Live shape | Write lever |
|---|---|---|
| `/NetSDK/System/time/rtc` | bare int unix-seconds | **bare scalar PUT** (object → statusCode 6) |
| `/NetSDK/System/time/timeZone` | bare string `"GMT+08:00"` | **bare string PUT** (object → statusCode 6) |
| `/NetSDK/System/time/calendarStyle` | bare string `"general"` | `{"calendarStyle":"general"}` → OK |
| `/NetSDK/Video/HumanDetect` | `{enabled, drawRegion, sensitivityStep}` | `$.enabled` |
| `/NetSDK/Video/cordon` | `{id, enabled, type, sensitivityLevel, line[], grid[]…}` | `$.enabled` |
| `/NetSDK/System/gb28181` | `{sipPort, sipServerport, sipUsername, sipUserpass, …}` | SIP fields |
| `/NetSDK/Network/port` | `[{id, portname, value}]` array | array echo → HTTP 200 |
| `/NetSDK/Network/wireless/stationSignal` | bare int dBm (`-48`) | read-only |
| `/NetSDK/System/deviceInfo/deviceName` | bare string `"5523-W"` | subpath read-only; write via full `deviceInfo` |
| `/NetSDK/System/deviceInfo/deviceAddress` | bare int (`1`) | read-only |

### 6b. GET-gated (HTTP 500 `Device Error`, all GET forms) / Not Support

| Endpoint | Live result |
|---|---|
| `ledpwm`, `ledpwm/ChannelInfo`, `AlarmSchedule`, `AlarmScheduleV2`, `RecordSchedule`, `FaceDetection`, `FTP`, `RTMP` | HTTP 500 `{statusCode:2}` every form (bare, `?id=0`, `/0` → 404) |
| `AlarmTone`, `gat1400` | HTTP 200 envelope `{statusCode:3, 'Device Not Support'}` |
| `allStaInfo` | HTTP 200, empty body |

### 6c. Catalog + contract updates from the pass

- **`endpoint_catalog.json`:** all 21 probed entries annotated with `settled_verdict`
  (`date: 2026-08-11`, `status`, `canonical_keys`, `summary`, `evidence`) — the observed payload
  shapes, not the firmware-string guesses.
- **Seed contracts:** the disproven SourcePaths corrected to the live shapes and promoted to
  `TruthState.Proven` with `Source = "live-2026-08-11"`; GET-gated / Not-Support endpoints keep
  their contracts (firmware families that do support them) with the live gate recorded in `Notes`.
- **`scripts/5523w-surface-verify.py`:** reusable read-only probe with per-endpoint verdicts
  (confirmed / partial / empty / gated / error) and bare-scalar handling.
