# 2026-08-09 Gate-flip experiment — can ANY server reply unlock the /user gate?

## Executive summary

A one-hour MITM campaign against `10.0.0.169` (`5523-W`, firmware `03061030`) was built to
answer a precise question: **is the byte-accurate session grant the *only* server reply that
can flip the camera's `/user/*.xml` "check in falied" gate, or would *any* reply shape do it?**
The experiment cycles every candidate server reply — `cadence`, `plus1`, `badoffset`, `magic`,
`echo`, `empty` — per registration while a parallel poller watches the gate.

**The measurement came back VOID, and that void is itself the answer.** Over the run window
the camera made **zero outbound connections** — no SYN to any redirected cloud IP, no DNS
redirect, no WebSocket registration, no `/message` retry. The fake server never received a
single frame (`variants observed: none`), so no reply was ever in flight to flip the gate.
The gate poller (5s cadence) returned **33× GATED, 0× OPEN** while the camera stayed reachable
the entire time. This is the network-level confirmation of the campaign's `error:3004
"no user to push"` finding: **the camera's cloud binding is dead — it is not dialing the
cloud at all, so there is no check-in channel to forge on.**

## 1. The tooling built for this experiment

### 1.1 Rotate reply mode (`scripts/eseecloud-ws-server.py`)

New `rotate` reply mode added alongside the existing `replay` engine. Each registration is
answered with the **next** variant in a fixed cycle, so one long run measures every candidate
reply shape against the gate:

| Variant   | Reply shape | Bytes | Rationale |
|-----------|-------------|-------|-----------|
| `cadence` | byte-accurate real grant: `abbccdde 12`, 0x44 marker at `[28]`, next-counter (camera's own cadence, e.g. +0x15A0 for pconv `0x02d99e0f`) at `[32:36]` | 100 | The known-adopted shape (verified in real-cloud captures) |
| `plus1`   | same layout, next-counter = counter+1 | 100 | Legacy counter+1 — never adopted under MITM |
| `badoffset` | the run-3 2-byte misplacement: 0x44 at `[26]`, next at `[30:34]` | 100 | Proven silently rejected by the camera |
| `magic`   | `d9ffcc` hello magic + pconv + zeros ("server acknowledges device") | 24 | Minimal acknowledgement hypothesis |
| `echo`    | the registration mirrored verbatim | 128 | Passive-acceptance hypothesis |
| `empty`   | no grant payload | 0 | Camera retry-forever state (observed 061742Z) |

`GRANT_VARIANTS` is defined at `eseecloud-ws-server.py:101`; the pick-and-advance logic lives
in `CheckinReplay._build_grant`.

### 1.2 Hour-long orchestrator (`scripts/gate-flip-experiment.sh`)

Launches the MITM in rotate mode against one camera while a **parallel gate poller** curls
`http://<cam>/user/user_list.xml` every 5s (env `GATE_INTERVAL`), classifying each poll
`GATED` / `OPEN` / `NO-RESP` into a timeline. On completion it prints a verdict: **GATE
FLIPPED** (with the reply hex in flight *before* the flip timestamp, matched by ISO timestamp
so an end-of-run reply can't be misattributed) or **NO GATE FLIP** (with GATED/NO-RESP counts
and the variant distribution actually observed). NO-RESP dominance triggers an inconclusive
warning instead of a false negative.

Output: `captures/gate-flip-<ts>/gate-timeline.log`, `experiment.log`, plus the underlying
`captures/eseecloud-mitm-<ts>/` session (connections log with every reply hex, pcap, syn-watch).

### 1.3 The reviewer-caught rotate-index fix

The first implementation kept the variant index on the **per-connection** `CheckinReplay`
object. Code review caught that the camera re-dials on a **fresh TCP connection per check-in**
(~20s cadence), so the cycle would have restarted at `cadence` on every re-dial and the hour
would have measured only one variant. Fixed by hoisting the cycle to server scope:

