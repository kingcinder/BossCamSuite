# 2026-08-10 Beacon cadence vs gateway flaps — fixed timer, not ARP-spoof-triggered (and the camera WAS dialing)

## Question

The camera emits 760-byte UDP discovery broadcasts to `255.255.255.255` (source ports
`:8002` and `:18002`, dst ports vary: 8002/33748/37892/35945/...). Are those beacons
**triggered by ARP spoofing** (bettercap gateway flaps), or do they run on a **fixed
timer** independent of our MITM?

## Tooling

### `scripts/beacon-listener.sh` — validated and ready to run

Watches one camera's broadcasts with `tcpdump -i any -nn -tt -l 'src host <cam> and
udp and dst host 255.255.255.255'` (epoch-microsecond timestamps), logs every emission,
optionally tails a live MITM session's `bettercap.log` for `gateway.change` flaps
(freshness guard: only if the log was written in the last 300s), then runs an embedded
python correlator that:

- groups the `:8002`+`:18002` pair (fires ~8ms apart) into one **emission**
- measures inter-emission intervals → `FIXED-TIMER` if max spread < 15% of median
- pairs each emission with the nearest **PRIOR** flap (≤3s) → `FLAP-TRIGGERED` if all
  emissions have a prior flap and intervals are irregular
- single-instance `flock` guard; `MITM_DIR=none` to skip flap tracking entirely

Status: **bash -n clean, embedded python py_compile clean, synthetic dry run yields the
correct FIXED-TIMER verdict on 900s-interval input.** Execute bit set. Launch is queued
behind root approval (`sudo ./scripts/beacon-listener.sh 10.0.0.169 2700 none`).

### Historical pcap mine — every emission ever captured (UTC)

Mined from all `captures/eseecloud-mitm-20*/capture.pcap` (`udp and src host 10.0.0.169
and dst host 255.255.255.255`):

```
Aug 8  05:32:36  (18002→33748)              ← the single off-grid outlier
Aug 8  22:50:30  (8002→8002)                ← 19s after session 225011Z start, ~3 min
Aug 8  23:35:30  (8002→8002 + 18002→35945)     BEFORE the first gateway flap (22:53:30Z)
Aug 9  09:35:30  (8002 + 18002→37892)       ← in the 3600s reboot-kick run
Aug 9  10:05:29  (8002 + 18002→49257)
Aug 9  11:07:37  12:07:40  13:07:40  14:07:39  15:07:39  ← heartbeat era: all :07:39-40
Aug 9  16:07, 17:07, 18:07  ← ABSENT (3-session gap)
Aug 9  19:07:39  20:07:39  21:07:40  22:07:39  23:07:40
Aug 10 00:07:40  01:07:40  02:07:39
```

## Findings

### 1. Fixed wall-clock timer, not session-start-triggered (heartbeat era)

13 heartbeat-era beacons are pinned at **`:07:39-40` UTC with ≤3s jitter** while session
starts vary by ~96s (`RandomizedDelaySec=120`). The delay-after-session-start varies
**inversely** (336–432s) to keep the wall-clock pinned:

| Session start | Beacon | Delay |
|---|---|---|
| 11:00:32 | 11:07:37 | 425s |
| 12:01:47 | 12:07:40 | 353s |
| 13:01:52 | 13:07:40 | 348s |
| 15:02:03 | 15:07:39 | 336s |
| 20:00:27 | 20:07:39 | 432s |

A session-start trigger would produce a fixed delay and drift with the starts; instead
the delay compensates to hold the wall-clock. **This is a fixed timer.**

**Phase caveat (reviewer correction):** the *early-era* beacons (22:50:30, 23:35:30,
09:35:30, 10:05:29) sit on the `:05/:20/:35/:50` grid at ~`:30s` — a ~2-minute different
phase from the heartbeat era's `:07:39-40`. Either the early "grid" was a sparse-sampling
artifact or the timer's phase reset between eras. We assert a fixed timer, not one
universal phase.

### 2. Not flap-triggered

- Session `225011Z`: beacon fired **19s after MITM start and ~3 min BEFORE the first
  gateway flap** (bettercap logged the first flap at 22:53:30Z).
- Session `232711Z`: **0 gateway flaps** in the whole session, yet a beacon fired.
- Session `200027Z`: a beacon fired at 20:07:39 with **zero camera connections** that hour.

The "19s after start" beacon is consistent with the fixed timer — that session happened
to start just before a grid slot. Beacons do not require a preceding flap.

