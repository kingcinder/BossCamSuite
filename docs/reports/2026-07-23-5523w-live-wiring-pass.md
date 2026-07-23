# 2026-07-23 5523-W live wiring pass

## Live targets (this session)

| IP | Model | Serial | ESEE ID | Firmware | Role |
|----|-------|--------|---------|----------|------|
| 10.0.0.30 | 5523-W | Z7C34780038910 | 4780038910 | 3.6.103.5721106 | channel name "3" |
| 10.0.0.170 | 5523-W | Z7C34781620744 | 4781620744 | 3.6.103.5721106 | channel name "Driveway" |

Auth for NetSDK HTTP: **Basic `admin:` (empty password)**.

## Reverse-engineering sources used

- `/home/cody/Downloads/nSDK_v1.1.0.8` (HISISDK.h + avlib.dll + sample clients)
- `/home/cody/Downloads/NETSDK V1.4 接口说明.pdf` + platinum markdown
- Vendor installers under Ark Security Camera Programs (CMS / EseeCloud / IPCamSuite)
- Prior BossCamSuite evidence reports + live HTTP probing

## Proven control surface (settings)

| Surface | Path | Methods | Notes |
|---------|------|---------|-------|
| Device info | `/NetSDK/System/deviceInfo` | GET | model, serial, eseeID, firmware, MAC |
| Image + flip/mirror | `/NetSDK/Video/input/channel/1` | GET/PUT | brightness/contrast/sat/sharp/hue/flip/mirror/privacy |
| Image advanced | `/NetSDK/Image`, `/wdr`, `/denoise3d`, `/irCutFilter`, `/manualSharpness` | GET | full image struct |
| Encode main | `/NetSDK/Video/encode/channel/101` (+ `/properties`) | GET/PUT | H.265/H.264, res, bitrate, fps |
| Encode sub | `/NetSDK/Video/encode/channel/102` (+ `/properties`) | GET/PUT | substream |
| Network eth0 | `/NetSDK/Network/interface/1` | GET/PUT | **singular** `interface` |
| Network wlan0 | `/NetSDK/Network/interface/4` | GET/PUT | wireless station/AP/DHCP |
| Network list | `/NetSDK/Network/interface` | GET | array of interfaces |
| DNS | `/NetSDK/Network/Dns` | GET | preferred + alternate |
| ESEE | `/NetSDK/Network/Esee` | GET/PUT | `{ "enabled": bool }` |
| Motion | `/NetSDK/Video/motionDetection/channel/1` | GET/PUT | grid detection |
| SD | `/NetSDK/SDCard/status` | GET | `"ejected"` on tested units |
| Time | `/NetSDK/System/time/localTime`, `/ntp` | GET/PUT | |
| Users | `/user/user_list.xml?username=admin&password=` | GET | XML |
| Reboot | `/NetSDK/System/operation/reboot` | **PUT** | GET → 405 Bad Method |
| Snapshot | `/NetSDK/Video/encode/channel/101/snapShot` | GET | `image/jpg` |

**Broken paths previously in adapters (fixed):**

- `/NetSDK/Network/interfaces` → 404 (must be `/interface`)
- `/NetSDK/Stream/*` → 404 on this firmware
- `/NetSDK/Image/gamma` → 404

## Stream / record surface

| Mode | URL | Result |
|------|-----|--------|
| RTSP main | `rtsp://admin:@ip:554/11` | DESCRIBE OK (Digest), SETUP/PLAY OK, **RTP media silent** on this LAN |
| RTSP sub | `rtsp://admin:@ip:554/12` | same as main |
| Bubble | `http://ip/bubble/live?ch=1&stream=0` | 200 `video/bubble` proprietary (needs vendor decoder) |
| JPEG snapshot | `/NetSDK/Video/encode/channel/101/snapShot` | **Works** — used for preview tiles + default recording pipeline |

RTSP server banner: `happytime rtsp server 2.2`. SDP advertises H264 + PCMA. Cam 170 main encode reports H.265+.

Recording default is now a **snapshot pipeline** (curl JPEG loop → ffmpeg libx264 segments) because RTSP media bytes were zero after PLAY on live units. RTSP URLs remain first-class for clients that can open them (OEM Windows stack / future media fix).

## Code changes in this pass

1. `HttpControlAdapters` — correct network/image/video/maintenance endpoints
2. `VideoTransportAdapters` — authenticated `/11` `/12` RTSP + snapshot + bubble sources
3. `GroupedConfigService` / contract catalog — singular `/Network/interface/*`
4. `RecordingService` — Linux `ffmpeg` path, video-only RTSP args, **snapshot record pipeline**, start-all/stop-all
5. `HighlightBoardService` — multi-cam board with select / next / prev / stream mode / record-selected
6. `DeviceRegistrationService` — register by IP + enrich from deviceInfo
7. Service APIs for register, highlights, snapshot proxy, start-all
8. Linux-friendly `appsettings.json` + `scripts/start-bosscam-linux.sh`

## Operator quick start (Linux)

```bash
chmod +x scripts/start-bosscam-linux.sh
BOSSCAM_CAMERA_IPS=10.0.0.30,10.0.0.170 ./scripts/start-bosscam-linux.sh
```

```bash
# flip highlight
curl -X POST http://127.0.0.1:5317/api/highlights/next
# record all cameras
curl -X POST http://127.0.0.1:5317/api/recordings/start-all
# snapshot of selected device
curl -o snap.jpg http://127.0.0.1:5317/api/devices/<id>/snapshot
```

## Live smoke results (this pass)

- Service health OK on `http://127.0.0.1:5317`
- Registered Cam-30 + Driveway from deviceInfo (firmware `3.6.103.5721106`)
- Highlight board: next/prev cycles IPC tiles only
- Snapshot proxy returns JPEG
- Settings adapter `LanDirectNetSdkRestAdapter` reads Device/Network/Video/Image groups
- **Recording:** `POST /api/recordings/start-all` → snapshot JPEG loop → ffmpeg MPEG-TS
  - `10.0.0.30` ~239 KB playable H.264 704×480 ~7.5s
  - `10.0.0.170` ~219 KB playable H.264 704×480 ~8.0s
- Unit tests: **56/56 passed**

## Remaining gaps

- RTSP RTP media path still not delivering frames after PLAY (investigate vendor DLL / concurrent stream limits / firmware RTSP enable)
- Bubble FLV proprietary decoder not open-sourced in nSDK alone
- WPF desktop is Windows-only; service API is cross-platform
- Wireless write matrix (AP mode switch) still needs guarded live validation
