# 5523-W Private Transport Reverse Engineering

This pass starts after standard ONVIF/RTSP and low-resolution snapshot probing has already proven the 10.0.0.227 problem:

- NetSDK channel 101 exists as 2560x1920 H.264.
- ONVIF advertises `/ch0_0.264` and `/ch0_1.264`.
- RTSP with lowercase `admin` and an explicit empty password returns 401.
- `http://10.0.0.227/snapshot.jpg` works but is `LOWRES_ONLY` at 704x480.
- IPCamSuite can preview the high-resolution feed over port 80, so the remaining task is private transport recovery, not password guessing.

## Run

From the BossCamSuite repo:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\Inspect-5523W-PrivateTransport.ps1 -Ip 10.0.0.227
```

The script preserves explicit empty-password auth:

```powershell
-Username admin -Password ""
```

## Artifacts

The script writes:

- `artifacts/private-transport/CAPTURE_TRUTH.md`
- `artifacts/private-transport/IPCAMSUITE_BINARY_TRUTH.md`
- `artifacts/private-transport/SDK_API_TRUTH.md`
- `artifacts/private-transport/PRIVATE_PAYLOAD_TRUTH.md`
- `artifacts/private-transport/EXTRACTION_RELAY_TRUTH.md`
- `artifacts/private-transport/BLUE_IRIS_SURFACE_TRUTH.md`
- `artifacts/private-transport/CIRCLE_CLOSURE.md`
- `artifacts/private-transport/private-transport-summary.json`

## Proven Relay Path

The high-resolution stream is available through go2rtc's `bubble://` protocol handler:

```text
Upstream main: bubble://admin:@10.0.0.227:80/bubble/live?ch=0&stream=0
Upstream sub:  bubble://admin:@10.0.0.227:80/bubble/live?ch=0&stream=1
Relay main:    rtsp://127.0.0.1:8554/5523w_main
Relay sub:     rtsp://127.0.0.1:8554/5523w_sub
```

Run:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\Start-5523W-BubbleRelay.ps1 -Ip 10.0.0.227 -Username admin -Password "" -StopExisting
```

The script downloads go2rtc if needed, starts the relay, and runs `ffprobe` against both local RTSP outputs. A passing run writes `artifacts/private-transport/go2rtc/bubble-relay-summary.json`.

## Current Interpretation

The expected current result is not a Blue Iris URL. Evidence points at IPCamSuite using a private port-80 transport through `NetSdk.dll` / `CNetClient` with `bubble/live`, `livestream`, FLV-like, and `text/HDP` payload clues.

Blue Iris should consume the local relay, not the camera's failing native RTSP auth path:

```text
Address: 127.0.0.1
Port: 8554
Path main: /5523w_main
Path sub: /5523w_sub
User: blank
Password: blank
Make/Model: Generic/ONVIF or VLC-compatible RTSP
```

`/snapshot.jpg` remains low-resolution only and must not be treated as a completed high-resolution path.

## Next Material Recovery Step

Build a minimal native SDK harness that validates either:

- `HISISDK.dll` can log in to `10.0.0.227` with `admin` and an explicit empty password, request live preview, and save callback bytes; or
- IPCamSuite `NetSdk.dll` can be driven through its `CNetClient` exports with the correct ABI.

That harness is the next distinct strategy after RTSP/HTTP URL probing and packet inspection.
