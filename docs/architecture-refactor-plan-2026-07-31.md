# BossCamSuite Architecture Deepening Plan

**Goal:** Deepen the four architecture-review candidates without breaking the existing operator API: playable-source policy, recording lifecycle, typed control operations, and persistence seams.

**Approach:** Introduce focused policy modules behind the existing services first, prove each seam with behavior tests, then route existing consumers through the seam. Preserve public endpoint shapes and existing service constructors where possible; use compatibility façades rather than a flag-day rewrite.

## Scope

1. `PlayableSourcePolicy`: one decision and explanation for main/sub/snapshot fallback selection.
2. `RecordingLifecycleCoordinator`: centralize job ownership, process attachment, stop/restart transitions, and delegate segment/index/export concerns to focused policies.
3. `TypedControlOperation`: centralize evidence gate, payload construction, apply/verify, semantic observation, redaction, and audit behavior while keeping `TypedSettingsService` as the endpoint-facing façade.
4. Domain-focused persistence seams: introduce recording, device, control-evidence, and diagnostics persistence façades over `IApplicationStore`/SQLite without changing the database schema in this pass.

## Constraints

- Test-first for every new behavior and seam.
- No new external dependencies.
- Preserve REST routes and response contracts.
- Preserve existing uncommitted user changes.
- Keep SQLite as the storage engine and existing recording pipeline adapters.
- Do not expose credentials in source decisions, recording jobs, control results, or persistence logs.

## Verification

- Focused xUnit tests after each slice.
- `dotnet test BossCamSuite.Linux.sln -c Release` after all slices.
- `npm run build` in `src/BossCam.ManagementUI` if TypeScript/Svelte files are affected.
- Code review after implementation.
