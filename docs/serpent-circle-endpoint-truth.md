# Serpent Circle Endpoint Truth

Endpoint discovery follows this loop:

1. Observe device responses, service endpoints, ONVIF metadata, stream URIs, playback probes, failures, timeouts, and authorization results.
2. Verify each endpoint independently.
3. Adapt to differences between live proof and templates by recording drift.
4. Persist per-camera endpoint truth.
5. Project only persisted/proven truth into UI and playback.
6. Re-verify with build, tests, and validation scans.

Candidate endpoint lists may include ONVIF services, snapshots, vendor CGI paths, and RTSP fallback paths, but they remain `UnverifiedCandidate` until probed on that camera. One failed endpoint is partial truth, not a failed import.

The 10.0.0.29 5523-W sample is fixture truth for that camera only. It proves that `.264` can carry HEVC and that ONVIF-declared JPEG can be wrong. It does not create a global 5523-W endpoint map.
