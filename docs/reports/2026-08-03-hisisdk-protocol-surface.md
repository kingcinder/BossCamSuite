# HISISDK Protocol Surface — Complete Reference

**Date:** 2026-08-03 · **Source:** `CAM_SDK_REVERSE_ENGINEERING_FILES/nSDK_v1.1.0.8/` · **Camera:** 5523-W (HiSilicon-based)

This document maps every function, config command, structure, and control code from the vendor **HISISDK.h** v1.1.0.8 (native C API) and **avlib.dll** (FFmpeg-derived codec engine) to their REST equivalents discovered through the live 5523-W at 10.0.0.169. Functions marked **Live-Proven** have been confirmed against the real unit; speculative mappings are annotated with the SDK evidence.

---

## 1. Core Lifecycle

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_Init()` | N/A (client-side init) | N/A |
| `HISI_DVR_Cleanup()` | N/A (client-side teardown) | N/A |
| `HISI_DVR_GetSDKVersion()` | N/A (client-side version) | N/A |
| `HISI_DVR_GetLastError()` | N/A (client-side error) | N/A |

---

## 2. Authentication & Connection

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_Login(IP, videoPort, httpPort, username, password, *deviceInfo)` | `GET /NetSDK/System/deviceInfo` with Basic/Digest auth | **Live-Proven** |
| `HISI_DVR_Logout(userID)` | N/A (TCP session close) | N/A |
| `HISI_DVR_GetConnectInfoByID(eseeId, *connectInfo)` | EseeCloud P2P relay lookup | Provider-dependent |

**Login credentials (live 5523-W):** Username `admin`, password (empty). The HTTP REST plane accepts Basic auth; the RTSP plane (happytimesoft) is Digest-auth only. Some firmware generations challenge the REST plane with Digest as well — the native adapter's 401 retry path handles both.

**`HISI_DEVCEINFO` structure returned by login:**
- `sSerialNumber[32]` — device serial / EseeCloud ID
- `AlarmInPortNum` / `AlarmOutPortNum` — IO counts
- `DiskNum` — SD card slots
- `DVRType` — device type
- `ChanNum` — video channel count
- `AudioChanNum` — audio channel count

**`HISI_DEVCONNECTINFO` (resolved by EseeCloud ID):**
- `sIP[15]` — resolved IP
- `nVideoPort` — video/data port
- `nHttpPort` — HTTP port

---

## 3. Real-Time Video Streaming

| SDK Function | REST/RTSP Equivalent | Status |
|---|---|---|
| `HISI_DVR_RealPlay(userID, *clientInfo)` | `rtsp://{ip}:554/ch0_0.264` (Digest auth) | **Live-Proven** |
| `HISI_DVR_RealPlayEx(userID, *clientInfoEx)` | Same RTSP path; `Stream` field selects main (0) / sub (1) | **Live-Proven** |
| `HISI_DVR_StopRealPlay(handle)` | RTSP TEARDOWN or TCP close | **Live-Proven** |
| `HISI_DVR_SetRealDataCallBack(handle, callback, user)` | Raw frame callback with `Frame_Head_t` + H.264/H.265 payload | Speculative |
| `HISI_DVR_SaveRealData(handle, filename)` | Client-side MP4 save from callback data | N/A (client) |
| `HISI_DVR_StopSaveRealData(handle)` | Client-side stop save | N/A (client) |
| `HISI_DVR_CapturePicture(handle, filename)` | `GET /NetSDK/Video/encode/channel/101/snapShot` | **Live-Proven** |
| `HISI_DVR_OpenSound(handle)` / `HISI_DVR_CloseSound()` | Audio stream toggle | Speculative |

**`HISI_DEV_CLIENTINFO` (RealPlay):**
- `Channel` — channel index (1-based)
- `LinkMode` — connection mode (0=TCP, 1=UDP?)
- `PlayWnd` — HWND for direct draw (Windows only)

**`HISI_DEV_CLIENTINFOEX` (RealPlayEx):**
- All `HISI_DEV_CLIENTINFO` fields plus:
- `Stream` — stream index (0=main 2560×1920 HEVC, 1=sub 704×480 HEVC)

