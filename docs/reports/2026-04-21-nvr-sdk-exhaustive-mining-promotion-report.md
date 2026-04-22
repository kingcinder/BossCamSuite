# 2026-04-21 NVR SDK Exhaustive Mining + Promotion Report

## Scope
Executed exhaustive mining on:
- `C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8`
- `C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8.zip`
- CHM expanded and re-parsed in this pass.

## Exhaustive extraction outputs
Folder:
- `artifacts/sdk-mining/nvr_exhaustive`

Generated:
- `nvr_file_manifest.csv`
- `nvr_text_corpus_paths.txt`
- `nvr_functions_catalog.json`
- `nvr_define_catalog.json`
- `nvr_struct_catalog.json`
- `nvr_enum_catalog.json`
- `nvr_token_index.json`
- `nvr_dll_symbol_candidates.json`
- `nvr_semantic_clues.json`
- `nvr_to_bosscam_field_hits.json`
- `nvr_exhaustive_summary.json`
- `nvr_relevant_define_slice.json`
- `nvr_relevant_struct_slice.json`

Cross-check artifacts:
- `artifacts/sdk-mining/nvr_dll_exports.txt`
- `artifacts/sdk-mining/nvr_api_coverage_report.json`
- `artifacts/sdk-mining/nvr_export_coverage_report.json`

## Extraction counts
From `nvr_exhaustive_summary.json`:
- manifest files: 138
- text documents parsed: 187
- HISI functions: 68
- defines/constants: 360
- structs: 75
- enums: 9
- token index entries: 192
- DLL symbol candidates: HISISDK.dll=69, avlib.dll=9

## High-value semantic findings (relevant to objectives)
- Config families confirmed:
  - `HISI_DVR_GET_DEVICECFG / SET_*`
  - `HISI_DVR_GET_ENCODECFG / SET_*`
  - `HISI_DVR_GET_NETCFG / SET_*`
  - `HISI_DVR_GET_DETECTIONCFG / SET_*`
  - `HISI_DVR_GET_SENSORCFG / SET_*`
  - `HISI_DVR_GET_SCHEDULECFG / SET_*`
- Save/apply behavior clues:
  - write path is centered on `HISI_DVR_SetDVRConfig`
  - capture/save control calls: `HISI_DVR_SaveRealData`, `HISI_DVR_StopSaveRealData`, `HISI_DVR_PlayBackSaveData`, `HISI_DVR_StopPlayBackSave`
- Channel/index orchestration clues:
  - channel/stream references in client structs and sample code (`Stream`, play channel usage, find/play APIs)
- Alarm/motion semantics:
  - `HISI_DETECTIONINFO` includes `sens`, `mdalarmduration`, `mdalarm`, `mdbuzzer`, `vlalarmduration`, `vlalarm`, `vlbuzzer`
  - alarm callback and setup: `HISI_DVR_SetupAlarmChan`, `HISI_DVR_CloseAlarmChan`
- Playback/search semantics:
  - `HISI_DVR_FindFile`, `FindNextFile`, `FindClose`, `PlayBackByTime`, `PlayBackByName`, `GetFileByTime`, `GetFileByName`
- Auth/pairing/orchestration:
  - `HISI_DVR_Login`, `Logout`, and ESee resolution via `HISI_DVR_GetConnectInfoByID`

## Promotion into BossCamSuite (this pass)
### Contract promotions
Updated `motion.detection.channel` and `alarm.output.channel` with NVR-derived fields:
- `motionAlarmDuration` (`$.mdalarmduration`)
- `motionAlarm` (`$.mdalarm`)
- `motionBuzzer` (`$.mdbuzzer`)
- `videoLossAlarmDuration` (`$.vlalarmduration`)
- `videoLossAlarm` (`$.vlalarm`)
- `videoLossBuzzer` (`$.vlbuzzer`)
- `alarmDuration` (`$.alarmduration`)
- `alarmEnabled` (`$.alarm`)
- `alarmBuzzer` (`$.buzzer`)

### Operator UI promotions
Added controls in `Motion/Alarm` tab for all fields above with full binding/gating/visibility wiring.

## PTZ handling
NVR SDK contains generic PTZ APIs (`HISI_DVR_PTZControl`, PTZ enums). For 5523-w hardware these remain classified as **NotApplicableToHardware** targets and are not surfaced as required active controls.

## Validation
- `dotnet build BossCamSuite.sln -c Release` passed
- `dotnet test BossCamSuite.sln -c Release --no-build` passed (30/30)
