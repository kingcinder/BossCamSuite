# IPCamSuite Oracle Mining Report (5523-w)

Date: 2026-04-20

## Scope

- Static reverse-mining targets:
  - `C:\Program Files\IPCamSuite`
  - `C:\Program Files (x86)\EseeCloud`
  - `C:\Users\ceide\AppData\Local\EseeCloud`
- Goal: promote IPCamSuite/EseeCloud semantics into BossCamSuite endpoint contracts and protocol manifests.

## Mined Endpoint Clues

- `/NetSDK/Factory?cmd=InfraRedCtrl`
- `/NetSDK/Factory?cmd=WhiteLightCtrl`
- `/NetSDK/Image/irCutfilter`
- `/NetSDK/System/operation/default`
- `/NetSDK/System/operation/reboot`
- `/NetSDK/Network/Esee`
- `/NetSDK/Network/Interface/1`
- `/NetSDK/System/deviceInfo`
- `/netsdk/video/encode/channel/ID/requestKeyFrame`
- `/netsdk/Reboot`
- `/netsdk/GetUpgradeRate`
- `/onlineupgrade`
- `/cgi-bin/upload.cgi`
- `/cgi-bin/upgrade_rate.cgi?cmd=upgrade_rate`
- `/cgi-bin/gw2.cgi`
- `/cgi-bin/hi3510/getnonce.cgi`
- `/cgi-bin/hi3510/ptzctrl.cgi`
- `/cgi-bin/hi3510/preset.cgi`

## Mined Enum / Value Clues

- IR cut control method: `software`, `hardware`
- IR/day-night mode family A: `auto`, `daylight`, `night`
- IR/day-night mode family B: `ir`, `light`, `smart`
- Stream definition modes: `auto`, `fluency`, `HD`, `BD`
- Codec/profile labels: `codecType`, `h264Profile`, `resolution`, `bitrate`, `framerate`
- Stream labels: `mainstream`, `substream1`, `substream2`

## Save / Apply / Reboot Behavior Clues

- EseeCloud remote settings use command envelope with `Method: "set"` and capability payload writes.
- `saveSetting` label and OEM save-confirm labels indicate explicit save/apply UX step in OEM clients.
- Reboot/factory operations are explicit command families (`System/operation/reboot`, `System/operation/default`, and EseeCloud reboot/restore command paths).
- Firmware update behavior split into upload + progress polling + reboot endpoint families.
- Channel-indexed write path behavior is explicitly indicated for video encode routes (`101`, `102` style IDs and request-keyframe trigger).

## Likely OEM Command Families

- NetSDK REST: `/NetSDK/...`
- Lowercase NetSDK variant: `/netsdk/...`
- OEM factory commands: `/NetSDK/Factory?cmd=...`
- Legacy CGI families: `/cgi-bin/hi3510/*`, `/cgi-bin/gw2.cgi`, `/cgi-bin/upload.cgi`
- EseeCloud remote JSON envelope (`remote:IPCam` / `remote:XVR` command style)

## BossCamSuite Promotion Completed

- Updated seed contracts in `EndpointContractCatalogService` with:
  - IR/day-night expanded enum families (`auto/daylight/night` plus `ir/light/smart`)
  - explicit `irCutMethod` (`software/hardware`)
  - stream `definition` enum
  - channel-indexed keyframe trigger endpoint contract
  - ESEE network endpoint contract
  - alternate reboot and firmware-upgrade endpoint candidates
  - private white/IR OEM type-index hints mapped from `MAINSET.INI`
- Updated `assets/protocols/ipcamsuite_private.manifest.json` with EseeCloud-derived maintenance and stream trigger endpoints plus method expansions.

## BossCamSuite Gaps Closed by IPCamSuite Evidence

- Clarified IR semantics split (mode vs control method).
- Added concrete stream definition enum values used by OEM client.
- Added upgrade/reboot command family alternatives used in EseeCloud flows.
- Added channel-indexed keyframe trigger path missing from previous seed contracts.
- Added explicit ESEE network config endpoint as first-class candidate.
