# NVR Firmware → 5523-W Camera Surface Revelation — Complete Discovery Report

**Date:** 2026-08-13
**Firmware:** `FWHI102_20240715_W-NVR_K8210-3WS_3_6_2_22_0x62102106_RELEASE.rom` (HiSilicon NVR), cross-referenced against the 5523-W camera ROMs (`IPCAKV3C_20211221_3_6_60_572010`, `IPG5322_W_20211210_3_6_58_572011`)
**Status:** COMPLETE — the NVR firmware is the missing "oracle" for the camera's hidden surface. 37 new camera entry points revealed (43 raw route strings; 6 trailing-slash variants fold under normalization), ~830 editable schema descriptors enumerated, N1/BCAM control plane mapped.
**Artifacts:** `assets/protocols/firmware-surface/` (7 machine-readable surfaces) · `scripts/nvr-firmware-surface-diff.py` · `scripts/nvr-firmware-catalog-append.py`

---

## 1. Executive summary

The 5523-W cameras were designed to pair with this NVR, and the NVR firmware — the `app.out`
application binary inside the decrypted FWHI102 image — is the **only artifact that documents how
the camera's *entire* feature set is driven remotely**. The camera's own `anyka_ipc` binary embeds
route strings and `$.` schema descriptors but nothing that explains their meaning or their protocol.
The NVR's `app.out` fills that gap completely:

- The NVR **names the camera family "BCAM"** and drives every channel through a JSON-RPC control
  plane called **`IPC.API`** carried over **`/rpc/p2p`** (KP2P/EseeCloud P2P) and `/rpc/netsdk`
  (LAN), with **`R.BCAM.*`** method calls (`WorkMode`, `UsageScenarios`, `WirelessCheck`) and a
  **`IPCAPI_*`** verb surface (`IPCAPI_GET_IPC_ALARM`, `IPCAPI_SET_ALARM_SCHEDULE_V2`,
  `IPCAPI_GET_MOTION_HUMANFACE`, `IPCAPI_SET_LIGHT_MANCTRL_V2`, …) that mirror the camera's
  `N1Device_Evt*` callback surface.
- The NVR stores per-camera identity/state in **`$.Channel.IPCamInfo[%d]`** — including fields
  never exposed by the camera's own catalog: `N1.Eseeid`, `N1.Devid`, `N1.OtaMagic` (the OTA
  upgrade token), `AddType`, `WorkMode`, `MediaProtocolVer`, `OtaMagic`, `SupportPir` —
  revealing the **OTA/upgrade handshake** and the **pairing state machine**.
- **43 camera NetSDK routes** that were absent from the 140-entry catalog are now proven to exist
  in the camera firmware: the entire **Image** plane (`irCutFilter`, `wdr`, `denoise3d`,
  `manualSharpness`, `videoMode`), **Audio** channels, **System module** plane (`bluetooth`,
  `gsensor`, `gsensor/calibration`), **time/ntp**, **operation** (`reboot`, `remoteUpgrade`,
  `default`), **Factory**, **ProductionResult** (`WIFITest`, `ImageTest`, `SFPTest`,
  `FinishedTest`, `FinishedOldTest`), **RangeUpload** (`Firmware`, `UploadPT`), and `Network/ESee`.
- The camera exposes **~830 `$.` schema descriptors** with typed metadata
  (`*.Property.type/.mode/.opt[n]/.max/.min/.def`) — this is the definitive editable-parameter
  vocabulary, superset of everything wired in BossCamSuite today.
- **37 NetSDK routes** new to the 140-entry catalog (43 raw strings, of which 6 are
  trailing-slash variants of the same endpoint) are now cataloged — see §4 and §7.

This report is the complete map: every option, feature, editable parameter, and their wire/control
meaning, as revealed by the NVR.

---

## 2. The cipher & extraction (recap — full detail in `2026-08-11-firmware-full-extraction-report.md`)

- NVR ROM is **repeating-key XOR (period 64)**; the key was recovered from the repeated 64-byte
  block in a blank partition region and is committed in the extraction report. Decrypted image:
  `extracted/FWHI102/FWHI102_decrypted.rom`.
