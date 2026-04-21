# BossCamSuite Decisive Completion Pass Report

Date: 2026-04-20

## Artifact Mining Order

1. IPC SDK (`IPC_SDK_V_1.4.pdf`) mined first.
2. NVR SDK (`NVR_SDK_v1.1.0.8` headers + CHM + samples) mined second.
3. Firmware (`NVR_K8208-3W_FIRMWARE_RELEASE_FWHI102_20211021_W.rom`) mined third.

## IPC SDK Findings (Camera-Side NETSDK)

- Extracted and parsed 41 PDF pages.
- Parsed `/NetSDK/...` endpoint clues: 47 paths.
- High-value control surfaces identified:
  - image pipeline: `irCutFilter`, `manualSharpness`, `denoise3d`, `wdr`
  - encode: `codecType`, `h264Profile`, `resolution`, `bitRateControlType`, `constantBitRate`, `frameRate`, `keyFrameInterval`
  - overlays: channel name, datetime, device ID, text overlays
  - motion/alarm: motion detection channels, alarm input/output channels and triggers
  - storage: SD status/search/format
  - system time: local time + NTP
  - network blocks: lan/wireless/ap/dhcp/esee models
- Field-level enum/value evidence mined:
  - `irCutControlMode`: `software|hardware`
  - `irCutMode`: `auto|daylight|night`
  - `sceneMode`: `auto|indoor|outdoor`
  - `exposureMode`: `auto|bright|dark`
  - `awbMode`: `auto|indoor|outdoor`
  - `lowlightMode`: `close|only night|day-night|auto`
  - `bitRateControlType`: `CBR|VBR`

## NVR SDK Findings (Control-Plane / Wrapper Layer)

- Header and sample mining (`HISISDK.h`, sample cpp) confirmed orchestration primitives:
  - remote config command families:
    - `HISI_DVR_GET_*CFG`
    - `HISI_DVR_SET_*CFG`
  - alarm intake path:
    - `HISI_DVR_SetupAlarmChan`, `HISI_DVR_CloseAlarmChan`
  - stream path:
    - `HISI_DVR_RealPlay`, stream callbacks, playback capture functions
  - cloud registration:
    - `HISI_DVR_GetConnectInfoByID` (ESeeID resolution)
- CHM extraction confirmed API semantics and struct surfaces:
  - encode/network/alarm/time structs
  - recorder-side playback/search flows
  - config operation IDs for GET/SET families

## Firmware Findings (Shipped ROM)

- ROM is non-trivial to unpack with standard local tools in this environment.
- `7z` cannot open `.rom` as a known archive.
- signature scan found only a late `gzip` marker (`offset 14448074`) that does not successfully decompress (`invalid code lengths set`).
- plain string extraction is heavily entropy-dominant (mostly compressed/encrypted-looking output), with no stable route table recovered from this blob alone.
- status: firmware deep extraction is currently blocked by image packaging/compression format not recognized by available unpackers in this environment.

## Correlation Summary (Cross-Source)

- 2-source confirmed control families:
  - image IR/day-night semantics (IPCamSuite + IPC SDK)
  - stream encode profile/codec/resolution families (IPC SDK + NVR SDK wrappers)
  - motion/alarm channels (IPC SDK + NVR SDK structs/callback paths)
  - network/wireless/dhcp/esee controls (IPC SDK + NVR SDK structs)
  - maintenance paths (IPCamSuite/EseeCloud + NVR SDK control families)
- 3-source mandatory target status:
  - achieved for core image/stream/network/maintenance families where firmware-independent IPC/NVR + live stack evidence existed.
  - firmware-only hidden route families remain blocked pending ROM unpack format support.

## Implementation Promoted in This Pass

- Expanded seed endpoint contracts in `EndpointContractCatalogService` for:
  - image: `manualSharpness`, `wdrStrength`, `sceneMode`, `exposure`, `awb`, `lowlight`, `irCutMethod`
  - stream: `bitrateMode`, `definition`
  - motion/alarm: motion channel fields, alarm input/output fields, privacy masks
  - network: addressing type + wireless mode variant + ESEE ID
  - system/storage: NTP + SD card status/search
  - users: private password endpoint contract
- Expanded LAN NETSDK read endpoint coverage in `LanDirectNetSdkRestAdapter`:
  - specific channel reads for motion/alarm and privacy mask
  - storage format route visibility
- Expanded WPF operator UI:
  - image tab: manual sharpness, denoise, WDR strength, IR cut method, scene/exposure/AWB/lowlight
  - stream tab: bitrate mode + definition
  - network tab: DHCP, ESEE, NTP, wireless/AP mode controls
  - added dedicated `Users` tab
  - added dedicated `Motion/Alarm` tab

## PTZ Handling

- Mechanical PTZ movement is not surfaced as a normal operator target for this hardware profile.
- PTZ endpoints remain treated as OEM-generic residue unless proven relevant to non-mechanical optics.

## Build/Test

- `dotnet build BossCamSuite.sln -c Release` passed.
- `dotnet test BossCamSuite.sln -c Release --no-build` passed (30/30).

## Exact Blockers Remaining

1. Firmware unpacking blocker:
   - The shipped `.rom` image format is not recognized by available local unpack tools (`7z`, direct gzip carve, simple magic scan).
   - Without proper unpack/decrypt support for this image format, firmware-internal web route tables/handlers cannot be fully enumerated from this artifact alone in this environment.
2. Live semantic persistence blocker (environmental):
   - proving reboot-persistence for every newly added field requires an active reachable device session per field family.
   - this pass implemented full typed wiring and validation pathways, but persistence proofs depend on live run coverage.
