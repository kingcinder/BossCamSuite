# 2026-07-23 Multi-brand + high-res + durable settings + GUI pass

## Brands on Aegon LAN

| IP | Brand | Model | Control | High-res stream |
|----|-------|-------|---------|-----------------|
| 10.0.0.30 | Juan / GUANGZHOU | 5523-W | NetSDK :80 + ONVIF :8888 | `rtsp://…/ch0_0.264` HEVC **2560×1920** |
| 10.0.0.170 | Juan / GUANGZHOU | 5523-W | same | same |
| 10.0.0.228 | Juan / GUANGZHOU | 5523-W | same | same |
| 10.0.0.129 | WVC | W5C (ONVIF Model 631GA) | ONVIF :8899 | ONVIF/media (auth required for full media) |
| 10.0.0.128 | Lorex (FLIR/Dahua) | Lorex web shell | Digest CGI | `…/cam/realmonitor?channel=1&subtype=0` |

## High-res recording (proven)

- Source selected: `rtsp://admin:@10.0.0.30:554/ch0_0.264` (not sub `ch0_1` / 704×480)
- Output: MPEG-TS, **codec=hevc width=2560 height=1920**, ~1.3 MB / ~11 s live smoke
- Sub-stream paths are ranked ≥50 and filtered out of default record selection

## Settings durability (main path)

- Image controls write `/NetSDK/Video/input/channel/1` (shared capture for main encode) — brightness PUT readback OK
- Stream encode controls target **channel 101** (2560×1920 H.265), not 102 sub
- NetSDK PUT statusCode 0; values survive process restart (device NVRAM). GUI “Verify Persistence” remains available for reboot checks

## Multi-brand wiring

- `MultiBrandHighResTransportAdapter` — main-first RTSP + ONVIF stream discovery
- `DahuaLorexControlAdapter` — Lorex/Dahua CGI
- `OnvifImagingControlAdapter` — WVC/generic ONVIF device info
- `POST /api/devices/register-aegon-lan` — enrolls all five endpoints
- SQLite device upsert now keeps `id` and JSON payload aligned (fixed empty sources / “Device not found”)

## GUI layout

- Window: Maximizd start, Min 1280×720, resizable
- All operator tabs wrapped in ScrollViewer; storage paths use star columns (no 520/654 px clip)
- Decorative image no longer uses negative margin overflow
- Recording defaults to 30 s segments, high-res main stream

## Credentials note

- Juan 5523-W: `admin` / empty password works
- Lorex (.128): Digest required — password not in common defaults; register with real password:
  `{"ipAddress":"10.0.0.128","loginName":"admin","password":"<your-lorex-password>","hardwareModel":"Lorex"}`
- WVC (.129): full media/settings need authorized ONVIF credentials similarly

## Tests

- Unit tests: 56/56 after segment index fixture fix