- Carving yields: u-boot, **Linux 4.9.84 kernel**, bootfs SquashFS, and the **appfs** whose main
  binary `/app/app.out` (9.42 MB ARM ELF) links the **miv100 (MStar/MSR621x)** SDK, **libEsee**,
  **KP2P** cloud stack, plus `/bin/cgi.app`, `/web/` UI, `config_ini/` (smtp, qrcode, gui, 3G APN).

All strings used below were mined with `strings -n 6` from:
- `anyka_ipc` (both camera ROMs; 14,923/16,317 strings)
- `app.out` (NVR; 63,347 strings)

Artifact files (committed):
| File | Content |
|---|---|
| `firmware-surface/5523w_schema_descriptors.txt` | 834 unique `$.` descriptors from anyka_ipc |
| `firmware-surface/5523w_netsdk_routes.txt` | 72 unique `/NetSDK/…` route strings |
| `firmware-surface/5523w_n1_protocol_surface.txt` | 173 `N1Device_*` protocol calls |
| `firmware-surface/nvr_ipcapi_surface.txt` | 41 `IPCAPI_*` verbs the NVR calls on cameras |
| `firmware-surface/nvr_rpc_methods.txt` | 188 `R.*` JSON-RPC methods |
| `firmware-surface/anyka_ipc_strings.txt` | full camera string table |
| `firmware-surface/nvr_app_out_strings.txt` | full NVR string table |

---

## 3. The BCAM control plane (the NVR→camera protocol, revealed)

This is the single biggest discovery: **the NVR talks to 5523-W-class cameras over an N1/KP2P
JSON-RPC channel**, and the camera binary implements the matching `N1Device_*` surface.

### 3.1 Channel + method dispatch

| Evidence (app.out) | Meaning |
|---|---|
| `NK_JSONAPI_SyncCallEx("/rpc/p2p", "IPC.API", "R.BCAM.WorkMode", chn+1, req, rsp)` | Per-channel JSON-RPC over P2P |
| `NK_JSONAPI_SyncCallEx("/rpc/p2p", "IPC.API", "R.BCAM.UsageScenarios", …)` | Usage-scenario query |
| `NK_JSONAPI_SyncCallEx(NULL, "IPC.API", "R.BCAM.WirelessCheck", chn+1, …)` | Wireless check |
| `NK_JSONAPI_SyncCallEx(…, "R.WifiScanApList")`, `"R.AutoWifiChannel"`, `"R.Restore.FactorySetting"`, `"R.UpdateSoftProbe"`, `"R.RecordBackup.Progress"` | Other RPC methods |
| `NK_N1_BCAM_COMM_get_ipc_info(NK_N1_BCAM_GetChn(stToken.szip), …)` | N1 comm layer, channel from token |
| `chn[%d] attach bcam success` / `detach bcam success` | BCAM session attach lifecycle |
| `CH%d::R.BCAM.WorkMode` / `CH%d::R.BCAM.WorkModeDuration` / `CH%d::R.BCAM.UsageScenarios` | Log framing per channel |
| `ch%d update bcam::capability(ver:0x%x)` / `ch%d update bcam version: %s` | Capability sync on attach |
| `$.Stat.IPC[%d].BcamOnline` | Per-channel online state |
| `bcm-logo auto update daemon`, `bcam auto wakeup daemon`, `bcam-notify` | Background daemons |

### 3.2 The camera side (`anyka_ipc` implements N1)

The camera exports the full N1 server surface (173 symbols, all in
`5523w_n1_protocol_surface.txt`). Grouped:

**Product/OEM controls (the "hidden" hardware features):**
- `N1Device_EvtProductGet/Set{InfraRedLevel, WhiteLightLevel, OemInfraRedLevel, OemWhiteLightLevel, OemLightControl, MirrorFlip, PhotoSensitive, PirTrigger, SoundPrompt, NoiseReduction, MotionRec, OsdOption, KeyPressStatus, AudioInputMode, AudioIO, Vendor, IRCutControlMode, IrcutMode, IRCutSoftWare, OemVideoHumanDetect, OemPTZSetting, OemPTZExternalControl, WifiRtxInfo, WirelessMac}`
- `N1Device_EvtProduct{SetInfraRedSwitch, SetTmpIrCutMode, LightMode, UpdateModel, Get/WriteSnData, Get/ReadUID, ExtractE2PROM, GetAgingTest, GetIO, PutIO, GetonProductionResult, SetonProductionResult}`

