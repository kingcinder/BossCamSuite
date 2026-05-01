@"
# DECODE RECORD TRUTH

- NvrFrameDecodeSession startup timeout default is 20 seconds, env override BOSSCAM_NVR_STARTUP_TIMEOUT_SECONDS.
- FFmpeg decode includes bounded network probe flags.
- RecordingService uses ArgumentList, not a giant interpolated args string.
- RTSP transport flag is RTSP-only.
- RecordingProfile.SourceId is respected.

Validation: Release build/test PASS.
