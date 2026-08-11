# 2026-08-10 Beacon payload decode — HDS/1.0 LAN identity advertisement (eseeid confirmed, nonce flip, Wi-Fi AP PSK leak)

## Question

The camera's 760-byte UDP discovery broadcasts to `255.255.255.255` (src ports
`:8002` and `:18002`, on the fixed ~15-min grid — see
`2026-08-10-beacon-cadence-correlation.md`) are the only camera-originated egress the
dead-cloud campaign ever observed. What is **inside** them? Do they embed the
serial/eseeid (a second, independent confirmation of the derivation), and could the
beacon be a **registration probe in disguise** — a non-TCP cloud-dial vector we've been
overlooking?

## Tooling — `scripts/beacon-payload-extract.py`

Pure-Python, no-scapy pcap parser (SLL/SLL2/Ethernet-aware, classic-pcap LE/BE magic),
which:

- extracts every `10.0.0.169 → 255.255.255.255` UDP payload from all
  `captures/eseecloud-mitm-*/capture.pcap`
- hexdumps the full 092927Z pair
- ASCII-scans every payload for embedded strings and searches for the serial /
  `d9ffcc` / `abbccdde` registration signatures
- byte-diffs every emission against the first captured to expose constant vs varying
  fields (contiguous-range summarization)

Reproduce with:

```bash
python3 scripts/beacon-payload-extract.py          # default: all captures/eseecloud-mitm-*/capture.pcap
python3 scripts/beacon-payload-extract.py captures/eseecloud-mitm-20260809T092927Z/capture.pcap
```

Current corpus: **28 beacon packets across 20 emissions** (Aug 8 05:32 → Aug 10 04:01),
**all payloads exactly 760 bytes**.

## The payload — HDS/1.0 200 OK advertisement (plain text, not binary)

The 092927Z pair (both 760 bytes, **byte-identical** across the two packets of the
emission) is an `HDS/1.0` HTTP-style advertisement in the EseeCloud HDP protocol
family — **segment 1 of 2** (Data-Length:423) plus segment 2 (Data-Length:107):

```
HDS/1.0 200 OK
SERVER: nginx
CONTENT-LENGTH: 613
CONTENT-TYPE: text/HDP
CSEQ: 1
CLIENT-ID: BossCam1786268128758          ← generation timestamp (epoch-ms)
DEVICE-ID: Z7C34781620744                ← serial, JAZ prefix stripped
                                          (blank line = segment-1 body)
Segment-Num:2
Segment-Seq:1
Data-Length:423

Device-ID=Z7C34781620744
Device-Model=5523-W
Device-Name=5523-W
Esee-ID=4781620744                       ← ★ the pconv-derived eseeid, in the payload
Channel-Cnt=1
IP=10.0.0.169
MASK=255.255.255.0
MAC=9c:a3:a9:bc:6f:ec
Gateway=10.0.0.1
Software-Version=3.6.103.5721106
Http-Port=80
Dhcp=1
Fdns=0.0.0.0
Sdns=0.0.0.0
nonce=2ce8497827b90fa34600657493928156601214e9    ← 40-hex cloud-session nonce
Interface=wlan0
wireless=stationMode
wirelessApEssId=Aegon
wirelessApPsk=812354444                    ← ★ the camera's own Wi-Fi AP password
wirelessStationDhcp=true
                                          (blank line = segment-2 body)
Segment-Seq:2
Data-Length:107

[dev-media-info]
cam-count=1
[cam1]
id=1
stream-count=2
[cam1-stream1]
id=11
[cam1-stream2]
id=12
```

**Format quirk (do not misread):** the advertisement is *formatted as a server
response* (`HDS/1.0 200 OK`, `SERVER: nginx`) despite being an **unsolicited
broadcast**. There is no server involved — the "200 OK" is just the shape the device
reuses for its HDP frames.

## Findings

### 1. eseeid/serial embedded — independent confirmation of the derivation

- `Esee-ID=4781620744` in the payload **exactly matches** the derived
  `eseeid = '4' + serial[len('JAZ7C34'):]` → `'4' + '781620744'` = `4781620744`.