**Analytics:**
- `N1Device_EvtGetVideoEncoder` / `EvtSetVideoEncoder`, `N1Device_EvtGetIRCutFilter` / `EvtSetIRCutFilter`
- `N1Device_Cordon` / `EvtGetCordon` / `EvtSetCordon`, `N1Device_EvtProductGetOemVideoHumanDetect`

**VRCam (virtual-reality / fisheye mount):**
- `N1Device_EvtVRCamGetCameraInstalling/SetCameraInstalling`, `GetFishEyeType`,
  `GetFishEyeCalibration/SetFishEyeCalibration` (+ `Calibration2`), `GetGensorCalibration`,
  `SetGensorEnabled`

**Protocol config:**
- `N1Device_EvtGetGb28181Config/SetGb28181Config`, `EvtGetGat1400Config/SetGat1400Config`
- `N1Device_Onvif`, `N1Device_EvtOnvif{Get,Set,AutoIPAdapt}`, `EvtOnvifActivedAutoIPAdapt`
- `N1Device_IPCR` (`EvtIPCRGetRID/SetRID/KeepAlive`), `N1Device_Infotmic`, `N1Device_GAT1400`, `N1Device_GB28181`

**User/TF-card/stream:**
- `N1Device_UserManage` (`AddUser/EditUser/RemoveUser/HasUser/NumberOfUser/IndexUser`), `N1Device_EvtUserManage*`
- `N1Device_TFCard` (`EvtTFCard{Status,Touch,Read,Write,Remove,Format,RWstatus,DelRecordFiles}`)
- `N1Device_EvtAttachStream/DetachStream/onLive{Connected,Disconnected,ReadFrame,AfterReadFrame}`,
  `EvtRecommendStream`, `EvtPortChanged`
- `N1Device_Monitor` (`EvtMonitorGetCPUUsage/GetMemoryUsage`)
- `N1Device_TwoWayTalk` (`SendG711/SendH264/SendH26x/SendHEVC`), `N1Device_AirPair`,
  `N1Device_ClientPair`, `N1Device_PairWiFiNVR`, `N1Device_Bluetooth`, `N1Device_Discovery{Listener,Reset}`
- `N1Device_GetNonce` (auth nonce), `N1Device_Get{ID,IDV2,UID,3rdUID,DeviceID,DeviceModel,DeviceVersion,ESeeID}`,
  `N1Device_HasUser`, `N1Device_EvtReboot`, `N1Device_EvtReset`, `N1Device_EvtSetNetWorkMode`,
  `N1Device_EvtSetEthConfig`, `N1Device_EvtDetectRJ45Connected`, `N1Device_Upgrade`,
  `N1Device_EvtCatchException`, `N1Device_UptimeNano`

### 3.3 The `IPCAPI_*` verb surface (NVR → camera, protocol-agnostic adapter)

`IPCAPI_CALL_API(protocolName, IPCAPI_*, …)` — the NVR abstracts camera brands behind
`szProtocolName` adapters. Verbs proven in `nvr_ipcapi_surface.txt`:

| Verb | Meaning |
|---|---|
| `IPCAPI_GET_IPC_ALARM` / `_OLD` | Motion/alarm config (V1/V2) |
| `IPCAPI_GET_MOTION_CONFIG` | Motion detection config |
| `IPCAPI_GET_MOTION_HUMANFACE` | Human/face detection config |
| `IPCAPI_GET_ALARM_SCHEDULE_V2` / `IPCAPI_SET_ALARM_SCHEDULE_V2` | Alarm schedule |
| `IPCAPI_GET_ALARM_SWITCH_STATUS_V2` | Alarm switch state |
| `IPCAPI_SET_ACTIVE_DEFENSE` | Arming/disarming |
| `IPCAPI_GET/SET_SOUND_ALARM_V2` | Sound alarm |
| `IPCAPI_GET/SET_SLIGHT_ALARM_V2` | (Security) light alarm |
| `IPCAPI_GET/SET_RB_ALARM_LIGTH` | Red/blue (RB) alarm light |
| `IPCAPI_SET_LIGHT_MANCTRL_V2` | Light manual control |
| `IPCAPI_SET_RBLIGHT_MANCTRL_V2` | RB light manual control |
| `IPCAPI_SET_SOUND_MANCTRL_V2` | Sound manual control |
| `IPCAPI_GET_VIDEO_COVER` | Privacy/video-cover regions |
| `IPCAPI_ONVIF{G,S,U,A}` | ONVIF adapter get/set/update/… |

