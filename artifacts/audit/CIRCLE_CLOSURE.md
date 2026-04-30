@"
# BOSSCAM EMPTY PASSWORD SOURCE TRUTH REPORT

- starting commit: 8903dc3e2e970118da2231a01f589f9be8736b6a
- repair branch: repair/bosscam-empty-password-source-truth
- complete file audit count: 849

## Files changed
- .codex/prompts/BOSSCAM_FAILURE_REPORT_TEMPLATE.md
- .codex/prompts/BOSSCAM_SERPENT_CIRCLE_HEADER.md
- .codex/SERPENT_CIRCLE_SKILL.md
- .codex/SKILL.md
- .github/ISSUE_TEMPLATE/serpent-circle-truth-gap.md
- .github/pull_request_template.md
- AGENT_HANDOFF_SERPENT_CIRCLE.md
- CODEX_SERPENT_CIRCLE_PROMPT.md
- CONTRIBUTING.md
- docs/5523W-HighRes-Recovery.md
- docs/live-feed-smoke.md
- docs/serpent-circle-endpoint-truth.md
- README.md
- scripts/smoke-live-camera-feed.ps1
- scripts/validate-serpent-circle.ps1
- SERPENT_CIRCLE.md
- src/BossCam.Contracts/Models/DeviceContracts.cs
- src/BossCam.Contracts/Models/ProtocolContracts.cs
- src/BossCam.Core/Abstractions/Interfaces.cs
- src/BossCam.Core/BossCamRuntimeOptions.cs
- src/BossCam.Core/CoreServiceCollectionExtensions.cs
- src/BossCam.Core/Services/CameraEndpointTruthService.cs
- src/BossCam.Core/Services/EndpointTruthIntegrityGuard.cs
- src/BossCam.Core/Services/EndpointTruthLiveBuilder.cs
- src/BossCam.Core/Services/ProtocolValidationService.cs
- src/BossCam.Core/Services/RecordingService.cs
- src/BossCam.Desktop/AssemblyInfo.cs
- src/BossCam.Desktop/MainWindow.Nvr.cs
- src/BossCam.Desktop/MainWindow.xaml
- src/BossCam.Desktop/MainWindow.xaml.cs
- src/BossCam.Desktop/NvrFrameDecodeSession.cs
- src/BossCam.Infrastructure/InfrastructureServiceCollectionExtensions.cs
- src/BossCam.Infrastructure/Persistence/SqliteApplicationStore.cs
- src/BossCam.Infrastructure/Video/VideoTransportAdapters.cs
- src/BossCam.Service/appsettings.json
- src/BossCam.Service/fixtures/5523w/__IPC/image-behavior-maps.json
- src/BossCam.Service/fixtures/5523w/__IPC/image-inventory.json
- src/BossCam.Service/fixtures/5523w/__IPC/image-writable-test-set.json
- src/BossCam.Service/Program.cs
- tests/BossCam.Tests/CameraEndpointTruthTests.cs
- tools/Probe-5523W-HighRes.ps1

