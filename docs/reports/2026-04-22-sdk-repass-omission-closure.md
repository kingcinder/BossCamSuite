# 2026-04-22 SDK Re-pass Omission Closure (NVR SDK + IPC SDK, firmware excluded)

## Scope
- Re-read and re-mined:
  - `C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8`
  - `C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8.zip`
  - `C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\IPC_SDK_V_1.4.pdf`
- Explicitly excluded in this pass (per directive):  
  - `NVR_K8208-3W_FIRMWARE_RELEASE_FWHI102_20211021_W.rom`

## Re-pass findings promoted

### NVR SDK omissions closed
From `HISISDK.h` and CHM topics, the following previously mined but not fully surfaced operations were promoted:
- `HISI_DVR_FindClose`
- `HISI_DVR_PlayBackByName`
- `HISI_DVR_GetFileByName`
- `HISI_DVR_StopGetFile`
- `HISI_DVR_PlayBackSaveData`
- `HISI_DVR_StopPlayBackSave`

Promotion result:
- New backend routes under `/api/devices/{id}/playback/*` for each operation.
- New operator Storage tab controls/inputs:
  - file name
  - save path
  - handle id
  - action buttons for each operation above

### IPC SDK omissions closed
From IPC SDK `VideoDatetimeOverlay` structure semantics:
- `dateFormat`
- `timeFormat`
- `displayWeek`

Promotion result:
- Added typed contract fields:
  - `osdDateFormat`
  - `osdTimeFormat`
  - `osdDisplayWeek`
- Added Image tab controls:
  - Date Format
  - Time Format
  - Display Weekday

## PTZ classification note
- Mechanical PTZ calls remain present in NVR SDK by inheritance.
- For 5523-w they remain **NotApplicableToHardware** and are not promoted as active operator controls.

## Verification target for this pass
- Build and tests must remain green after these promotions.