**Meaning for BossCam:** every one of these has a `$.`-schema equivalent on the camera
(§4) — the NVR is a fully-fledged second client of the camera's NetSDK/N1 surface, and its
dialog set (`*.lui`, §6) tells us exactly what the vendor considers "the features".

---

## 4. The camera's editable parameter vocabulary (834 `$.` descriptors)

`5523w_schema_descriptors.txt` holds the complete sorted list. Every descriptor that carries
`*.Property.{type,mode,opt[n],max,min,def}` has schema metadata; 323 metadata lines were found.
Root groups (by descriptor count):

```
57 Function   44 videoMode   25 RecordSchedule   24 SnNumberDate   24 irCutFilter
22 Capabilities   21 AlarmSchedule   20 expandChannelNameOverlay   15 angles   13 LightAlarm
12 languageProperty   8 videoEncodeChannel   8 ntp   7 WDR   7 sensitivityStepProperty
7 manualSharpness   7 denoise3d   7 datetimeOverlay   7 audioEncodeChannel
6 lowlightModeProperty   5 sceneModeProperty   5 imageStyleProperty   5 externalControl
5 exposureModeProperty   5 channelInfo   5 CGREG   5 calibration   5 BLcompensationModeProperty
5 awbModeProperty   … (full counts in script output / descriptor file)
```

### 4.1 The `$.Function.*` group — the OEM feature register (57 entries)

The single most informative group — every hardware/software feature the ODM can toggle:

| Descriptor | Meaning |
|---|---|
| `$.Function.infraRedLevel` / `whiteLightLevel` | IR / white-light intensity |
| `$.Function.irCutControlMode` / `irCutMode` / `softwareIrOption.{dayToNightLevel,nightToDayLevel}` | IR-cut / day-night |
| `$.Function.mirrorEnabled` / `flipEnabled` | Mirror/flip |
| `$.Function.photosensitive.{nphotosensitiveType,Low,High,nDayLevel}` | Light sensor (photoresistor) thresholds |
| `$.Function.pirEnabled` / `pirTrigger` | PIR sensor |
| `$.Function.promptSoundType` / `microphoneType` | Prompt tones / mic type |
| `$.Function.audioInputGain/Volume` / `audioOutputGain/Volume` | Audio levels |
| `$.Function.bMotionRec` / `bNoiseReduction` | Motion recording / 3D-NR |
| `$.Function.hdEnabled` / `hdDrawRegion` / `hdSensitivityStep` | Human detection |
| `$.Function.irfraRedToAlarm` / `ledToLight` | IR→alarm, LED→light coupling |
| `$.Function.osd.{bShowDateOsd,bShowNameOsd,nDateOsdx,nDateOsdy,nNameOsdx,nNameOsdy,strNameOsd}` | OSD |
| `$.Function.ptzHorizontal{AngleMax,StepMax,DefaultSpeed,SelfCheckSpeed}` / `ptzVertical{…}` / `ptzBeat` | PTZ axis limits & speed (digital PTZ) |
| `$.Function.vendor` | OEM vendor tag |
| `$.Function.softwareIrOption` | Software IR-cut option |

### 4.2 Capability register — `$.Capabilities.*` (the feature flags)

```
SupportFaceDetect, MaxFaceDetectNum, SupportHumanDetect, MaxHumanDetectNum, supportPIR,
Http-Port, mediaProtocolVer, OdmCode, MaxHardDiskDrivers, MaxTFCards, SupportAutoRepeater,
SupportUserManage, spFisheye, spLimitMatch, spLinkvisual, spLte, spMatchCode, spOneNet,
spPtUpload, spQRPair, spSoundPair, spWifiPair
```

