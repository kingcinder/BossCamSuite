# CAPTURE TRUTH

Result: CAPTURE_PAYLOAD_SUFFICIENT

Input folder: C:\Users\ceide\Documents\BossCamSuite\artifacts\5523w-highres\10.0.0.227\20260430-074324

Files inspected:
- candidate_urls.txt (146 bytes)
- candidate-1.ffprobe.err.txt (160 bytes)
- candidate-1.ffprobe.out.txt (8 bytes)
- candidate-2.ffprobe.err.txt (160 bytes)
- candidate-2.ffprobe.out.txt (8 bytes)
- candidate-3.ffprobe.err.txt (167 bytes)
- candidate-3.ffprobe.out.txt (8 bytes)
- candidate-4.ffprobe.err.txt (167 bytes)
- candidate-4.ffprobe.out.txt (8 bytes)
- channel-101-properties.json (1976 bytes)
- deviceInfo.json (398 bytes)
- encode-channels.json (1200 bytes)
- ffprobe-results.json (2505 bytes)
- http_requests.tsv (71 bytes)
- pktmon.etl (24340451 bytes)
- pktmon.txt (57633048 bytes)
- request-keyframe.txt (916 bytes)
- snapshot.jpg (46231 bytes)
- summary.json (555 bytes)

Capture conversion:
- pktmon.etl present: True
- pktmon text present: True
- pcapng present: True
- tshark available: False

Payload-level evidence counts:
- FlvMarkers: 18
- HdpContentType: 48
- JsonContentType: 78
- BasicAdminEmpty: 56
- DigestRtsp: 32
- NetsdkDeviceInfo: 56

Flows found:
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898565:2585898848, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898565:2585898848, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898565:2585898848, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898565:2585898848, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898565:2585898848, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898565:2585898848, ack 383389609, win 3534, length 283: HTTP
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585898848, win 511, length 0
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 74: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898848:2585898868, ack 383389609, win 3534, length 20: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1414: 10.0.0.227.80 > 10.0.0.186.63151: Flags [.], seq 2585898868:2585900228, ack 383389609, win 3534, length 1360: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 74: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898848:2585898868, ack 383389609, win 3534, length 20: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1414: 10.0.0.227.80 > 10.0.0.186.63151: Flags [.], seq 2585898868:2585900228, ack 383389609, win 3534, length 1360: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 74: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898848:2585898868, ack 383389609, win 3534, length 20: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1414: 10.0.0.227.80 > 10.0.0.186.63151: Flags [.], seq 2585898868:2585900228, ack 383389609, win 3534, length 1360: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 74: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898848:2585898868, ack 383389609, win 3534, length 20: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1414: 10.0.0.227.80 > 10.0.0.186.63151: Flags [.], seq 2585898868:2585900228, ack 383389609, win 3534, length 1360: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 74: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898848:2585898868, ack 383389609, win 3534, length 20: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1414: 10.0.0.227.80 > 10.0.0.186.63151: Flags [.], seq 2585898868:2585900228, ack 383389609, win 3534, length 1360: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 74: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585898848:2585898868, ack 383389609, win 3534, length 20: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1414: 10.0.0.227.80 > 10.0.0.186.63151: Flags [.], seq 2585898868:2585900228, ack 383389609, win 3534, length 1360: HTTP
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- D4-5D-64-D1-07-F5 > 9C-A3-A9-B9-BF-55, ethertype IPv4 (0x0800), length 54: 10.0.0.186.63151 > 10.0.0.227.80: Flags [.], ack 2585900228, win 511, length 0
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1276: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585900228:2585901450, ack 383389609, win 3534, length 1222: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585901450:2585901733, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1276: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585900228:2585901450, ack 383389609, win 3534, length 1222: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585901450:2585901733, ack 383389609, win 3534, length 283: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 1276: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585900228:2585901450, ack 383389609, win 3534, length 1222: HTTP
- 9C-A3-A9-B9-BF-55 > D4-5D-64-D1-07-F5, ethertype IPv4 (0x0800), length 337: 10.0.0.227.80 > 10.0.0.186.63151: Flags [P.], seq 2585901450:2585901733, ack 383389609, win 3534, length 283: HTTP

Request paths if present:
- /netsdk/system/deviceInfo observed in converted payload strings: True
- RTSP Digest attempts observed in converted payload strings: True

Payload sufficiency:
- The pktmon ETL converted to pcapng and contains HTTP/auth/content-type clues. It is sufficient to prove private port-80 transport clues.
- It is not yet sufficient to emit a playable elementary stream without TCP reassembly or SDK frame mapping.

If stronger payload extraction is needed:
```powershell
tshark -i <adapter> -f "host 10.0.0.227 and tcp port 80" -a duration:45 -w artifacts/private-transport/10.0.0.227-ipcamsuite-preview.pcapng
tshark -r artifacts/private-transport/10.0.0.227-ipcamsuite-preview.pcapng -Y "http or tcp.port==80" -T fields -e frame.number -e ip.src -e tcp.srcport -e ip.dst -e tcp.dstport -e http.request.method -e http.request.uri -e http.content_type -e tcp.len > artifacts/private-transport/http_payload_index.tsv
```
