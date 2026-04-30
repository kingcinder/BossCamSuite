# PRIVATE PAYLOAD TRUTH

Classification: PAYLOAD_UNKNOWN_CONTAINER

Observed markers:
- FlvMarkers: 18
- HdpContentType: 48
- JsonContentType: 78
- BasicAdminEmpty: 56
- DigestRtsp: 32
- NetsdkDeviceInfo: 56
- IPCamSuite NetSdk FLV/bubble clues: True
- IPCamSuite CNetClient stream exports: True

Interpretation:
- The capture and binaries indicate a nonstandard port-80 transport with FLV-like/private HDP markers.
- No standard high-resolution RTSP or HTTP URL has been proven from these artifacts.
- Raw NAL extraction is not proven because TCP stream reassembly and the private frame header mapping are still missing.