- `WsCaptureServer` owns `self.rotate_holder = [0]` (`eseecloud-ws-server.py:421`)
- each per-connection `CheckinReplay` seeds `_variant_index` from `rotate_holder[0]` and
  writes the advance back (`:156-157`, `:238-239`, wired at `:667-668`)
- adoption-map recording gated to real 100-byte `0x12` grants so `magic`/`echo`/`empty`
  can't pollute the verdict map

Offline validation against all **51 real registration→grant pairs** (`eseecloud-real-grants.json`):
**0 errors** — exact cycle `cadence→plus1→badoffset→magic→echo→empty` across simulated fresh
connections, all six variants seen, correct per-variant byte shapes, correct counter math
(`cadence` next = counter + 0x13A0 with the default cadence, counter+1 for `plus1`,
counter+0x13A0 at `[30:34]` for `badoffset`).

### 1.4 `--no-early-abort` (added after the void run)

The void run was cut short at 150s by the mitm script's fail-fast guard ("Zero connections/DNS
after 150s"). A follow-up added `--no-early-abort` (env `NO_EARLY_ABORT=1`) to
`eseecloud-mitm-capture.sh`, which gates exactly the two mid-capture "nothing is connecting
yet" aborts (CERTREJECT and the 150s zero-everything guard) so a run can ride out the full
requested window even when the camera's check-in timer exceeds 150s. The L2 pre-flight,
server-startup, root, and config checks intentionally remain. `gate-flip-experiment.sh` now
passes the flag, so a re-run measures the full hour regardless of how long the camera stays
silent.

## 2. The live run (session `20260809T091421Z`)

```
MITM start        2026-08-09T09:14:24Z   camera 10.0.0.169   our IP 10.0.0.149 (wlp5s0)
cloud IPs to redirect  129.153.101.14 172.235.43.92 47.79.67.71
L2                  camera reachable (MAC 9c:a3:a9:bc:6f:ec); ARP spoof active
```

The capture loop logged `connections=0 data_chunks=0 dns_redirects=0` on **every** 5s tick.
The only camera traffic observed was SYN to its **own LAN ports** (`10.0.0.169:34567`,
`:554`, `:80`, `:8899`, `:8888`), all correctly skipped as LAN/private by the auto-heal —
there were **no SYN to external destinations at all** (syn-watch: zero non-LAN dests;
dns-queries.log: zero `REDIRECT` lines; eseecloud-connections.log: server header only, no
`CONNECT`, no `DATA`, no `REGISTER`). At 150s the zero-everything guard aborted the capture
(window had not produced a single frame).

The **gate poller ran independently of the MITM** (it only curls the camera) and produced a
clean, unambiguous timeline across its whole window — which tracks the MITM lifetime, so it
covered ~2.7 minutes (09:14:21Z → 09:17:04Z) before the 150s abort ended the run (the
poller is killed when the MITM returns; it is not the full hour).

```
gate-timeline.log (33 polls, 09:14:21Z → 09:17:04Z)
  33 GATED     0 OPEN     0 NO-RESP
```

The camera was reachable and answered `/user/user_list.xml` with `check in falied` on every
poll — the gate stayed closed the entire time, and the camera was never offline.

### 2.1 The verdict

```
VERDICT: NO GATE FLIP — 33 GATED polls
  every reply variant (cadence/plus1/badoffset/magic/echo/empty) cycled
  variants observed: none
```

`variants observed: none` is the operative phrase. This is a **void measurement, not a
negative one**: we did not prove the replies can't flip the gate — we proved the camera never
sent a registration for any reply to answer. With the abort guard now bypassable
(`--no-early-abort`), a re-run can cover the full hour of silence, but the same mechanism that
voided this run (no outbound traffic) is itself the strongest evidence yet that the cloud
plane is dead on this unit.

## 3. Network-level confirmation of the dead cloud binding

The run corroborates, at the packet level, everything the campaign established at the
protocol level:

1. **The camera emits no cloud check-in traffic.** Zero SYNs to the redirected cloud IPs
   (`129.153.101.14` / `172.235.43.92` / `47.79.67.71`), zero DNS queries, zero WS frames —
   over the entire window, with the camera otherwise healthy (answering HTTP on :80, ARP
   reachable, MAC active).
2. **The `/user` gate is real and persistent.** 33 consecutive `check in falied` responses
   across 3 minutes of polling. The gate is not flapping and not tied to our MITM state — it
   is the camera's steady state.
3. **Consistent with `error:3004 "no user to push"`.** The cloud rejected the check-in at the
   *account* level. If the binding lives in the EseeCloud account rather than camera NVRAM,
   the camera having nothing to check in *with* explains exactly why it isn't trying: from the
   camera's perspective there is no valid cloud session to maintain.

## 4. What this means for the unlock campaign

- **The cloud plane is closed at the transport level**, not just the application level. There
  is no check-in to replay, forge, or answer — so no fake-cloud reply (byte-accurate grant or
  otherwise) can flip the gate on a unit that isn't dialing.
- **The one remaining software vector that can force a check-in attempt is a reboot** (or
  factory default). We hold ONVIF `admin:admin` on `.169`, so a `tds:Reboot` SOAP call
  (drafted in `2026-08-09-factory-default-unlock-draft.md` §2.7) followed immediately by
  `gate-flip-experiment.sh --no-early-abort` would give the first real measurement of all six
  reply variants against a *freshly booted* camera that is actively attempting its cloud
  session. If the reboot still produces zero outbound traffic, the dead binding is confirmed
  as boot-time firmware behavior, not a runtime condition.
- **Artifacts kept for the re-run:** `captures/gate-flip-20260809T091421Z/` (timeline +
  experiment log) and `captures/eseecloud-mitm-20260809T091421Z/` (connections log, pcap,
  syn-watch) — the pcap's 3.2 MB of silence is the baseline the reboot-kick run will be
  compared against.

## 5. Reboot-kick attempt — `tds:Reboot` NOT IMPLEMENTED (2026-08-09)

Attempted to force a fresh cloud check-in by rebooting `.169` over the ONVIF plane (we
hold `admin:admin`), then immediately launching the rotate-mode experiment against the
freshly booted camera. The ONVIF reboot failed with a definitive firmware finding:

1. **Read plane still live:** `GetDeviceInformation` via Basic `admin:admin` on
   `:8888/onvif/device_service` → **HTTP 200** (serial `Z7C34781620744` — matches the
   pconv-derived serial from the extraction inventory).
2. **Reboot is Digest-gated:** `tds:Reboot` via Basic → **HTTP 400 +
   `WWW-Authenticate: Digest realm="happytimesoft", qop="auth"`** — the hsoap/2.8 stack
   requires RFC 7616 Digest for privileged actions.
3. **Action genuinely absent:** `tds:Reboot` with a correct RFC 7616 Digest response
   (MD5 HA1/HA2, qop=auth, fresh nonce) → **HTTP 400 SOAP Fault `s:Receiver /
   ter:ActionNotSupported` "Action Not Implemented"**. Auth PASSED (the fault is not
   `NotAuthorized`); the action simply does not exist in this firmware.
4. **Camera never rebooted:** ports `80/8888/554` stayed OPEN through the attempt and the
   gate returned `check in falied` on every subsequent poll — no dark window, no reboot.

**Conclusion:** the 5523-W's ONVIF surface implements read/media actions only — the device
management actions (`tds:Reboot`, and likely `tds:SetSystemFactoryDefault`, consistent with
the SetUser precedent of a thin, partly-inert management layer) are not implemented. No
reboot CGI exists in the firmware strings either (`/sbin/reboot` is the kernel binary, not
an endpoint). **A reboot cannot be forced over ONVIF.**

