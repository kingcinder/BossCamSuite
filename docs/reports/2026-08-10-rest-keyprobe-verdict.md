# REST-Plane Key Truth — Live Verdict (5523-W interface/4 wireless key)

**Date:** 2026-08-10 · **Status:** **SETTLED LIVE — keyprobe's original question closed.** The REST plane's canonical mode key is **`wirelessMode`**; the wire-plane key **`wireless` is rejected** by the REST PUT in both body shapes. Both `wirelessMode` body forms (nested `stationMode` + flat siblings) round-trip on GET. Evidence: live probe of **10.0.0.29** (eseeid `4780038910`, blank admin) at 11:22Z and 11:53Z, recorded in the campaign ledger `local-camera-recovery/ledger/JAZ7C34780038910.jsonl`. The same §5.8 chain was also run live against **10.0.0.169** (12:00Z): the delegation was validated end-to-end, but that unit answers **HTTP 401** to blank admin, so the key truth is not obtainable under PLAN=B on .169 (per-camera ledger record written; see §2.4).

**Cross-linked as the authority from the protocol report's §5.3b appendix** (KEY-SPELLING HALF: RESOLVED LIVE block in [`2026-08-09-controlled-verify-experiment-protocol.md`](2026-08-09-controlled-verify-experiment-protocol.md)) — the two documents now reference each other bidirectionally.

---

## 1. The question this closes

`5523w-interface4-keyprobe.sh` existed to settle one ambiguity: the camera's **wire-plane** media-info frames serialize the station config under the key `wireless` (`wireless=stationMode..wirelessApEssId=…`), while the vendor NetSDK REST contract (`endpoint_catalog.json` Appendix I) documents the **REST-plane** key as `wirelessMode`. Which key does the REST GET return, and does a PUT using the wire-plane spelling transfer?

**Answer (live, 2026-08-10):**

| Question | Answer |
|---|---|
| REST GET `/NetSDK/Network/interface/4/wireless` mode key | **`wirelessMode`** |
| PUT `wirelessMode` + nested `stationMode` | **accepted, round-trips** |
| PUT `wirelessMode` + flat siblings | **accepted, round-trips** |
| PUT `wireless` (nested or flat) | **rejected** (HTTP/statusCode ≠ 0) |

The wire-plane key name does **not** transfer to the REST plane. The re-provision tool's station-mode write (`wirelessMode` + nested `stationMode`) is confirmed correct on live firmware.

---

## 2. Evidence — live runs

### 2.1 Run A: direct `--keyprobe-only` (11:22Z, 4-variant probe)

```
═══ KEY VERDICT 10.0.0.29 ═══
  GET  mode key      : wirelessMode
  PUT accepted+ok    : wirelessMode(1)=round-trips wirelessMode(0)=round-trips
  PUT rejected       : wireless(1) wireless(0)
  VERDICT            : REST plane accepts only:wirelessMode(1)=round-trips — other spellings rejected
```

`wirelessMode` accepted and round-tripped in **both** nested (`(1)`) and flat (`(0)`) form; `wireless` rejected in both forms. This is the run that settled the question and motivated trimming the probe from 4 PUT variants to 2 (`wirelessMode` only — see §4).

### 2.2 Run B: controlled-verify §5.8 delegation (11:53Z, trimmed 2-variant probe)

The §5.8 delegation chain (controlled-verify `keyprobe_truth_check` → `5523w-wifi-reprovision.sh --keyprobe-only` → keyprobe subprocess, PLAN=B blank admin) reproduced the truth end-to-end:

```
  GET  mode key      : wirelessMode
  PUT accepted+ok    : wirelessMode(1)=round-trips wirelessMode(0)=round-trips
  PUT rejected       : none
  VERDICT            : all wirelessMode PUT forms accepted (nested + flat); GET canonical key = wirelessMode
```

### 2.4 Run C: §5.8 delegation vs 10.0.0.169 (12:00Z) — chain validated, unit non-blank-admin

The same §5.8 delegation driver (PLAN=B) was run against the second fleet unit. The chain **worked end-to-end**: `keyprobe_truth_check` → `5523w-wifi-reprovision.sh --keyprobe-only` → keyprobe subprocess → non-fatal no-verdict warning:

```
[12:00:30Z]   running REST key probe via: .../5523w-wifi-reprovision.sh --keyprobe-only 10.0.0.169 (admin:)
[12:00:38Z]   ⚠ no key probe verdict lines in wrapper output — REST key truth not recorded
  keyprobe_truth_check rc=0
```