The `sp*` set is the **pairing/onboarding feature matrix** shared verbatim with the NVR
(`$.Capabilities.spFisheye … spWifiPair` appear in both binaries) — see §5.

### 4.3 Image plane (all new to the catalog)

| Descriptor | Route | Meaning |
|---|---|---|
| `$.irCutFilter.{irCutControlMode,irCutMode,softwareIrOption.*}` | `/NetSDK/Image/irCutFilter` | IR-cut control |
| `$.WDR.{enabled,WDRStrength}` | `/NetSDK/Image/wdr` | Wide dynamic range |
| `$.denoise3d.{enabled,denoise3dStrength}` | `/NetSDK/Image/denoise3d` | 3D noise reduction |
| `$.manualSharpness.{enabled,sharpnessLevel}` | `/NetSDK/Image/manualSharpness` | Sharpness |
| `$.videoMode.{cellMode,fixMode,tableMode,wallMode,FixParam[%d].{id,AngleX,AngleY,AngleZ,CenterCoordinateX,CenterCoordinateY,Radius}}` | `/NetSDK/Image/videoMode` | **Fisheye dewarp modes** (ceiling/wall/table) with per-scene fix params |
| `$.lowlightMode`, `$.sceneMode`, `$.imageStyle`, `$.exposureMode`, `$.awbMode`, `$.BLcompensationMode` | (existing Image routes) | Night mode, scene presets, style, exposure, AWB, backlight compensation |
| `$.calibration.enabled` | `/NetSDK/System/module/gsensor/calibration` | G-sensor calibration |
| `$.snNumberDate`, `$.datetimeOverlay`, `$.deviceIDOverlay`, `$.channelNameOverlay.{enabled,regionX,regionY}`, `$.expandChannelNameOverlay[0..3]` | (existing overlay routes) | OSD overlays incl. 4 extra channel names |

### 4.4 PTZ

- `$.angles.{anglesX,anglesY,anglesZ}` + `spPtz{Horizontal,Vertical}{AngleMax,StepMax}` →
  **the 5523-W has a digital PTZ with axis limits and step control** (`RESTful_NetSDKPTZChannelIDSetup_OnGET/PUT`
  handler proven in binary).
- `$.externalControl`, `$.guardPos.{enableGuard,presetIndex,schedule,stayTime}`, `$.MaxPreset` →
  guard positions / presets, `$.Function.ptzBeat` keepalive.

### 4.5 Audio

- `$.audioEncodeChannel[0].{id,codecType}` (+`opt[]` codec list), `$.AudioInCodec/AudioOutCodec`
  (ODM), `$.spAudioInput/Output{Gain,Volume}`, `$.spMicrophoneType`, `$.spPromptSoundType`,
  `$.Function.audioInputGain …` → **two-way audio is fully configurable** (`N1Device_TwoWayTalk`).

### 4.6 Schedules & recording

- `$.RecordSchedule[%d].{Enabled,RecType(opt 0..3),Weekday,BeginTime,EndTime}`,
  `$.AlarmSchedule[%d].{Enabled,nType,Weekday,BeginTime,EndTime}`, `$.AlarmScheduleV2`,
  `$.LightAlarm.{Enabled,Mode,DurationSec}`, `$.BitSchedule`, `$.TRangeSchedule[%d].RecType`.

### 4.7 Network / time / services

- `$.ntp.{ntpEnabled,ntpServerDomain}`, `$.bServerSyncEnble`, `$.nServerSyncType/Time`,
  `$.calendarStyle`, `$.timeZone` (31-offset enum), `$.ApMode.{Essid,Psk,Channel}`, `$.ApEssid`,
  `$.ApPsk`, `$.AllowRJ45/AllowWiFiAP/AllowWiFiRepeater/AllowWiFiStation`, `$.StaMode`,
  `$.eseeID`, `$.bEnableFTP`, `$.bFtpSchduleEnable`, `$.stFtpSchedule`, `$.sip*` (GB28181),
  `$.nonce` / `$.Auth.{usr,pwd,ticket,error}` (auth challenge), `$.Network.WIFI.WifiRegion`,
  `$.wlan.region`.