### 3. The 16:07–18:07 gap is real but not explained by "no dialing" (reviewer correction)

Three consecutive sessions (16:01:06, 17:01:38, 18:00:31) captured **zero beacons**, and
their heartbeat verdicts said `variants observed: none`. The tempting inference — "no
beacon because the camera wasn't dialing" — **does not hold**:

- Sessions 16/17/18:00 each had **14–28 camera connections** (`10.0.0.169`) but **0
  REGISTERs** parsed.
- Session `190126Z` (1 emission = 2 packets) also had **0 REGISTERs** (12 connections).
- Session `210032Z` (1 emission = 2 packets) had **42 camera connections, 0 REGISTERs**.
- Session `200027Z` (1 emission = 2 packets) had **0 connections at all**.
- Session `110032Z`: the gate poller got **104 NO-RESP** (camera did not answer gate polls
  at all that hour) yet a beacon still fired at 11:07:37 — beacon independent of both
  dialing and gate responsiveness.

So beacon-presence and REGISTER/dialing state are **not cleanly correlated**; the 3-hour
gap remains unexplained (timer skip? phase drift? firmware state?). This is exactly what
the live no-spoof run is meant to arbitrate.

### 4. ⚠️ CAMPAIGN-CHANGING: the camera WAS dialing the fake cloud server

During heartbeat sessions **12:01–15:02** the connections logs show **123 camera
connections per session from `10.0.0.169`** with real registrations:

```
CONNECT 10.0.0.169:56964 -> :80
DATA POST /address/device?sn=JAZ7C34781620744&max_ch=1 HTTP/1.1
CONNECT 10.0.0.169:53838 -> :19000
DATA d9ffcc028c38eed2d199ac6026947fae0f9ed902   (magic hello)
DATA abbccdde...                                 (grant)
REGISTER LITE 0x00 32B counter=... pconv=02d99e0f
```

**31 REGISTERs parsed per session** (12:01/13:01/14:01), ~20s cadence — and the heartbeat
verdicts for those hours show **all six reply variants were actually served to real
camera registrations** (15–17 of each: badoffset/cadence/echo/empty/magic/plus) with
`124 GATED / 0 OPEN`.

**Implication: the gate-flip experiment's central question has a real answer — even a
camera that IS dialing and receiving every reply variant (including the byte-accurate
`cadence` grant) does NOT open the `/user` gate.** The earlier "void measurement"
conclusion (zero connections over the 3600s run) was real for that window, but the
heartbeat sessions prove the camera *can* dial — and the gate stays closed anyway. This
upgrades the dead-cloud-binding evidence from "camera not dialing" to "**gate is not
server-reply-flippable even when dialing**."

## Verdict

| Hypothesis | Result |
|---|---|
| ARP-spoof / gateway-flap triggered | **Rejected** — beacon fired before flaps, and in flap-free sessions |
| Fixed timer (heartbeat era) | **Confirmed** — 13 beacons pinned :07:39-40 ±3s despite 96s-start drift |
| Beacon ⇔ camera dialing | **Not cleanly correlated** — beacon with 0 conns (20:00), no beacon with 14–28 conns (16–18:00) |
| Server reply can flip the gate | **Rejected even when dialing** — 12:01–15:02 served all six variants to real registrations, 0 OPEN |

## Next step (queued — with a timing correction)

Launch the live no-spoof run to measure the *current* phase and confirm the timer with
zero ARP spoofing active. Two constraints, per review:

1. **Avoid the hourly heartbeat MITM window.** `bosscam-gate-flip-heartbeat.timer`
   fires at `:00` (with `RandomizedDelaySec=120`) and each gate-flip session runs a real
   MITM (ARP spoof + fake server) for ~600s — so **~`:00–:12` each hour is spoofed**.
   A no-spoof run must sit inside the `:12–:58` band.
2. **Expect the grid slots** `:20`/`:35`/`:50` (the early-era `:05/:20/:35/:50` grid
   with ~`:30s` phase) — NOT the heartbeat-era `:07:39` slot, which lands inside the
   spoofed window and would be contaminated.

Cleanest window: launch at **~:15** for **~40 min** (`2400s`), catching `:20`/`:35`/`:50`
with zero spoofing and ending before the next hour's heartbeat:

```bash
sudo ./scripts/beacon-listener.sh 10.0.0.169 2400 none
```

(Launching mid-`:00–:12` spoofed band, or running past the next `:00`, invalidates the
no-spoof claim for the affected slots.) Whichever slots fire — and how regularly —
settles the current phase and the 3-hour-gap question.