Root cause (verified directly): **10.0.0.169 answers `HTTP 401` to blank admin** (`GET /NetSDK/System/deviceInfo`), ping OK. The keyprobe correctly refused to probe under auth failure, and the wrapper recorded a per-camera ledger line (`unknown.jsonl` — serial unknown because deviceInfo is unreadable under 401). **.169 is therefore a non-blank-admin unit** — the same one the controlled-verify protocol slated for the factory reset (which yields blank admin, after which this same chain will produce its key-truth line in `JAZ7C34781620744.jsonl`).

### 2.3 deviceInfo serial form (.29)

Direct `GET /NetSDK/System/deviceInfo` (blank admin):

```json
{ "serialNumber": "Z7C34780038910", "serial": null, "sn": null, "deviceSerial": null,
  "eseeID": "4780038910", "eseeId": null, "model": "5523-W" }
```

**Finding:** deviceInfo returns the **JA-less** serial (`Z7C34780038910`); the campaign's canonical serial is `JAZ7C34780038910` (AP SSID `IPCZ7C34780038910`, eseeid derivation). The ledger normalizes `Z7C* → JA+serial` so every run writes ONE `<serial>.jsonl` per camera.

---

## 3. Campaign ledger — evidence lines

`local-camera-recovery/ledger/JAZ7C34780038910.jsonl` (canonical file; the JA-less `Z7C34780038910.jsonl` was merged in and removed):

```json
{"ts":"2026-08-10T11:22:01Z","serial":"JAZ7C34780038910","ip":"10.0.0.29","keyprobe_verdict":"REST plane accepts only:wirelessMode(1)=round-trips \u2014 other spellings rejected","sta_dhcp":null,"source":"reprovision","status":"ok"}
{"ts":"2026-08-10T11:53:21Z","serial":"JAZ7C34780038910","ip":"10.0.0.29","keyprobe_verdict":"all wirelessMode PUT forms accepted (nested + flat); GET canonical key = wirelessMode","sta_dhcp":null,"source":"reprovision","status":"ok"}
```

Note `sta_dhcp: null` in both lines: the REST GET body does **not** echo `wirelessStationDhcp` into the stationMode block (it is a write-only wire-plane hint per §5.3b of [`2026-08-09-controlled-verify-experiment-protocol.md`](2026-08-09-controlled-verify-experiment-protocol.md)), so the keyprobe's read-back classify cannot observe it — expected, not a bug.

The .169 attempt (12:00Z) recorded a per-camera line under `unknown.jsonl` (serial unreadable because the camera rejected auth):

```json
{"ts":"2026-08-10T12:00:38Z","serial":"unknown","ip":"10.0.0.169","keyprobe_verdict":"n/a","sta_dhcp":null,"source":"reprovision","status":"ok"}
```

This is the designed non-fatal auth-failure path (verdict `n/a`, status `ok` because the delegation itself succeeded — the camera just refused the probe). Once .169 is factory-reset to blank admin, re-running the same chain writes its real verdict to `JAZ7C34781620744.jsonl`.

---

## 4. Tool changes this verdict drove

1. **`5523w-interface4-keyprobe.sh` trimmed 4 → 2 PUT variants** — the `wireless` spellings are dead; the probe now tests only `wirelessMode` nested + flat, removing half the PUT round-trips (wall time ~25s → ~21s on .29). VERDICT logic reworked for the 2-variant space (all-accepted / partial / all-rejected).
2. **Ledger serial normalization** — `fetch_serial()` in both `5523w-interface4-keyprobe.sh` and `5523w-wifi-reprovision.sh` maps the JA-less deviceInfo serialNumber (`Z7C34…`) to the canonical `JAZ7C34…` form; existing split ledger files merged.
3. **Single keyprobe code path** — controlled-verify §5.8 delegates to `5523w-wifi-reprovision.sh --keyprobe-only` (with `ADMIN_PASS=$KNOWN_PASS` for Plan A, blank for Plan B), so all three entry points (standalone, re-provision STEP 6, §5.8) share one implementation + one ledger line per run.

---

## 5. Implications

- The re-provision payload (`wirelessMode` + nested `stationMode`) is the accepted REST form — no further firmware probing needed on this axis.
- The wire-plane `wireless` key remains correct for interpreting MITM media-info frames; it simply cannot be written back through the REST PUT.
- The keyprobe's original question is **CLOSED**; the tool's remaining live role is regression/verification and fleet-wide key-truth recording in the ledger.
- **Fleet status:** .29 is a blank-admin unit with the key truth recorded; .169 rejects blank admin (HTTP 401) — consistent with it being the reset candidate. Both units have per-camera ledger records from the §5.8 delegation (real verdict / auth-failed respectively).
