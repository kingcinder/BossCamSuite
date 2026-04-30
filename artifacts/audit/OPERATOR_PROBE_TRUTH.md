@"
# OPERATOR PROBE TRUTH

- Script: tools/Probe-5523W-HighRes.ps1
- Docs: docs/5523W-HighRes-Recovery.md
- Empty password parameter uses [AllowEmptyString()].
- Candidate probes include raw RTSP and explicit tsp://admin:@... RTSP.
- Snapshot dimensions are classified; LOWRES_ONLY is not high-res success.
- Script parse: PASS (SCRIPT_PARSE_OK).

Validation: Release build PASS.