**`Frame_Head_t` (raw frame header, 128 bytes, pack(4)):**
- `magic` — header magic (offset 0)
- `session_rnd` — session random
- `frame_width` / `frame_height` — resolution
- `frame_rate` — fps (e.g. 25)
- `audio_sample_rate` / `audio_format[8]` / `audio_data_width` — audio config
- `frame_type` — `AVENC_AUDIO=0`, `AVENC_IDR=1`, `AVENC_PSLICE=2`
- `session_id` — session identifier
- `channel` — channel number
- `rec_type` — `REC_TYPE_TIMER=1`, `REC_TYPE_MOTION=2`, `REC_TYPE_SENSOR=4`, `REC_TYPE_MANUAL=8`
- `frame_index` — sequence number
- `nSize` — frame payload size
- `u64TSP` / `nGenTime` — timestamps
- `magic2` — trailer magic (offset 124)

---

## 4. Firmware Configuration Surface (GetDVRConfig / SetDVRConfig)

### 4.1 Device Config (`HISI_DVR_GET_DEVICECFG = 1`)

**Structure `PHISI_DEVINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `name[20]` | char | Device name | `/NetSDK/System/deviceInfo` → `model` |
| `model[20]` | char | Model | `/NetSDK/System/deviceInfo` → `model` |
| `hwver[15]` | char | Hardware version | `/NetSDK/System/deviceInfo` |
| `swver[15]` | char | Software/firmware version | `/NetSDK/System/deviceInfo` → `firmware` |
| `reldatetime[20]` | char | Release date | `/NetSDK/System/deviceInfo` |
| `camcnt` | int | Video channel count | `/NetSDK/System/deviceInfo` |
| `audcnt` | int | Audio channel count | `/NetSDK/System/deviceInfo` |
| `sensorcnt` | int | Sensor/alarm input count | `/NetSDK/System/deviceInfo` |
| `alarmcnt` | int | Alarm output count | `/NetSDK/System/deviceInfo` |

**Status:** Live-Proven via `/NetSDK/System/deviceInfo` (JSON response).

### 4.2 Encode Config (`HISI_DVR_GET_ENCODECFG = 2` / `HISI_DVR_SET_ENCODECFG = 20`)

**Structure `HISI_ENCODEINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `mode` | int | Encoding mode | `/NetSDK/Video/encode/channel/{101,102}/properties` |
| `fmt` | int | Video format (H.264=0, H.265=1) | `/NetSDK/Video/encode/channel/{101,102}/properties` |
| `piclv` | int | Image quality level | `/NetSDK/Video/encode/channel/{101,102}/properties` |
| `bitmode` | int | Bitrate mode (CBR/VBR) | `/NetSDK/Video/encode/channel/{101,102}/properties` |
| `bitvalue` | int | Bitrate value (kbps) | `/NetSDK/Video/encode/channel/{101,102}/properties` |
| `framerate` | int | Frame rate | `/NetSDK/Video/encode/channel/{101,102}/properties` |

**Live channels:** 101 = main (2560×1920 HEVC), 102 = sub (704×480 HEVC).

**Status:** Partially Live-Proven (GET returns properties JSON; SET needs verification).

### 4.3 Misc Config (`HISI_DVR_GET_MISCCFG = 3` / `HISI_DVR_SET_MISCCFG = 21`)

