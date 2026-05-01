# Serpent Circle Operating Law

BossCamSuite operates on camera-specific proof, not model assumptions.

## Observe
Collect live evidence: device responses, service endpoints, ONVIF profiles, stream URIs, RTSP/ffprobe playback results, failures, timeouts, and unauthorized responses.

## Verify
Mark each endpoint as `Verified`, `Failed`, `Unauthorized`, `Unsupported`, `Timeout`, `Untested`, or `UnverifiedCandidate`. Do not claim live verification unless a live probe command actually ran.

## Adapt
Treat model, firmware, and templates as hints. If same-model cameras disagree, preserve both maps and record drift notes.

## Persist
Save endpoint truth per camera. Never overwrite verified per-camera truth with weaker defaults, templates, or candidates.

## Project
UI and playback must read persisted truth. ONVIF-declared stream metadata and playback-probed metadata stay separate. Decoder choice follows playback probes.

## Re-Verify
After behavior changes, run `dotnet build BossCamSuite.sln`, `dotnet test BossCamSuite.sln`, and `scripts/validate-serpent-circle.ps1`.

## Banned Shortcuts
- No global endpoint truth tables.
- No codec inference from `.264` or `.h264` path suffixes.
- No assumption that same make/model/firmware means same endpoints.
- No treating ONVIF declared encoding as playback codec truth.
- No treating PTZ service as proof of mechanical PTZ.
- No failing whole import because one endpoint failed.
- No overwriting verified per-camera truth with weaker defaults/templates.
- No claiming live verification without live probe evidence.