## Issue-by-issue status
| # | Issue | Status | Evidence |
|---|-------|--------|----------|
| 1 | Desktop hardcoded local API URL | FIXED | `BOSSCAM_LOCAL_API_BASE_URL` env override; default remains 127.0.0.1:5317 |
| 2 | Desktop service health proof | FIXED | `EnsureServiceHealthAsync` checks `/api/health` before workflows |
| 3 | Swagger/CORS permissive | FIXED | Swagger gated; CORS loopback-only; LAN bind requires `BossCam:AllowLanApi=true` |
| 4 | Wrong NetSDK source path | FIXED | Stream adapter now probes `/NetSDK/Video/encode/channels` |
| 5 | Useless root RTSP fallback | FIXED | root `rtsp://IP:554` fallback removed |
| 6 | Bubble/private HTTP port drift | PARTIAL | Existing adapter still uses device.Port; NetSDK source truth uses HTTP port 80 defaults; deeper metadata resolver deferred |
| 7 | Empty password collapsed | FIXED | `CredentialState.UsernameOnlyEmptyPassword`, explicit `rtsp://admin:@...`, Basic `admin:` in probe |
| 8 | IPCamSuite global credentials | PARTIAL | Guessing removed from validation candidates; import deep audit remains REVIEW in matrix |
| 9 | EseeCloud sensitive metadata | PARTIAL | Public API redacted; internal imports still REVIEW |
| 10 | `/api/devices` exposes secrets | FIXED | returns `DeviceSummaryDto`, no Password/PasswordCiphertext/ssid_pwd |
| 11 | NVR diagnostics leak URLs/args | FIXED | `SensitiveValueRedactor` applied to URLs/FFmpeg args |
| 12 | Preview hardcodes 640x360 without truth | FIXED | diagnostics expose preview resolution and source expected metadata |
| 13 | NVR first-frame timeout short | FIXED | default startup timeout 20s, env configurable |
| 14 | Recording interpolated FFmpeg args | FIXED | uses `ProcessStartInfo.ArgumentList` |
| 15 | RTSP flags on non-RTSP | FIXED | `-rtsp_transport tcp` only for RTSP |
| 16 | RecordingProfile.SourceId ignored | FIXED | recording resolves SourceId before fallback |
| 17 | appsettings user paths | FIXED | appsettings uses portable/env defaults |
| 18 | Runtime env expansion | FIXED | runtime options expand env vars and normalize paths |
| 19 | typed source truth states missing | FIXED | `SourceTruthOutcome`, `SourceTruthState`, candidates/results added |
| 20 | risky maintenance exposure | FIXED | dangerous ops require loopback and explicit confirmation |
| 21 | SQLite sensitive payload | PARTIAL | public surfaces redacted; internal encrypted/segmented persistence deferred as migration-risk |
| 22 | deterministic 5523-W high-res probe | FIXED | `tools/Probe-5523W-HighRes.ps1` parses and preserves empty password |

## Build/test/format/script parse
- restore: PASS
- Release build: PASS
- tests: PASS 64/64
- format: PASS after applying whitespace normalization
- validation script: PASS
- PowerShell probe parse: PASS

## Remaining deferred items
- Bubble/private HTTP metadata resolver: PARTIAL, existing adapter still has simple device.Port behavior; high-res proof now depends on capture-derived probe script.
- Internal SQLite sensitive payload separation: PARTIAL, public DTO/diagnostics are redacted; migration is deferred to avoid storage regression.
- Live 10.0.0.227 high-res consumption: PARTIAL until operator runs the probe with IPCamSuite preview open; no live capture was run in this turn.

## Exact next operator action
Run: pwsh -ExecutionPolicy Bypass -File .\tools\Probe-5523W-HighRes.ps1 -Ip 10.0.0.227 -RequirePreviewOpen
"@ | Set-Content BOSSCAM_EMPTY_PASSWORD_SOURCE_TRUTH_REPORT.md
@"
# CIRCLE CLOSURE

1. Existing local working copy: YES.
2. Local copy modified decisively: YES.
3. Every inventoried file listed in audit matrix: YES, 849 rows.
4. Build/test pass: YES.
5. Source truth derives from NetSDK/ONVIF/sample evidence: YES for modeled truth; live 10.0.0.227 capture not run.
6. Lowercase admin empty password preserved: YES.
7. 10.0.0.227 classification on RTSP 401: FAIL_RTSP_EMPTY_PASSWORD_AUTH_NEGOTIATION.
8. /snapshot.jpg labeled LOWRES_ONLY: YES.
9. Credentials redacted: YES on public DTO and NVR diagnostics.
10. Operator probe reproduces truth: script parses; live run deferred pending IPCamSuite preview/camera availability.
11. Branch pushed/uploaded: YES, branch pushed to origin. PR creation blocked because gh CLI is unauthenticated.

Final result: CIRCLE_CLOSED_WITH_DEFERRED_ITEMS.

