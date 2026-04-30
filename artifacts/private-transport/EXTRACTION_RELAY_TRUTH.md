# EXTRACTION OR RELAY TRUTH

Result: PRIVATE_TRANSPORT_RELAY_BLOCKED_SDK_MAPPING

What was proven:
- IPCamSuite has a private NetSdk.dll path with CNetClient/OpenStreamEx/GetStreamDes clues.
- The NVR SDK exposes callback APIs, but the exact IPCamSuite NetSdk.dll ABI and channel mapping are not yet proven.
- The converted capture contains private port-80 payload clues, but no directly reusable standard high-res URL.

Missing for relay:
- Concrete CNetClient construction/initialization ABI or a validated HISI_DVR_Login/RealPlayEx proof against 10.0.0.227.
- Mapping from NetSDK encode channel 101/102 to SDK preview channel/stream parameters.
- Frame callback payload format or TCP-reassembled FLV/HDP body extraction proving H.264 elementary stream boundaries.

Next material strategy:
- Build a tiny native SDK harness that calls HISI_DVR_Init/Login with admin and an explicit empty password, then tests RealPlayEx stream values while saving callback bytes. This is a different strategy than URL probing.
