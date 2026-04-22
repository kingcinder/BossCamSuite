# 2026-04-22 Grouped Control Model Correction Pass

## Scope
- Shifted unsupported-field retesting from prior field-gated assumptions to grouped full-object write behavior.
- Implemented persistence of grouped apply/commit behavior by firmware/IP/group.
- Added operator-facing grouped apply indicators and image behavior warnings.

## Implemented

### Struct-based grouped model
- Added grouped config domain models:
  - `ImageConfig`
  - `VideoEncodeConfig`
  - `NetworkConfig`
  - `WifiConfig`
  - `UserConfig`
- Implemented grouped full-object write flow in `GroupedConfigService`:
  - read full endpoint object
  - modify target path
  - write full payload back

### Apply/commit discovery
- Added grouped retest workflow for previously unsupported fields:
  - immediate readback
  - delayed readback at 1s/3s/5s
  - secondary write
  - resend write
- Classified outcomes:
  - `ImmediateApplied`
  - `DelayedApplied`
  - `RequiresSecondWrite`
  - `RequiresCommitTrigger`
  - `Unapplied`
- Persisted outcomes per firmware/IP/group:
  - `grouped_apply_profiles`
  - `grouped_retest_results`

### Image pipeline correction
- Step candidate generation updated for:
  - `brightness`
  - `contrast`
  - `saturation`
  - `sharpness`
  - `wdr`
- Uses ±1/±2/±4 probes to expose flat zones, cliffs, delayed effects, and unstable behavior through behavior maps.

### Reclassification correction
- Added grouped-retest promotions:
  - successful grouped retests promote fields to supported/write-verified in normalized fields.
  - image inventory promoted to `ProvenWritable` or `RequiresCommitTrigger` as appropriate.
- Stopped treating old `UnsupportedOnFirmware` as terminal truth in explicit classification mapping; down-shifted to `LikelyUnsupported` unless new grouped evidence proves otherwise.

### UI correction
- Added grouped apply indicator in main header.
- Added `Retest Unsupported (Grouped)` action in Advanced tab.
- Surfaced behavior badge lines for:
  - brightness
  - contrast
  - saturation
  - sharpness
  - wdr

## Build/Test
- `dotnet build BossCamSuite.sln` passed
- `dotnet test BossCamSuite.sln` passed