---

## 5. Pairing & onboarding state machine (sp* matrix)

The NVR's quick-start flow (dialogs `quick_matchcode.lui`, `camera_search.lui`,
`camera_poweron_tips.lui`) plus the shared `sp*` capability flags reveal five onboarding paths:

| Capability flag | Pairing path |
|---|---|
| `spQRPair` | QR-code pairing (`qrcode.conf` links: wechat, localip `index.cgi?username=…&type=localip`, `%ESEEID%`, support web) |
| `spMatchCode` | Match-code pairing (`quick_matchcode.lui`; `ch%d-qrmatch eseeid(…) ip(…)`) |
| `spSoundPair` | Sound pairing (`N1Device_AirPair`) |
| `spWifiPair` | WiFi-repeater pairing (`wifi_repeater_setup.lui`, `N1Device_PairWiFiNVR`) |
| `spOneNet` / `spLte` / `spLinkvisual` / `spLimitMatch` | OneNet/LTE/LinkVisual cloud or region-gated paths |
| `spFisheye` | Fisheye (dewarp) variant |
| `spPtUpload` | PT upload (camera-only flag) |

NVR stores the outcome in `$.Channel.IPCamInfo[%d].{AddType,N1.Eseeid,N1.Devid,N1.OtaMagic,
IPAddr,MACAddr,Port,Username,Password,Protocolname,Modelname,SWVersion,WorkMode,WorkModeDuration,
MediaProtocolVer,DevType,InterfaceType,Enable,Cooldown,BufferStratage,SupportFaceDetect,
SupportHumanDetect,SupportPir}` and refreshes `$.Stat.ChnCapability[%d].*` on attach.

---

## 6. The NVR feature surface as an "oracle" for the camera's intended features

The NVR's 150+ GUI dialogs (`app/dialog/*.lui`) enumerate the vendor's intended feature set —
each maps to a camera-side schema group:

| NVR dialog | Camera feature it drives |
|---|---|
| `camera_advanced_setup`, `ipc_status_detail`, `new_ipc_ipsetup` | IPCamInfo / network config |
| `ipc_upgrade`, `install_firmware`, `updating_your_firmware` | `N1Device_Upgrade`, `/NetSDK/System/operation/remoteUpgrade`, OTA (`OtaMagic`) |
| `mdarea`, `detection_setup`, `detection_period_setting`, `custom_detection_period_setup`, `one_period_setup` | motion detection grid + schedules |
| `privacy_area`, `set_area` | video cover (`IPCAPI_GET_VIDEO_COVER`) |
| `ptzcontrol`, `zoomin` | digital PTZ |
| `recordmode`, `record_schedule_setting`, `record_setting`, `quick` | record schedules (`IPCAPI_SET_ALARM_SCHEDULE_V2`) |
| `soundalarm`, `new_volume`, `new_color` | sound/light alarms |
| `wificonnection`, `new_wifinetwork`, `new_wifisetup`, `wifi_repeater_setup` | wireless station/AP/repeater |
| `quick_matchcode`, `camera_search` | pairing |
| `smtp_setup`, `contact` | email alerts |
| `storage`, `format`, `backup`, `playback` | TF-card + playback |
| `system_diagnostics`, `health_check`, `produce_test` | `N1Device_Monitor`, production tests |
| `factory_reset0/1`, `reset_values` | factory reset |
| `guide_*` (8 dialogs) | onboarding wizard |

Config files backing these: `smtp_conf.ini` (9 providers: 163/126/QQ/sina/hotmail/yahoo/icloud/
gmail/sohu/outlook, `EncryptType 0=NONE 1=STARTTLS 2=SSL`), `hock_main_system_3gsetup.ini`
(3G/4G APNs for India + China carriers), `qrcode.conf`/`QRCodeODM.ini` (pairing QR links and
white-label visibility), `ddns.conf` (DDNS provider schema), `gui_config.ini`, `user.conf`
(admin default with empty password; 18 `USER_AUTH_*` permission bits).

