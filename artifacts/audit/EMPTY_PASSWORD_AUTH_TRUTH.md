@"
# EMPTY PASSWORD AUTH TRUTH

Canonical credential state for 5523-W targets is lowercase dmin with explicit empty password.

10.0.0.227 classification when standard RTSP returns 401:
FAIL_RTSP_EMPTY_PASSWORD_AUTH_NEGOTIATION

Reason chain:
- high-res channel 101 exists by NetSDK truth
- ONVIF advertises /ch0_0.264
- HTTP Basic dmin: works for snapshot
- snapshot is LOWRES_ONLY 704x480
- RTSP tsp://admin:@10.0.0.227:554/ch0_0.264 returns 401
- therefore this is not UNKNOWN_PASSWORD or BAD_PASSWORD.

Validation: test Camera_227_Source_Truth_Preserves_Empty_Password_Auth_Failure_And_Lowres_Snapshot PASS.