Consequence for the experiment: the reboot-kick catalyst is unavailable, so the full-hour
rotate run launched in its place measures the camera's NATURAL behavior — whether ANY cloud
check-in occurs during a full hour of observation. With `--no-early-abort` this rules out a
check-in timer longer than 150s (the prior run's gap). A zero-connection hour is the
strongest runtime confirmation of the dead binding; any registration that does land still
gets all six reply variants measured against the `/user` gate.

## 6. pcap comparison — silent baseline (091421Z) vs reboot-kick run (092927Z)

**Framing:** the `092927Z` capture is **not** a post-boot observation — `tds:Reboot`
returned `ter:ActionNotSupported` (§5), the camera never went dark, and it stayed up the
entire window. The comparison therefore measures the same steady-state camera over a longer
window, which is itself the finding. Both pcaps are filtered `host 10.0.0.169` on `-i any`
(`eseecloud-mitm-capture.sh:405`, filter built from `$CAMS`). All counts below are tcpdump
derivations from the raw pcaps.

### 6.1 Counts

| Metric | 091421Z (baseline) | 092927Z (reboot-kick) |
|--------|--------------------|----------------------|
| Capture window | 151.7s (02:14:33 → 02:17:05) | 959.4s (02:29:40 → 02:45:39) |
| pcap size | 3.2 MB | 19.4 MB |
| **Total packets** | 38,946 | 234,782 |
| **Outbound SYN from .169 (camera-initiated TCP)** | **0** | **0** |
| **DNS queries (any direction)** | **0** | **0** |
| **WS frames (camera → fake cloud server)** | **0** | **0** |
| non-LAN destinations | 0 | 2 — both camera **UDP broadcast beacons** (`255.255.255.255:8002` + `:18002`, 760 B each, 02:35:30) |
| Camera-originated UDP broadcasts | 0 | 2 (the beacons above) |
| Camera-originated TCP (responses to our polls) | 267 | 1,633 |
| Our SYN probes (.149 → camera) | 56 | 325 |
| Full TCP handshakes involving .169 | 54 | 316 |
| Loopback packets in pcap | 0 | 0 (filter excludes `127.0.0.1`) |

### 6.2 Per-minute rates — the volume difference is duration, not behavior

- 091421Z: 38,946 packets / 151.7s ≈ **257 pkt/s** (~15.4k pkt/min)
- 092927Z: 234,782 packets / 959.4s ≈ **245 pkt/s** (~14.7k pkt/min)

The near-identical rates (257 vs 245 pkt/s) prove the size difference (3.2 MB vs 19.4 MB) is
pure observation-window length. The bulk of both captures is **our own probing** — the 5s gate
poll (a full HTTP exchange per poll), the periodic SYN-scan of `:80/:554/:8899/:34567`, and
connectivity probes — with the camera answering each with SYN-ACK/HTTP/RST and nothing more.

### 6.3 The only camera-originated egress: 2 UDP discovery beacons

The only non-LAN packets in either capture are two 760-byte UDP broadcasts from the camera
at 02:35:30 in the longer window (`10.0.0.169:8002 > 255.255.255.255:8002` and
`10.0.0.169:18002 > 255.255.255.255:37892`). These are LAN device-discovery beacons — local
broadcasts, not cloud dialing. They appear only in the longer window, suggesting a ~5–10 min
periodic cadence (or an ARP-spoof-flap trigger; bettercap logged IPv6 gateway flaps at
02:32/02:33/02:36, the last ~1 min before the beacons).

### 6.4 Loopback-artifact exclusion

`092927Z`'s `eseecloud-connections.log` shows **4 `CONNECT` events** (the naive grep for
`CONNECT ` returns 8 because `DISCONNECT` lines also match) — all `127.0.0.1 → :80`
with the identical 127-byte `GET /NetSDK/System/deviceInfo` (curl/8.5.0, `Basic YWRtaW46`)
probe. These are **our own host's** NetSDK probes landing on the fake server's `:80` listener
(the fake server binds `:80` too) — verified not camera traffic three ways: 0 of 4 CONNECTs
originate from `10.0.0.169`, 0 loopback packets appear in the pcap (filter is `host
10.0.0.169`), and the payload is a curl User-Agent, not the camera's registration shape.
They do usefully prove the fake server's `:80` listener was live and answering throughout.
Both `server.log` and `ws-server.log` are **empty (0 lines)** — no camera frame, registration,
or WS message ever reached the fake cloud server.