- The serial appears as **`Z7C34781620744` (JAZ-prefix-stripped)** — the payload does
  *not* contain `JAZ7C34` (search: absent). This is distinct from the full
  `JAZ7C34781620744` seen in the NetSDK `POST /address/device?sn=` frames; do not
  confuse the two serial forms.
- Derivation remains validated on 3/3 data points (`.29`: JAZ7C34780038910 /
  4780038910 / pconv 0x02d96045; `.169`: 4781620744 / pconv 0x02d99e0f; and now the
  beacon's own `Esee-ID` field for `.169`).

### 2. NOT a registration probe — a LAN discovery advertisement

- **No `d9ffcc` magic hello, no `abbccdde` grant, no counter, no pconv bytes** — all
  searches absent across every payload.
- It is **not** a TCP-alternative cloud-dial vector. It's a periodic identity
  broadcast that **leaks cloud-binding material to anyone on the LAN**: serial,
  eseeid, MAC, firmware, the live cloud-session nonce, and the Wi-Fi AP password.

### 3. ⚠️ The nonce flip — in step with the 12:01–15:02 dialing window

Exactly **two fields vary** across all 20 emissions (everything else is
byte-constant):

| Field | Behavior |
|---|---|
| `CLIENT-ID` | `BossCam<epoch-ms>` generation stamp — changes every emission (~1.4–1.6s before the packet hits the wire; measured 1.569s at 05:32, 1.404s at 12:01) |
| `nonce` | **40-hex cloud-session nonce — flipped exactly once, between 11:00Z and 12:01Z Aug 9** |

| Era | Nonce | Emissions |
|---|---|---|
| Aug 8 05:32 → Aug 9 11:00 | `2ce8497827b90fa34600657493928156601214e9` | all early + 092927Z + 10:00 + 11:00 |
| Aug 9 12:01 → Aug 10 04:01 | `8671353c7ec2c76ae2d009cd1776445bdec5ff98` | 12:01 through 04:01, held |

The byte-diff pins it: at `120147Z` the nonce region `0x01d0–0x01f7` changes (38 of 40
bytes) and stays changed through the latest emission. That flip is **precisely
coincident with the 12:01–15:02 dialing window** from the cadence report (123 camera
connections/session, 31 real `REGISTER LITE` frames/session, pconv `02d99e0f`, all six
reply variants served, 124 GATED / 0 OPEN). **The beacon channel is exposing the
camera's cloud-session state change on the LAN** — a passive side-channel for
detecting cloud-session resurrection with zero spoofing.

### 4. Wi-Fi AP PSK leak

`wirelessApEssId=Aegon` / **`wirelessApPsk=812354444`** — the camera's own soft-AP
credentials, broadcast unauthenticated on the 15-min grid. Anyone on the LAN can
associate with the camera's Wi-Fi AP. (Operator note: if this AP matters, change the
PSK in the camera's wireless config — it is currently an open secret.)

## Verdict

| Hypothesis | Result |
|---|---|
| Beacon embeds serial/eseeid | **Confirmed** — `Esee-ID=4781620744` == derivation; serial as JAZ-stripped `Z7C34781620744` |
| Beacon is a registration probe (cloud-dial via UDP) | **Rejected** — no `d9ffcc`/`abbccdde`/counter/pconv; it's an HDS/1.0 identity advertisement |
| Payload constant across emissions | **Rejected** — CLIENT-ID varies per emission; nonce flipped once, in step with dialing |
| Nonce tracks cloud-session state | **Strongly indicated** — single flip coincident with the 12:01–15:02 dialing window, then stable |

## Follow-ups

1. **Nonce as an attack input:** the verify/grant formula work should be re-tested
   against the *beacon* nonce (`8671353c...dec5ff98`) rather than only MITM-captured
   ones — it is a fresh, camera-sourced cloud-session nonce.
2. **Passive resurrection monitor:** the hourly pcap-audit (`gate-flip-heartbeat.sh`)
   can be extended to compare each tick's beacon nonce against the running value — a
   nonce change *is* the resurrection signal, detectable without any spoofing.
3. **PSK rotation:** change `wirelessApPsk` on `.169` since it has been broadcast
   cleartext on the LAN grid since Aug 8.