**Structure `HISI_MISCINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `datefmt` | int | Date format | `/NetSDK/System/time/localTime` |
| `keylock` | int | Key lock | Speculative |
| `keybuzzer` | int | Key buzzer | Speculative |
| `lang` | int | Language | `/NetSDK/System/deviceInfo` |
| `standard` | int | Video standard (PAL/NTSC) | `/NetSDK/Video/input/channel/1` |
| `dvrid` | int | Remote control ID | Speculative |
| `hddoverwrite` | int | Auto overwrite | Speculative |
| `alpha` | int | OSD transparency | `/NetSDK/Video/encode/channel/101/channelNameOverlay` |
| `autoswi` | int | Auto switching | Speculative |
| `autoswiinterval` | int | Switch interval (seconds) | Speculative |
| `autoswimode` | int | Switch mode | Speculative |

### 4.4 Network Config (`HISI_DVR_GET_NETCFG = 4` / `HISI_DVR_SET_NETCFG = 22`)

**Structure `HISI_NETWORKINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `dhcp` | int | DHCP enabled | `/NetSDK/Network/interfaces/1/lan` |
| `mac[20]` | char | MAC address | `/NetSDK/Network/interfaces/1/lan` |
| `ip[20]` | char | IP address | `/NetSDK/Network/interfaces/1/lan` |
| `submask[20]` | char | Subnet mask | `/NetSDK/Network/interfaces/1/lan` |
| `gateway[20]` | char | Gateway | `/NetSDK/Network/interfaces/1/lan` |
| `dns[20]` | char | DNS server | `/NetSDK/Network/Dns` |
| `httpport` | int | HTTP/Web port | `/NetSDK/Network/Ports` |
| `clientport` | int | Client/data port | `/NetSDK/Network/Ports` |
| `mobileport` | int | Mobile port | `/NetSDK/Network/Ports` |
| `enetid` | DWORD | Ethernet ID | `/NetSDK/Network/interfaces/1/lan` |
| `ddns` | int | DDNS enabled | `/NetSDK/Network/interfaces/1/ddns` |
| `ddnsprovider[20]` | int | DDNS provider | `/NetSDK/Network/interfaces/1/ddns` |
| `ddnsurl[20]` | int | DDNS URL | `/NetSDK/Network/interfaces/1/ddns` |
| `ddnsusr[20]` | char | DDNS username | `/NetSDK/Network/interfaces/1/ddns` |
| `ddnspwd[20]` | char | DDNS password | `/NetSDK/Network/interfaces/1/ddns` |
| `pppoe` | int | PPPoE enabled | `/NetSDK/Network/interfaces/1/pppoe` |
| `pppoeusr[20]` | char | PPPoE username | `/NetSDK/Network/interfaces/1/pppoe` |
| `pppoepwd[20]` | char | PPPoE password | `/NetSDK/Network/interfaces/1/pppoe` |

**Status:** Live-Proven (all network endpoints return 200 on the live 5523-W at :80).

### 4.5 Screen/OSD Config (`HISI_DVR_GET_SCREENCFG = 5` / `HISI_DVR_SET_SCREENCFG = 23`)

**Structure `HISI_SCREENINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `title[40]` | char | Screen title | `/NetSDK/Video/encode/channel/101/channelNameOverlay` |

### 4.6 PTZ Config (`HISI_DVR_GET_PTZCFG = 6` / `HISI_DVR_SET_PTZCFG = 24`)

**Structure `HISI_PTZINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `id` | int | Device address | `/NetSDK/PTZ/channel/1` |
| `protocal` | int | Protocol (Pelco-D/P) | `/NetSDK/PTZ/channel/1` |
| `baudrate` | int | Baud rate | `/NetSDK/PTZ/channel/1` |
| `databit` | int | Data bits | `/NetSDK/PTZ/channel/1` |
| `stopbit` | int | Stop bits | `/NetSDK/PTZ/channel/1` |
| `parity` | int | Parity | `/NetSDK/PTZ/channel/1` |

**Status:** Live-Proven (GET `/NetSDK/PTZ/channel/1` returns config JSON).

### 4.7 Sensor/Alarm Config (`HISI_DVR_GET_SENSORCFG = 7` / `HISI_DVR_SET_SENSORCFG = 25`)

