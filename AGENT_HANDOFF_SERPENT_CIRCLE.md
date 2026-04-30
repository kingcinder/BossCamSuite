# Agent Handoff - Serpent Circle

Standing law: observe, verify, adapt, persist, project, re-verify.

Checklist before editing:
- Read the current code path.
- Separate candidate from verified endpoint truth.
- Keep ONVIF-declared metadata separate from playback-probed metadata.
- Keep PTZ service separate from mechanical PTZ.
- Preserve per-camera truth over model templates.

Checklist before final response:
- Did build/test run?
- Was any live camera probe actually run?
- Are partial and blocked items explicit?
- Were drift notes created for contradictions?

Failure report format:

COMPLETED:
- real changes made

VERIFIED:
- build/test/live commands actually run

PARTIAL:
- implemented but not live-verified

BLOCKED:
- exact blocker

DRIFT FOUND:
- contradiction between model/template and live camera

NEXT RECOVERY STEP:
- one concrete next action