### 6.5 Verdict (definitive — harvested 2026-08-09, session `20260809T092927Z`)

```
VERDICT: NO GATE FLIP — 200 GATED / 7 NO-RESP / 0 OPEN  (207 polls)
  variants observed: none — the fake server received ZERO camera connections
```

**The camera never dialed the cloud.** Across the full 959.4s capture: zero outbound SYN,
zero DNS queries, zero WS frames, zero camera-initiated TCP. The fake server's
`server.log` and `ws-server.log` are both empty (0 lines) — the only server contacts were
the 4 host-loopback NetSDK curls (§6.4). No reply variant was ever in flight
(`variants observed: none`), so **no server reply can flip the `/user` gate on a unit that
isn't dialing** — the dead-cloud-binding conclusion moves from protocol-level inference (§3)
to **runtime observation**.

**Honest window caveat.** The run was **SIGKILLed at 09:47:01Z (`mitm rc=137`), ~17.6 min
into its 3600s budget**, when the surrounding session was torn down — the orchestrator's
`over 3600s` label is the configured budget, not the elapsed window. The measurement that
matters is the gate timeline itself: **207 polls, 09:29:27Z → 09:46:58Z, 200 GATED / 0
OPEN**, and the camera answered `check in falied` on every reachable poll — the gate never
opened even once.

**The 7 NO-RESP polls are a capture artifact, not a gate change.** They form one 30s
cluster (09:42:26Z → 09:42:56Z) that sits exactly inside a bettercap **gateway-flap
window** (IPv6 flap 09:42:07Z, IPv4 flap 09:42:57Z logged in `bettercap.log`) — most
likely the gate poller's curls to the camera timed out during the ARP-spoof confusion, then
returned GATED immediately after (09:43:05Z). 0 OPEN stands across all 207 polls; the camera was never
offline.

**Post-run corroboration — the heartbeat has since added hours of runtime observation.**
The hourly heartbeat monitor (`captures/gate-flip-heartbeat.log`) has run 16 more 10-min
rotate sessions through 02:11Z (Aug 10, as of this harvest) — **every one `NO GATE FLIP — 124–125 GATED /
0 OPEN / variants observed: none`**. Combined with this run's ~17.6 min, the dead cloud
binding now has **~3.0 hours of cumulative runtime observation (16 × 10 min + 17.6 min)
with zero gate opens**.

Harvest (reproducible):

```bash
cd /home/cody/Documents/BossCamSuite-main
D=captures/gate-flip-20260809T092927Z
wc -l < "$D/gate-timeline.log"         # 207
awk '{print $2}' "$D/gate-timeline.log" | sort | uniq -c   # 200 GATED / 7 NO-RESP
# verdict row:
tail -3 "$D/experiment.log"            # VERDICT: NO GATE FLIP — 200 GATED polls
```

## Appendix — files touched

| File | Change |
|------|--------|
| `scripts/eseecloud-ws-server.py` | `rotate` reply mode, `GRANT_VARIANTS`, server-scoped `rotate_holder`, adoption-map gating |
| `scripts/gate-flip-experiment.sh` | hour-long orchestrator: rotate-mode MITM + parallel gate poller + timestamp-correlated verdict; passes `--no-early-abort` |
| `scripts/eseecloud-mitm-capture.sh` | `--no-early-abort` / `NO_EARLY_ABORT=1`: gates CERTREJECT + 150s zero-connections aborts |