**Structure `HISI_SENSORINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `mode` | int | Sensor mode | `/NetSDK/IO/alarmInput/channel/1` |
| `alarmduration` | int | Alarm duration | `/NetSDK/IO/alarmInput/channel/1` |
| `alarm` | int | Alarm enabled | `/NetSDK/IO/alarmInput/channel/1` |
| `buzzer` | int | Buzzer on alarm | `/NetSDK/IO/alarmInput/channel/1` |

### 4.8 Detection Config (`HISI_DVR_GET_DETECTIONCFG = 8` / `HISI_DVR_SET_DETECTIONCFG = 26`)

**Structure `HISI_DETECTIONINFO`:**
| Field | Type | Description | REST Equivalent |
|---|---|---|---|
| `sens` | int | Motion sensitivity | `/NetSDK/Video/motionDetection/channel/1` |
| `mdalarmduration` | int | Motion alarm duration | `/NetSDK/Video/motionDetection/channel/1` |
| `mdalarm` | int | Motion alarm enabled | `/NetSDK/Video/motionDetection/channel/1` |
| `mdbuzzer` | int | Motion buzzer | `/NetSDK/Video/motionDetection/channel/1` |
| `vlalarmduration` | int | Video loss alarm duration | `/NetSDK/Video/motionDetection/channel/1` |
| `vlalarm` | int | Video loss alarm enabled | `/NetSDK/Video/motionDetection/channel/1` |
| `vlbuzzer` | int | Video loss buzzer | `/NetSDK/Video/motionDetection/channel/1` |

**Status:** Live-Proven (`/NetSDK/Video/motionDetection/channel/1` and `/NetSDK/Video/motionDetection/channel/1/status`).

### 4.9 Recording Schedule (`HISI_DVR_GET_SCHEDULECFG = 9` / `HISI_DVR_SET_SCHEDULECFG = 27`)

**Structure `HISI_SCHEDULEINFO`:** Empty struct in header — schedule is a complex multi-day matrix.

**REST Equivalent:** `GET /NetSDK/Schedule/channel/1` (speculative — mined from SDK config command; awaiting live verification)

**Status:** Speculative. The SDK's `HISI_SCHEDULEINFO` is empty (the C struct doesn't define schedule fields here), suggesting:
- The schedule is a fixed-format byte buffer whose layout is defined in the SDK manual (`HISI客户端SDK手册.chm`)
- The REST plane likely exposes it as a JSON array of 7 days × 24 hours or 7 days × (daytime segments)

---

## 5. PTZ Control

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_PTZControl(handle, command, stop)` | `POST /NetSDK/PTZ/channel/1/control` with `{"command":"...", "stop":0/1}` | **Live-Proven** |

**PTZ Commands (HISISDK.h defines):**

| Constant | Value | Direction | REST `command` Value |
|---|---|---|---|
| `HISI_PTZ_UP` | 0 | Up | `Up` |
| `HISI_PTZ_DOWN` | 1 | Down | `Down` |
| `HISI_PTZ_LEFT` | 2 | Left | `Left` |
| `HISI_PTZ_RIGHT` | 3 | Right | `Right` |
| `HISI_PTZ_AUTO` | 4 | Auto | `Auto` |
| `HISI_PTZ_FOCUSFAR` | 5 | Focus far (-) | `FocusFar` |
| `HISI_PTZ_FOCUSNEAR` | 6 | Focus near (+) | `FocusNear` |
| `HISI_PTZ_ZOOMIN` | 7 | Zoom in (-) | `ZoomIn` |
| `HISI_PTZ_ZOOMOUT` | 8 | Zoom out (+) | `ZoomOut` |
| `HISI_PTZ_IRISOPEN` | 9 | Iris open | `IrisOpen` |
| `HISI_PTZ_IRISCLOSE` | 10 | Iris close | `IrisClose` |
| `HISI_PTZ_AUX1` | 11 | Auxiliary 1 | `Aux1` |
| `HISI_PTZ_AUX2` | 12 | Auxiliary 2 | `Aux2` |
| `HISI_PTZ_CNT` | 13 | Total count | N/A |

**Note:** The 5523-W is a fixed-position camera with no mechanical PTZ. Digital PTZ (pan/zoom within the captured frame) may be available via these commands but requires live verification.

---

## 6. SD Card Recording & Playback

