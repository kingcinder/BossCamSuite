# 2026-04-22 Adapter Routing Fix Pass

## Mission Scope
- No SDK model expansion.
- Fix adapter routing so grouped writes hit real device endpoints.
- Prove at least one grouped write succeeds on live device.

## Adapter Trace Findings
- Root cause of `No control adapter matched the device` in grouped write flow:
  - grouped writes set `WritePlan.AdapterName = "sdk-enumerator"`
  - runtime resolver filters adapters by exact adapter name
  - no concrete adapter named `sdk-enumerator` exists
  - result: zero candidates before capability probe

## Fixes Applied
- Removed forced fake adapter name from grouped writes.
- Added adapter-resolution trace logging in `SettingsService.ResolveAdapterAsync`.
- Added write-routing failure trace in `SettingsService.WriteAsync` with endpoint/method/payload.
- Added HTTP request/response trace in `HttpControlAdapterBase.SendAsync` including:
  - URL
  - method
  - auth mode used
  - headers
  - payload
  - status and raw response
- Added auth fallback retry:
  - attempt Basic first
  - on 401 retry with credential cache (digest-capable handshake path)
- Added endpoint candidate routing for grouped writes:
  - Image: `/NetSDK/Image`, `/NetSDK/Image/0`, `/NetSDK/Video/input/channel/0`, `/NetSDK/Video/input/channel/1`
  - VideoEncode: `/NetSDK/Video/encode/channel/0`, `/101`, `/101/properties`, `/102`, `/102/properties`
  - Network/Wifi: `/NetSDK/Network/interfaces`, `/NetSDK/Network/interfaces/0`, `/NetSDK/Network/Ports`, `/NetSDK/Network/Dns`, `/NetSDK/Network/Esee`
- Added payload shape candidates per group, including wrapped full-struct payloads (for example `{ "Image": { ... } }`) in addition to direct full object payloads.

## Live Validation (10.0.0.29)
- Device: `5523-W` at `10.0.0.29`
- Minimum success test achieved:
  - `brightness` write routed to `/NetSDK/Video/input/channel/1`
  - response: `{"requestMethod":"PUT","requestURL":"/NetSDK/Video/input/channel/1","statusCode":0,"statusMessage":"OK"}`
  - classification: `RequiresCommitTrigger` (accepted write path with commit-trigger behavior)

### Full forced re-enumeration after routing fix
- Total SDK fields tested: 83
- Classification summary:
  - `RequiresCommitTrigger`: 4
  - `ReadableOnly`: 17
  - `Unsupported`: 62
- Writable/accepted image fields under grouped writes:
  - `brightness`
  - `contrast`
  - `saturation`
  - `sharpness`

## Working Endpoint Paths Confirmed
- `GET /NetSDK/System/deviceInfo`
- `GET /NetSDK/Video/input/channel/1`
- `PUT /NetSDK/Video/input/channel/1` (accepted)
- `GET /NetSDK/Image`
- `GET /NetSDK/Image/irCutFilter`
- `GET /NetSDK/Video/encode/channel/101/properties`

## Payload Example (working)
```json
{
  "id": 1,
  "enabled": true,
  "powerLineFrequencyMode": 60,
  "brightnessLevel": 61,
  "contrastLevel": 50,
  "sharpnessLevel": 50,
  "saturationLevel": 50,
  "hueLevel": 50,
  "flipEnabled": false,
  "mirrorEnabled": false,
  "privacyMask": [
    { "id": 1, "enabled": false, "regionX": 0, "regionY": 0, "regionWidth": 0, "regionHeight": 0, "regionColor": "0" }
  ]
}
```

## Trace Artifacts
- `artifacts/sdk-mining/adapter_trace_live_write.out.log`
- `artifacts/sdk-mining/adapter_trace_live_write.err.log`
- `artifacts/sdk-mining/adapter_trace_full_enum.out.log`
- `artifacts/sdk-mining/adapter_trace_full_enum.err.log`
- `artifacts/sdk-mining/live_minimum_success_results.json`
- `artifacts/sdk-mining/full_forced_enumeration_10.0.0.29.json`
