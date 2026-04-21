# 2026-04-21 decisive closeout pass report

## COMPLETED
- Expanded operator UI control surface without removing diagnostics.
- Kept all probe/truth/diagnostics systems and confined engineering views to `Advanced` tab.
- Added operator-facing controls for additional recoverable fields:
  - `gamma`
  - `whiteLight`
  - `infrared`
  - `osd`
  - `privacyMaskEnabled`, `privacyMaskX`, `privacyMaskY`, `privacyMaskWidth`, `privacyMaskHeight`
  - `alarmInputActiveState`, `alarmOutputActiveState`, `alarmPulseDuration`
  - `sdStatus`, `sdMediaType` (Storage tab)
  - camera identity fields `serial`, `mac`
  - network identity field `eseeId`
- Added `Storage` tab and expanded `Motion/Alarm` tab to include privacy/alarm IO controls.
- Added `gamma` to endpoint contract catalog (`image.profile`) and expanded adapter read endpoint candidates with:
  - `/NetSDK/Image/gamma`
  - `/NetSDK/Factory?cmd=WhiteLightCtrl`
  - `/NetSDK/Factory?cmd=InfraRedCtrl`
- Hardened normal-mode UI gating so expert-only fields are not surfaced in operator tabs.
- Expanded classification enum coverage to include additional required labels (`ProvenReadable`, `ProvenWritable`, `ProvenWritablePersistent`, `RequiresGroupedPayload`, `RequiresReboot`, `ReadOnly`, `HiddenCandidate`, `FirmwareVariantSpecific`, `BlockedByAuth`, `NotApplicableToHardware`, `TrulyUnsupported`).
- Build/test green after changes.

## PARTIAL
- Firmware deep extraction from `.rom` remains partial: header/signature/entropy/compression-carve analysis done, but filesystem/routes not fully unpacked.
- Full live persistence/reboot validation for every newly surfaced field is partial (depends on connected target devices and credentials during this run).

## BLOCKED
- **Firmware container unpack blocker**: provided ROM appears packed/obfuscated; standard carve/decompression signatures (`gzip`/`bzip2`) fail with invalid stream errors; no directly mountable FS header recovered from this pass.
- **Live device evidence blocker**: no guaranteed always-on target camera session in this run for exhaustive write+post-read+post-reboot confirmation per newly added field.

## Files changed
- `src/BossCam.Contracts/Models/SemanticTrustContracts.cs`
- `src/BossCam.Core/Services/EndpointContractCatalogService.cs`
- `src/BossCam.Desktop/MainWindow.xaml`
- `src/BossCam.Desktop/MainWindow.xaml.cs`
- `src/BossCam.Infrastructure/Control/HttpControlAdapters.cs`

## Before / after screenshots
- `artifacts/ui/before-operator-refactor.png`
- `artifacts/ui/after-operator-refactor.png`

## New UI flow
1. Select camera from left list.
2. Use operator tabs (`Camera`, `Image`, `Stream`, `Network`, `Users`, `Motion/Alarm`, `Storage`, `Maintenance`) for recoverable controls.
3. Use `Save Changes` for validated writes and `Advanced Apply` only for expert paths.
4. Use `Advanced` tab for diagnostics/truth maps/inventory/transcripts/raw JSON.

## Test results
- `dotnet build BossCamSuite.sln -c Release` passed.
- `dotnet test BossCamSuite.sln -c Release --no-build` passed (30/30).

## Next weakest link
- Deterministic unpack pipeline for the supplied `.rom` container (likely vendor-wrapped/encrypted stage) to extract internal web/handler/config assets for additional private-path recovery.