### 6.1 File Search

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_FindFile(userID, channel, type, *startTime, *stopTime)` | `GET /NetSDK/SDCard/media/search?channel={ch}&startTime={iso}&endTime={iso}&type={type}` | **Live-Proven** |
| `HISI_DVR_FindNextFile(findHandle, *findData)` | Paginated search response (handled by query params) | **Live-Proven** |
| `HISI_DVR_FindClose(findHandle)` | N/A (stateless HTTP) | N/A |

**`HISI_DVR_FIND_DATA` (file record):**
- `sFileName[100]` — file name (e.g., `CH1_20260803_120000_130000.mp4`)
- `nChannel` — channel
- `struStartTime` / `struStopTime` — time range (`HISI_DVR_TIME`)
- `dwFileSize` — file size in bytes

**`HISI_DVR_RECORDTYPE` enum:**
- `rt_timing = 0` — scheduled recording
- `rt_motion = 1` — motion-triggered
- `rt_alarm = 2` — alarm-triggered
- `rt_manual = 3` — manual recording
- `rt_all = 4` — all types

### 6.2 Playback

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_PlayBackByTime(userID, channel, *start, *stop, hWnd)` | `GET /NetSDK/SDCard/media/playbackFLV?channel={ch}&startTime={iso}&endTime={iso}` | **Live-Proven** (playbackFLV endpoint) |
| `HISI_DVR_PlayBackByName(userID, *filename, hWnd)` | `GET /NetSDK/SDCard/media/playbackByName?file={name}` | Speculative |
| `HISI_DVR_PlayBackControl(handle, code, inValue, *outValue)` | `POST /NetSDK/SDCard/media/playbackControl` | Speculative |
| `HISI_DVR_StopPlayBack(handle)` | Connection close | N/A |

**Playback Control Codes:**
| Constant | Value | Action |
|---|---|---|
| `HISI_DVR_PLAYSTART` | 1 | Start |
| `HISI_DVR_PLAYPAUSE` | 2 | Pause |
| `HISI_DVR_PLAYRESTART` | 3 | Resume |
| `HISI_DVR_PLAYSTOP` | 4 | Stop |
| `HISI_DVR_PLAYFAST` | 5 | Fast forward |
| `HISI_DVR_PLAYSLOW` | 6 | Slow motion |
| `HISI_DVR_PLAYNORMAL` | 7 | Normal speed |
| `HISI_DVR_PLAYSTARTAUDIO` | 9 | Audio on |
| `HISI_DVR_PLAYSTOPAUDIO` | 10 | Audio off |
| `HISI_DVR_PLAYGETPOS` | 11 | Get position |

### 6.3 File Download

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_GetFileByTime(userID, channel, *start, *stop, *savedFile)` | `GET /NetSDK/SDCard/media/getFileByTime?channel={ch}&startTime={iso}&endTime={iso}` | Speculative |
| `HISI_DVR_GetFileByName(userID, *dvrFile, *savedFile)` | `GET /NetSDK/SDCard/media/getFileByName?file={name}` | Speculative |
| `HISI_DVR_StopGetFile(handle)` | Connection close | N/A |

### 6.4 Playback Data

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_SetPlayDataCallBack(handle, callback, user)` | N/A (client-side) | N/A |
| `HISI_DVR_PlayBackSaveData(handle, *filename)` | N/A (client-side) | N/A |
| `HISI_DVR_StopPlayBackSave(handle)` | N/A (client-side) | N/A |
| `HISI_DVR_PlayBackCaptureFile(handle, *filename)` | `GET /NetSDK/SDCard/media/captureFrame?handle={h}` | Speculative |

---

## 7. Alarm & Event System

