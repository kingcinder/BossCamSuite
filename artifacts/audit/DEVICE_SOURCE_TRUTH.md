@"
# DEVICE SOURCE TRUTH

- Added credential state preserving explicit empty password.
- Added source truth outcome/state/candidate/result contracts.
- Source descriptors carry expected codec/resolution/frame rate, lowResOnly, authState, channelId, streamRole, sourceOfTruth.
- StreamDescriptorAdapter now uses NetSDK encode channels and ONVIF/persisted truth; root RTSP fallback removed.
- 10.0.0.227 projects main/sub RTSP candidates with explicit dmin: empty password and FAIL_RTSP_EMPTY_PASSWORD_AUTH_NEGOTIATION.
- /snapshot.jpg is projected as LOWRES_ONLY 704x480, never high-res success.

Validation: Release build PASS.
