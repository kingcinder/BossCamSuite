# 2026-04-22 SDK-Driven Forced Enumeration Pass

## Scope
- Reconstructed grouped control modeling around SDK-defined fields (including fields absent in first readback).
- Added forced SDK field enumeration pipeline with inject-missing behavior and explicit per-field classification.
- Added alarm/storage config groups and surfaced SDK enumeration action in Advanced UI.

## Implemented

### Struct-based grouped model expansion
- Added grouped kinds:
  - `AlarmConfig`
  - `StorageConfig`
- Added `SdkFieldDefinition` catalog model and `ForcedEnumerationRequest`.

### Forced field enumeration
- Added `GroupedConfigService.ForceEnumerateSdkFieldsAsync(...)`:
  - loads latest device snapshot
  - resolves endpoint by SDK pattern
  - reads baseline field value
  - injects missing fields into full-object grouped payloads
  - writes full payload
  - verifies immediate and delayed readback (1s/3s/5s)
  - retries write (secondary + resend)
  - classifies each field:
    - `Writable`
    - `ReadableOnly`
    - `Ignored`
    - `RequiresGroupedWrite`
    - `RequiresCommitTrigger`
    - `DelayedApply`
    - `Unsupported`
  - persists results in grouped retest store path (with classification metadata)

### SDK field catalog included for groups
- `ImageConfig`
- `VideoEncodeConfig`
- `NetworkConfig`
- `WifiConfig`
- `UserConfig`
- `AlarmConfig`
- `StorageConfig`

### API additions
- `GET /api/grouped-config/sdk-field-catalog`
- `POST /api/devices/{id}/grouped-config/force-enumerate-sdk-fields`

### UI updates (Advanced)
- Added action button:
  - `Force Enumerate SDK Fields`
- On run, UI posts forced-enumeration request and shows classification summary toast.

## Build/Test
- `dotnet build BossCamSuite.sln` passed
- `dotnet test BossCamSuite.sln` passed (30/30)