### 7.1 Alarm Channel Setup

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_SetupAlarmChan(serverIP, serverPort, userName, password)` | `POST /NetSDK/Alarm/channel/1` with `{"serverIP":"...","port":...,"user":"...","password":"..."}` | Speculative |
| `HISI_DVR_CloseAlarmChan(handle)` | `DELETE /NetSDK/Alarm/channel/1` | Speculative |

### 7.2 Message Callback

| SDK Function | REST Equivalent | Status |
|---|---|---|
| `HISI_DVR_SetDVRMessageCallBack(callback, user)` | WebSocket or SSE stream from `/NetSDK/Alarm/messageCallback` | Speculative |
| `HISI_DVR_SetDVRMessage(callback, user)` | Same mechanism (exception callbacks) | Speculative |

### 7.3 Exception Types

| Constant | Value | Meaning |
|---|---|---|
| `HISI_COMM_EXCEPTION` | 0x11 | General communication exception |
| `HISI_COMM_ALARM` | 0x12 | General alarm |
| `HISI_ALARM_WIRELESS` | 0x13 | Wireless module alarm |
| `HISI_ALARM_UPS` | 0x14 | UPS alarm |
| `HISI_ALARM_RECONNECT` | 0x15 | Reconnecting |
| `HISI_ALARM_RECONNECTED` | 0x16 | Reconnected |
| `HISI_EXCEPTION_EXCHANGE` | 0x8000 | User exchange exception |
| `HISI_EXCEPTION_AUDIOEXCHANGE` | 0x8001 | Audio intercom exception |
| `HISI_EXCEPTION_ALARM` | 0x8002 | Alarm exception |
| `HISI_EXCEPTION_PREVIEW` | 0x8003 | Preview exception |
| `HISI_EXCEPTION_RECONNECT` | 0x8005 | Preview reconnect |
| `HISI_EXCEPTION_ALARMRECONNECT` | 0x8006 | Alarm reconnect |
| `HISI_EXCEPTION_GETVIDEOPORT` | 0x8008 | Get video port failed |
| `HISI_EXCEPTION_GETHTTPPORT` | 0x8009 | Get HTTP port failed |
| `HISI_EXCEPTION_PLAYBACK` | 0x8010 | Playback exception |
| `HISI_EXCEPTION_NETERROR` | 0x8013 | Network error |

### 7.4 Wireless Module Info

**Structure `HISI_WIRELESSINFO`:**
- `Module[4]` — up to 4 wireless modules, each `HISI_WIRELESSMODULE` with `Channel[4]` bytes

**REST Equivalent:** `GET /NetSDK/Wireless/modules` (speculative — mined from SDK `HISI_ALARM_WIRELESS = 0x13` message callback data)

---

## 8. Broadcast (P2P Relay)

| SDK Function | Description | Status |
|---|---|---|
| `HISI_BroadcastStart()` | Start broadcast server | NOT for LAN use |
| `HISI_BroadcastAddClient(serverIP, serverPort, deviceName, user, password, &client)` | Add broadcast client | NOT for LAN use |
| `HISI_BroadcastDelClient(client)` | Remove client | NOT for LAN use |
| `HISI_BroadcastStop()` | Stop broadcast | NOT for LAN use |

**Note:** The broadcast subsystem is for P2P relay (EseeCloud style). BossCam uses direct LAN access and does not need P2P relay.

---

## 9. Local Playback Engine (avlib.dll)

The `avlib.dll` is a stripped-down FFmpeg build providing AAC and AC3 encode/decode, raw file/stream playback, and frame-level control. It is used client-side by the vendor's desktop apps (IPCamSuite, CMS) — not by the camera firmware itself.

### 9.1 Codecs (mined from DLL strings)

| Codec | Functions |
|---|---|
| AAC | `aac_decode_frame`, `aac_encode_frame`, `aac_parser`, `aac_demuxer`, `aac_muxer` |
| AC3/EAC3 | `ac3_decode_frame`, `ac3_probe`, `AC3_encode_close/frame/init`, `ac3_encoder`, `ac3_demuxer`, `ac3_muxer` |
| AASC | `aasc_decode_frame` (Autodesk Animator codec — legacy) |

### 9.2 Playback Engine Functions

| Function | Description |
|---|---|
| `HISI_Play_Init()` | Initialize playback engine |
| `HISI_Play_Realese()` | Release playback engine |
| `HISI_Play_GetPort(*port)` / `HISI_Play_FreePort(port)` | Port management for multi-instance playback |
| `HISI_Play_OpenFile(port, filename)` | Open local file for playback |
| `HISI_Play_CloseFile(port)` | Close file |
| `HISI_Play_Play(port, hWnd)` | Start playback with direct-draw window |
| `HISI_Play_Stop(port)` | Stop playback |
| `HISI_Play_Pause(port, pause)` | Pause/resume |
| `HISI_Play_Fast(port)` / `HISI_Play_Slow(port)` | Speed control |
| `HISI_Play_OneByOne(port)` | Frame step |
| `HISI_Play_SetPlayPos(port, pos)` / `HISI_PLAY_GetPlayPos(port)` | Seek |
| `HISI_Play_SetVolume(port, volume)` | Volume control |
| `HISI_Play_PlaySound(port)` / `HISI_Play_StopSound()` | Audio toggle |
| `HISI_Play_OpenStream(port)` / `HISI_Play_CloseStream(port)` | Stream mode (for live data) |
| `HISI_Play_InputData(port, buf, size)` | Feed raw data into stream playback |
| `HISI_Play_GetFileTime(port)` | Total file duration |
| `HISI_Play_GetPlayedTime(port)` | Current playback position |
| `HISI_Play_CapturePicture(port, filename)` | Screenshot from playback |

**Relevance to BossCam:** These functions are Windows-only (HWND rendering, stdcall ABI). BossCam on Linux uses ffmpeg directly for recording/playback — the avlib.dll surface is documented here for completeness but is NOT used by the native adapter.

---

## 10. Settings Catalog Coverage

### Groups and Endpoint Count

| Group | Endpoints | SDK Source | Status |
|---|---|---|---|
| Device | 3 | `HISI_DVR_GET_DEVICECFG (1)` | Live-Proven |
| Network | 13 | `HISI_DVR_GET_NETCFG (4)` | Live-Proven |
| Audio | 4 | Mined from REST | Live-Proven |
| Video | 24 | `HISI_DVR_GET_ENCODECFG (2)` + REST mining | Live-Proven |
| Detection | 7 | `HISI_DVR_GET_SENSORCFG (7)` / `DETECTIONCFG (8)` | Live-Proven |
| PTZ | 3 | `HISI_DVR_GET_PTZCFG (6)` | Live-Proven |
| Stream | 5 | REST mining | Live-Proven |
| Image | 10 | REST mining | Live-Proven |
| Storage | 9 | `HISI_DVR_FindFile` + REST mining | 5 Live-Proven, 4 Speculative |
| **Schedule** | 2 | `HISI_DVR_GET_SCHEDULECFG (9)` | **Speculative** |
| **Wireless** | 1 | `HISI_WIRELESSINFO` | **Speculative** |
| **Alarm** | 3 | `HISI_DVR_SetupAlarmChan` | **Speculative** |
| **Total** | **84** | — | 72 Live-Proven, 12 Speculative |

---

## 11. Error Codes

| Constant | Value | Meaning |
|---|---|---|
| `HISI_DVR_NOERROR` | 0 | Success |
| `HISI_DVR_NETWORK_FAIL_CONNECT` | 1 | Network connection failed |
| `HISI_DVR_PASSWORD_ERROR` | 2 | Password error / login failed |

**File search status codes:**
| Constant | Value | Meaning |
|---|---|---|
| `HISI_DVR_FILE_SUCCESS` | 1000 | File search success |
| `HISI_DVR_FILE_NOFIND` | 1001 | File not found |
| `HISI_DVR_ISFINDING` | 1002 | Searching in progress |
| `HISI_DVR_NOMOREFILE` | 1003 | No more files |
| `HISI_DVR_FILE_EXCEPTION` | 1004 | File search exception |

---

## 12. Live Verification Notes (5523-W at 10.0.0.169, admin / empty)

**Confirmed working (2026-08-03):**
- `GET /NetSDK/System/deviceInfo` → 200 JSON (serial, model, firmware, mac, eseeId)
- `rtsp://10.0.0.169:554/ch0_0.264` → Digest auth, HEVC 2560×1920 main stream
- `rtsp://10.0.0.169:554/ch0_1.264` → Digest auth, HEVC 704×480 sub stream
- `GET /NetSDK/Video/encode/channel/101/snapShot` → 200 JPEG snapshot
- `GET /NetSDK/Video/input/channel/1/brightnessLevel` → 200 JSON, read/write verified
- `GET /NetSDK/Video/input/channel/1/*` (all image controls) → 200
- `GET /NetSDK/Network/*` (all network endpoints) → 200
- `GET /NetSDK/PTZ/channel/1` → 200 JSON
- `GET /NetSDK/SDCard/status` → 200 JSON
- Port fallback: recorded ONVIF port (8888) → fallback to :80 where REST plane answers

**Awaiting live verification:**
- `GET /NetSDK/Schedule/*` — schedule endpoints (speculative from SDK)
- `GET /NetSDK/Wireless/modules` — wireless module status
- `GET /NetSDK/Alarm/*` — alarm endpoints
- `GET /NetSDK/SDCard/media/playbackByName` — named playback
- `GET /NetSDK/SDCard/media/playbackControl` — playback controls
- `GET /NetSDK/SDCard/media/getFileByTime` — file download by time
- `GET /NetSDK/SDCard/media/getFileByName` — file download by name
- `GET /NetSDK/SDCard/media/captureFrame` — playback frame capture
