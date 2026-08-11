# .29 Post-Re-Provision Network Drop — Forensics (5523-W)

**Date:** 2026-08-10 · **Subject:** 10.0.0.29 (eseeid `4780038910`, serial `Z7C34780038910`, MAC `9c:a3:a9:be:0a:89`) dropped off the network ~10 minutes after a clean WiFi re-provision on 2026-08-09.
**Verdict: the failure point is the camera's WiFi radio (re-init/lockup), NOT the config write.** The written config was correct, persisted through the outage, and the camera self-recovered with it intact.

---

## 1. Question

After `scripts/5523w-wifi-reprovision.sh` re-provisioned .29 (join AP → write station-mode config → camera rejoined Aegon at 10.0.0.29 with blank admin, HTTP 200), the camera vanished ~10 minutes later — no LAN answer, no AP broadcast. Was the drop caused by (a) the config write (bad payload / applied state) or (b) the camera's WiFi radio failing on its own?

## 2. Timeline (2026-08-09, UTC; bettercap logs are local = UTC−7)

| Time (Z) | Event | Source |
|---|---|---|
| ~09:30–09:36 | Re-provision completes; .29 answers blank-admin `deviceInfo` HTTP 200 at 10.0.0.29 | direct curl (earlier session) |
| 09:29:30 | Session `20260809T092927Z` starts (.169 soak; spoofs .169 only) | connections/dns logs |
| 09:36:57–09:41:44 | Script self-polls `127.0.0.1:80/NetSDK/System/deviceInfo` (curl/8.5.0, `Host: 127.0.0.1`) — laptop-side health checks, not camera traffic | `eseecloud-connections.log`, `eseecloud-data.bin` |
| 09:42:27–09:42:52 | bettercap: `error getting ipv4 gateway: Could not find mac for` ×6 — **L2/ARP gateway-resolution disruption begins** | `bettercap.log` |
| **09:42:57** | bettercap: `IPv4 gateway changed: '' () -> '172.14.10.1' (9c:a3:a9:be:0a:89)` — **.29's own MAC surfaces as a gateway candidate for ~5 s** | `bettercap.log` |
| 09:43:02 | bettercap: gateway reverted to `10.0.0.1` (router `c4:50:9c:de:ee:b7`); .29 never seen again in the window | `bettercap.log` |
| 09:47:17 | Session `20260809T094714Z` starts and **aborts at the L2 ARP guard** (no `capture.pcap`, no spoof ever started) — .29 already unreachable at L2 | session dir listing |
| 10:01:05 | Session `20260809T100102Z` starts; still no .29 anywhere | session logs |
| later | .29 **self-recovers** at 10.0.0.29 with the **identical config** (stationMode / Aegon / blank admin 200) | live curl, 2026-08-10 |

**Drop:** the camera's last L2 appearance is the 09:42:57Z gateway-change (its MAC in bettercap's gateway tracking); confirmed gone by 09:47 (aborted guard) — ~7–13 minutes after the re-provision completed (tighter than the earlier ~09:46 estimate). The 09:42:27–09:42:52 gateway-resolution errors are host-side bettercap noise that preceded it, not camera evidence.

## 3. Evidence per source

### 3.1 LAN / AP visibility
- Healthy through the post-repro window (blank-admin 200 at 10.0.0.29), then absent from ARP/neigh and HTTP from ~09:43Z onward.
- Its AP (`IPCZ7C34780038910`) also stopped broadcasting while the other camera's AP (`IPCZ7C34781634738`) remained visible — the radio went fully dark, not merely off-LAN.

### 3.2 DHCP lease — **ruled out**
- .29's `lan` block, read 2026-08-10 after the drop + self-recovery and before any probing: `{addressingType: static, dhcp: true, staticIP: 10.0.0.29, staticNetmask: 255.255.255.0, staticGateway: 10.0.0.1}`. It was static at drop time by inference: the re-provision wrote `wirelessStationDhcp: true`, which per the inverted-semantics finding (see §5.3b payload-truth appendix) = **static addressing**, and the camera recovered with the same staticIP — the post-recovery read is consistent with the write.
- A static address cannot expire; DHCP lease expiry is therefore not a possible cause. (No DHCP server runs on this host — only unrelated `lxcbr0`/`virbr1` :67 listeners — and the router at 10.0.0.1 owns leases, but the static config makes the lease question moot.)

