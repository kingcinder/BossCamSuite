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

## Current Interpretation

The expected current result is not a Blue Iris URL. Evidence points at IPCamSuite using a private port-80 transport through `NetSdk.dll` / `CNetClient` with `bubble/live`, `livestream`, FLV-like, and `text/HDP` payload clues.

No Blue Iris high-resolution settings should be printed until a direct standard stream or local relay is proven. `/snapshot.jpg` is low-resolution only and must not be treated as a completed high-resolution path.

## Next Material Recovery Step

Build a minimal native SDK harness that validates either:

- `HISISDK.dll` can log in to `10.0.0.227` with `admin` and an explicit empty password, request live preview, and save callback bytes; or
- IPCamSuite `NetSdk.dll` can be driven through its `CNetClient` exports with the correct ABI.

That harness is the next distinct strategy after RTSP/HTTP URL probing and packet inspection.
