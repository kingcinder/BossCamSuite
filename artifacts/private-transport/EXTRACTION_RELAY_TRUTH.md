# EXTRACTION OR RELAY TRUTH

Result: PASS_SDK_RELAY_IMPLEMENTED

What was proven:
- go2rtc consumed the private bubble upstream and exposed standard local RTSP.
- Upstream main: bubble://admin:@10.0.0.227:80/bubble/live?ch=0&stream=0
- Relay main: rtsp://127.0.0.1:8554/5523w_main
- Main probe: h264 2560x1920 15/1
- Relay sub: rtsp://127.0.0.1:8554/5523w_sub
- Sub probe: h264 704x480 15/1