---

## 7. Catalog & artifacts update

- **`endpoint_catalog.json`: 140 → 177 entries.** 37 camera routes that were missing now have
  entries with verbatim provenance and their `$.` schema field lists (generated by
  `scripts/nvr-firmware-catalog-append.py`; idempotent — skips existing under shared
  normalization). The original route diff listed 43 raw NEW forms; the remaining 6 were
  trailing-slash / parameter variants that fold onto the appended entries under the canonical
  normalization (e.g. `/NetSDK/Audio/encode/channel` and `/NetSDK/Audio/encode/channel/`), so a
  rerun of the diff after the append reports **0 NEW** (see §9). Both scripts share one
  normalization routine (`{0}`-style placeholders and numeric segments fold to the same token),
  so the workflow is rerunnable as firmware evolves.
- **New artifact directory `assets/protocols/firmware-surface/`** with the 7 machine-readable
  surfaces listed in §2.
- **New scripts** `nvr-firmware-surface-diff.py` (diff any route file vs catalog) and
  `nvr-firmware-catalog-append.py` (append new entries) — rerunnable as firmware evolves.

---

## 8. Honest caveats & next steps

1. **Not live-verified.** All 43 new endpoints are firmware-proven (strings present) but were not
   probed against the fleet. The 2026-08-11 live pass proved several firmware-string guesses
   wrong on the wire (bare-scalar documents, GET-gated routes); the same caution applies here.
   `scripts/5523w-surface-verify.py` should be extended to probe: `Image/irCutFilter`,
   `Image/wdr`, `Image/denoise3d`, `Image/manualSharpness`, `Image/videoMode`, `time/ntp`,
   `System/module/gsensor/calibration`, `System/operation/reboot`, `Audio/encode/channel/0`,
   `Network/ESee`.
2. **Production endpoints** (`/NetSDK/Factory`, `ProductionResult/*`, `RangeUpload/*`,
   `Production/SetEncryptionChip`) are factory-tooling surfaces — mark as danger/read-only in
   BossCam UI; `SetEncryptionChip` and `RangeUpload/Firmware` can alter device identity/state.
3. **`cgi.app`, `JaViewer.swf`, `WebClient.exe` yielded no additional surface** — the web bundle
   (`build.js`) contains no `/NetSDK/` strings (the NVR drives cameras exclusively over N1/P2P,
   not via camera web CGI); the camera's own web CGI is in `anyka_ipc` (`NetSDK_CGI_*` handlers
   proven: `OnCustomOEM`, `OnCustomOemCapabilities`, `OnFactory`, `OnProduction`,
   `OnWirelessStatus`, `ProductionResult`).
4. **`OtaMagic` / OTA handshake** is the strongest next target: `N1Device_Upgrade` +
   `$.Channel.IPCamInfo[%d].N1.OtaMagic` + `/NetSDK/System/operation/remoteUpgrade` +
   `$.Product.Conf.OnlineUpgrade.{FirmwareMagic,Get,Host,Port,Url,Ver}` (NVR) define the whole
   upgrade chain — reversing `NK_IPCUPGRADE_OnlineCheckFw` in `app.out` would yield the OTA
   endpoint + signing check.
5. **VRCam g-sensor/fisheye calibration** (`N1Device_EvtVRCam*`) implies the 5523-W chassis has
   a g-sensor (motion/orientation) and fisheye optics variants — probe `videoMode` to confirm
   which dewarp modes respond.

---

## 9. Validation

| Check | Result |
|---|---|
| `endpoint_catalog.json` parses | ✅ 177 entries valid JSON |
| Catalog diff rerun after append | ✅ 0 NEW (72/72 camera routes in-catalog) |
| Catalog append script rerun | ✅ idempotent — skips existing, reproduces 177 |
| `dotnet build BossCamSuite.Linux.sln` | ✅ 0 warnings, 0 errors |
| Catalog/contract/NativeNetSdk test subset | ✅ 33/33 passed |
| Code review (code-reviewer-deepseek-flash) | ✅ two passes — normalization parity, `{0}`-placeholder convention, and parent-route reproducibility all fixed |