### 3.3 Radio association / L2 — the drop signature
- bettercap's gateway tracking is the closest available proxy for radio association, and it shows a **35-second L2/gateway disruption (09:42:27–09:43:02Z)**:
  1. `error getting ipv4 gateway: Could not find mac for` ×6 (09:42:27–09:42:52) — the host briefly lost gateway resolution;
  2. at 09:42:57, .29's MAC claimed a gateway role at `172.14.10.1` for ~5 s;
  3. at 09:43:02 the real router was re-found; .29 was silent thereafter.
- Interpretation: the camera's radio re-initialized (briefly serving a self-hosted/gateway role — anyka radio re-init behavior), failed to complete the station join this time (radio lockup), and went dark. Consistent with the previously-documented 5523-W WiFi flakiness.

### 3.4 Media-info captures — consistent, and the window proves a different point
- .29's media-info frames across ALL sessions carry `wirelessStationDhcp=true` (static) — consistent with the static lan config above.
- **No .29 media-info exists in the drop window by construction**: the three window pcaps (091421Z / 092927Z / 100102Z) contain **0 byte-occurrences** of .29's MAC `9c:a3:a9:be:0a:89` and ~233k of .169's MAC — those runs ARP-spoofed **only .169**, so .29 traffic never transited the capture host. Absence of .29 in the pcaps is expected and is NOT evidence of silence; the bettercap log is the authoritative L2 witness.

### 3.5 Config write — **exonerated**
1. The write produced a correct, healthy camera at the right IP with blank admin (HTTP 200) — the payload worked.
2. The **identical config survived the outage**: .29 came back as stationMode/Aegon with blank admin 200 and the same staticIP. A failed/bad config write would leave the camera misconfigured after its radio came back, not perfectly re-provisioned.
3. The aborted 094714Z session started at 09:47, **after** the drop, and never spoofed (no pcap) — the MITM was not the cause either.

## 4. Verdict

**Camera-side WiFi radio failure ~7–13 minutes after re-provision; the config write is not implicated.** The robust facts: .29's MAC last appeared at L2 at 09:42:57Z (bettercap gateway tracking) and the camera was gone by 09:47, fully dark (no LAN, no AP), before self-recovering later with the same config. Interpretation (hedged): the radio re-initialized and failed to rejoin — the exact mechanism is uncertain because the claimed gateway `172.14.10.1` is not among the camera's known AP gateway candidates (192.168.1.1/192.168.0.1/10.10.10.1/192.168.2.1/172.16.0.1) and is outside RFC1918, so it could be a radio churn transient or a bettercap misdetection of one; either way it points at the camera's radio, not the config. This matches the 5523-W's known WiFi flakiness.

## 5. Recommendations for future re-provision runs

- Treat a post-re-provision camera as **provisionally attached**: after STEP 6 confirms blank-admin 200, keep a **radio-health poll** (HTTP or ARP every 30 s for ≥15 min) and flag a drop as radio, not config — do not re-run the re-provision on a live healthy-looking camera.
- **Power-cycle once after re-provision** and confirm the camera rejoins without re-provisioning: proves the radio boots into the persisted config (the strongest config-write sanity check).
- The `write_lan_addressing` verify-GET added on 2026-08-10 already detects a normalized lan write; a drop in the first ~15 min after a re-provision should be triaged as radio-first per this report.

## 6. Note for the record

Separately (later on 2026-08-10, after .29 had already self-recovered), the `wirelessStationDhcp` live probe flipped .29's `lan` to `dynamic`/`OnvifAutoAdapt:true` (the §5.3b payload-truth investigation). That is a distinct, later state change — not the cause of this drop, and the camera remained reachable throughout it.
