# 2026-04-21 IPC SDK exhaustive mining + promotion report

## Source
- `C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\IPC_SDK_V_1.4.pdf`

## Exhaustive extraction outputs
Folder:
- `artifacts/sdk-mining/ipc_exhaustive`

Generated files:
- `ipc_file_manifest.csv`
- `ipc_sdk_full_text.txt`
- `ipc_endpoint_catalog.txt`
- `ipc_operation_ids.txt`
- `ipc_enum_candidate_lines.txt`
- `ipc_likely_field_tokens.txt`
- `ipc_semantic_clues.json`
- `ipc_exhaustive_summary.json`
- `current_contract_keys.txt`
- `ipc_field_gap_report.json`

## Extraction summary
From `ipc_exhaustive_summary.json`:
- pages: 41
- text chars: 44,023
- endpoints recovered: 47
- enum-like lines: 108
- likely field tokens: 105
- semantic clue buckets:
  - save/apply: 139
  - channel mapping: 314
  - playback/storage: 63
  - alarm/motion: 73
  - network/wireless: 130

## Promotions applied to BossCamSuite
### New IPC-derived contracts and fields
- `audio.input.channel`
  - `audioEnabled`
  - `audioInputVolume`
- `audio.encode.channel`
  - `audioEnabled`
  - `audioBitRate`
  - `audioSampleRate`
- `video.overlay.channelName`
  - `osdChannelNameEnabled`
  - `osdChannelNameText`
- `video.overlay.datetime`
  - `osdDateTimeEnabled`
- `video.snapshot.channel`
  - `snapShotImageType`
  - `captureWidth`

### Read/probe endpoint expansion
`LanDirectNetSdkRestAdapter` additions:
- Audio:
  - `/NetSDK/Audio/input/channel/1`
  - `/NetSDK/Audio/encode/channel/1`
- Video overlay/snapshot:
  - `/NetSDK/Video/encode/channel/101/channelNameOverlay`
  - `/NetSDK/Video/encode/channel/101/datetimeOverlay`
  - `/NetSDK/Video/encode/channel/101/snapShot`
- Storage:
  - `/NetSDK/SDCard/media/playbackFLV`

### Operator UI expansion
- Stream tab:
  - Audio Enabled
  - Audio Bitrate
  - Audio Sample Rate
- Image tab:
  - Name Overlay toggle
  - Name Text
  - Date/Time Overlay toggle

## Build/test validation
- `dotnet build BossCamSuite.sln -c Release` passed
- `dotnet test BossCamSuite.sln -c Release --no-build` passed (30/30)
