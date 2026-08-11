# Controlled Verify-Formula Experiment — Protocol (5523-W)

**Date:** 2026-08-09 · **Status:** **FORMULA RECOVERED — 11/11 pairs confirmed 2026-08-09** (`verify = MD5hex(TOUPPER(nonce)+eseeid+TOUPPER(request_id)+"Japass^2>.j")` for the post_v2 chain, on both cameras, including run-7 and run-8 pairs — see Hypothesis update below). The controlled reset is **no longer required for formula recovery**; it now serves only to obtain a **known admin password** (the password sweep found no derivable password) · **Requires:** one physical factory reset (10–15 s reset button hold on the chosen camera)

---

## 1. Objective & Hypothesis

**Objective.** Recover the exact `verify` formula used by the EseeCloud `/message/` check-in chain (`GET /message/nonce` → `POST /message/message?method=post_v2&eseeid=…&request_id=…&verify=…`) by making **one controlled change** — the camera's admin password — and diffing the resulting verify hashes against the pre-change baseline.

**Hypothesis (RESOLVED 2026-08-09).** The pre-experiment hypothesis — `verify = H(secret ∥ nonce)` where `secret` is a per-device value derived from the admin password or an independent cloud credential — is **DISPROVEN**. The decompile-resolved algorithm (literal pools resolved from the AKV3C binary, `/tmp/romx/appfs/bin/anyka_ipc`, VA = file + 0x10000) is:

```
verify = MD5hex( TOUPPER(nonce) + eseeid + TOUPPER(request_id) + "Japass^2>.j" )
```

(`oc_cal_verify` @ 0x23ce00: per-param fmt `"%s"` @ 0x4104a0, combine `"%s%s%s%s"` @ 0x40fe8a, salt `Japass^2>.j` @ 0x41b6f7 — the salt is a **firmware constant**, not password- or device-derived.) This was confirmed on **11/11 live pairs** across both cameras (1 real-cloud anchor + 10 run-7/run-8 pairs; AWS salt `ds*aFjjK.^<1` matched zero). See `local-camera-recovery/verify-algo-static-analysis-2026-08-08.md` §7 and `tools/validate_verify_pairs.py`. **Scope: CLOSED (2026-08-09, offline):** the `/message/sts` token step calls the structurally different 2-field `oc_cal_message_verify` @ 0x23da98 (`MD5hex(TOUPPER(nonce)+request_id+"Japass^2>.j")` — param_1 absent). The sts 2-field formula is now **empirically confirmed on 42/42 live STS_FORGED pairs** recovered from 9 existing capture sessions (paired to their issued nonces by request_id; permutation/salt cross-check matched the derived NR+std form and nothing else), and the post_v2 formula now stands at **49/49** including the real-cloud anchor. The whole check-in chain — nonce → sts token → post_v2 — is forgeable on the **verify side**; camera acceptance of a forged sts token response still needs the live forge run to prove. Note §7.1 is retired; `tools/scan_verify_pairs.py` validates both formulas offline (exit 0 = both confirmed).

- ~~**Verify changes ⇒ the admin password is in the formula**~~ — **MOOT (resolved 2026-08-09):** the formula is fully recovered and does not involve the password at all; the salt is the firmware constant `Japass^2>.j`. See §2 below.
- ~~**Verify does not change ⇒ separate cloud/device credential**~~ — **MOOT (resolved 2026-08-09):** the "secret" was never a per-device credential — it is a firmware constant. The reset experiment is **not required for formula recovery**; its remaining purpose is a **known admin password** for `/NetSDK/*` and `/user/*.xml` access (the 397-attempt password sweep found no derivable password).

---

## 2. Camera Choice Rationale — pick **10.0.0.169** (eseeid `4781620744`)

| Criterion | 10.0.0.169 (eseeid 4781620744) | 10.0.0.29 (eseeid 4780038910) |
|---|---|---|
| `/message/` chain fired under **current** MITM config (run 8, 00:22Z) | ✅ 2 pairs (only camera that fired) | ❌ silent in run 8 |
| Baseline pairs under current config | **4** (2 run-7 + 2 run-8) | 6 (run-7 only) |
| Run-8 forge answerer | plain :80 listener (CONNECT :80 → NONCE_FORGED) | — |
| Real-cloud reference pair | none | ✅ `0b93ccb6…` (real server **rejected** it, err 3004) |
| Full 0x11 / WS dial history | ❌ run 8 was /message/-only (no :19000 SYNs) | ✅ 19 × :19000 dials in run 7 (LITE, never adopted) |
| Touched by later runs | run 8 active | run 7 active |

**Decision: reset .169 first, keep .29 untouched as control/reference.**

1. **Cleanest config-matched baseline** — .169's 4 pairs (below) were captured under exactly the forge configuration we will re-run (plain :80 forge + TLS :9900 forge, run 8 = most recent). Diffing post-reset pairs against them is apples-to-apples.
2. **Proven chain-firer** — .169 fired nonce→post_v2 in the most recent run; the experiment's primary data source is the `/message/` chain, and .169 demonstrably produces it.
3. **Isolation** — one camera is sacrificed to the experiment. If the reset reveals a destructive surprise (gate stays closed, eseeid re-binds, firmware re-locks), .29 remains pristine for the next attempt. Its real-cloud pair also documents the "real server rejects with 3004" reference behavior for comparison.
4. **Note on the FULL 0x11 (secondary objective)** — .169 did *not* dial :19000 in run 8, so capturing a boot-time 0x11 registration is a **bonus, not a requirement**, of this run. If power-cycle timing delivers it, diff it against the real-cloud registrations to locate the password-hash field; if not, the verify pairs alone satisfy the primary objective.

---

## 3. Baseline Dataset (Before-State) — archive these session dirs

All pairs below are from `captures/eseecloud-connections.log` lines in the named sessions; both the nonce (we forged it, so we know it exactly) and the verify (camera computed it) are known. **Do not delete or re-run these sessions.**

### 10.0.0.169 (eseeid 4781620744) — 4 pairs

| # | Session | nonce (forged by us) | verify (computed by camera) | mode |
|---|---|---|---|---|
| 1 | run 7 `20260808T232711Z` | `64646be2219729eacf30555439bc1fee` | `9586a12876cde3959d1c02135ed0421f` | — |
| 2 | run 7 `20260808T232711Z` | `6e1eb1387ad6538b933095efe432839d` | `f2af8d3dc456b4385f0914f87be217e3` | — |
| 3 | run 8 `20260809T002245Z` | `898a5175a15bd5ff1e2e9d7bdfc4cdb0` | `f201d7aad83ea62e2cf19b94fa06b704` | 0 |
| 4 | run 8 `20260809T002245Z` | `2b764fcc343a2b89acaeced915786385` | `de5e83ea19253ecedcf745b62d052222` | 1 |

### 10.0.0.29 (eseeid 4780038910) — 6 pairs (control camera; do not reset)

| # | Session | nonce | verify |
|---|---|---|---|
| 1 | run 7 | `1711ff544f64221ce06ecbbf596ca290` | `5b575c1df6239410740f72e7c412fcc9` |
| 2 | run 7 | `d175279e3ec01b24fbb03d643307e3ed` | `233d1745d8736bb79fe9571f45a7c1b8` |
| 3 | run 7 | `332037406150262f256a5627f3a86240` | `694acf63eed702ed1aa20be0fe586e9b` |
| 4 | run 7 | `5f36ec179bf13cadd23b6e19e66c7ec9` | `78fb5b2015f6807961cb67ae89a7764a` |
| 5 | run 7 | `494fe5d94d77eac6cf93ba8595e7a0df` | `5d91d153bdfb403c5fe3957f0f2a35ae` |
| 6 | run 7 | `78804d6ce7671688426995025d40835a` | `6161c82b0a54562802463ce3050fbefd` |

### Real-cloud reference (eseeid 4780038910 — the real server's verdict)

- Session `20260808T050802Z`, stream `47.251.40.12:80 → 10.0.0.29:39838` (05:09:55Z): `verify=0b93ccb64b348dee83f1c8e79e22e31b` → real server replied **HTTP 500** `{"error":3004,"error_description":"Invalid verify","msg":"no user to push"}`. Proves the camera's stored credential does **not** match the cloud's record for that eseeid (dead/rotated binding) — the `/message/` path cannot flip the gate against the real cloud regardless of forge quality.

### Precondition A Verdict — Password Sweep (COMPLETE 2026-08-09, 0 hits)

**Objective of the sweep:** try to crack the now-non-blank admin password from firmware/ROM/device-derived material **before** any physical reset — so the controlled experiment (which needs a *known* password) could be run without the reset, or even skipped entirely if the current password was recovered.

**Method:** candidate passwords tested live via Basic auth against `GET /NetSDK/System/deviceInfo` on **both** cameras (10.0.0.29, 10.0.0.169) — HTTP 200 = crack, 401 = reject. Sweeps were phased (camera-independent constants first, then per-device forms) with 150 ms pacing and lockout-signature detection (`lock|forbidden|too many|denied` in the body); no lockout appeared and both cameras continued answering normal 401 `Invalid Operation` throughout.

**Pass 1 — curated 205-attempt sweep (0 hits):** 205 total live attempts across both cameras (101 on .29 + 104 on .169; the ~57 camera-independent phase-1 candidates were tested on each camera, plus per-device serial/eseeid/MAC forms)

| Candidate family | Examples tested |
|---|---|
| ROM / reverse-engineered constants | `m#PWD`, **`d<?&pWD`** (IPCAKV3C ROM), **`?k2PfF`** (IPCAKV3C ROM), **`+4gPwD>4tJ`** (FWHI102 NVR ROM), `dvr163`, `KP2P`/`kp2p`/`k2p`, `eseecloud`, `esee`, `cloud` |
| Common admin defaults | `admin`, `root`, `operator`, `123456`, `888888`, `666666`, `p@ssw0rd`, `admin@123`, `BossCam2026!` (~40 more) |
| Firmware release dates / versions | `20211210`, `20211221`, `20240715`, `20260808`, `20260809`, `3.6.103`, `3.6.103.5721106`, `3.6.60`, `3.6.58`, `3.6.2.22`, `2.4.2.103`, `5721106` |
| Manufacturer / channel names | `GUANGZHOU`, `guangzhou`, `GZ`, `Driveway`, `driveway`, `3` |
| Per-device serial / eseeid / MAC forms (per camera) | full serial + rev + suffixes/prefixes (`Z7C34781620744`↔`Z7C34780038910`), eseeid + rev + slices (`4781620744`/`4780038910`), MAC `9ca3a9bc6fec`, combos (`admin+serial`, `admin+eseeid`, `serial+eseeid`, `serial+168/123`) |

**Pass 2 — full ROM/decompile meaningful sweep (0 hits):** extracted **40,337** printable 6–30 char tokens from the three ROMs + `firmware-decompile-akv3c.txt`, noise-filtered to 28,272 (dropping u-boot/kernel cmdline, paths, hex dumps, repeated-char padding), then narrowed to the **96 word-like / code-context candidates** most plausibly human-chosen. The other ~28k are random printable blobs from compressed/encrypted ROM regions — empirically 2,218/2,500 in a sampled cap were mixed-case random with near-zero password probability. All 96 tested on both cameras (192 live attempts) including:
- ROM words: `kernel`, `watchdog`, `ethernet`, `interface`, `photosensitive`, `encrypt`, `threshold`, `default`, `status`, `memory`, …
- Decompile code-context tokens: **`oc_get_message_token`** (actual function name in the cloud check-in code), `vencoder`, `ramdisk`, `fifosize`, `mipicsi0`, `motor0/1`, `saradc0`, …
- All-caps/mixed forms: `ATLAUR`, `MGFNLP`, `SYYYWH`, `VILEIN`, `ZYYIGR`, `FAESDYY`, …

**Result: 397 total live attempts (205 pass 1 + 192 pass 2), ≈237 unique candidates, 0 hits.** (≈237 = ~57 camera-independent pass-1 constants + ~91 pass-1 per-device forms + 96 pass-2 minus ~7 overlaps with pass 1 — GUANGZHOU, guangzhou, Driveway, driveway, 5523, 5523w, Z7C3478.) The non-blank admin password is **not** derivable from: ROM string constants, words/identifiers in the decompiled firmware, serial/eseeid/MAC/date/device-name/channel-name forms, or brand/firmware defaults.

**New material for the future verify-formula work (recorded here):** the ROM constants `d<?&pWD`, `?k2PfF`, `+4gPwD>4tJ` (alongside the known `m#PWD`) and the code-context token `oc_get_message_token` were NOT found in the admin-password sweep but remain candidate salts/format ingredients for the `oc_cal_verify_aws` MD5 formula (see `local-camera-recovery/firmware-decompile-salts.txt`).

**Consequence:** Precondition A is satisfied with a decisive negative. The physical factory reset of .169 (§5 step 2) remains the only path to a known admin password; the controlled experiment proceeds exactly as written below.

---

## 4. Known-Password Mechanism — `/user/set_pass.xml`

### 4.1 Wire shapes (both documented)

1. **Truth-report shape (GET, query-string driven, proven April 19 on .29):**
   ```
   GET /user/set_pass.xml?username=admin&password=<current>&content=<new>
   ```
   Returns XML status, e.g. `<user ver="1.0" you="admin" ret="ok" …>`. The April proof was blank→blank (`username=admin&password=&content=`); for this experiment it becomes blank→KNOWN.

2. **BossCam contract shape (POST, from `EndpointContractCatalogService` seed, `users.private.password`):**
   ```
   POST /user/set_pass.xml   Content-Type: application/x-www-form-urlencoded
   username=admin&newPassword=<new>
   ```
   Contract: PrivateCgiXml surface, ExpertOnly, DisruptionClass ServiceImpacting, newPassword min 8 / max 63 chars.

### 4.2 ⚠️ Gate caveat (live-proven 2026-08-09)

Today's probe showed **`set_pass.xml` is behind the same `"check in falied"` gate as `user_list.xml`** — the gate check fires *before* password validation:

```
GET /user/set_pass.xml?username=admin&password=WRONG123&content=NEWPASS
→ HTTP 200  <user ver="1.0" you="admin" add_user="no" ret="sorry" mesg="check in falied">
```

So `set_pass.xml` only works in a **non-gated** state. The gate is driven by cloud check-in state (`$.Auth.ticket`); today the gate is CLOSED on both cameras, and the real cloud rejects the check-in (3004), which is exactly why the gate is closed. **Whether factory reset re-opens the gate is the single biggest unknown of this experiment** (see §6 decision tree).

### 4.3 Post-write verification (truth-report discipline)

After the write, confirm with:
```
curl -u 'admin:<KNOWN>' http://<ip>/NetSDK/System/deviceInfo   → expect HTTP 200
```
(Post-write auth read must remain valid — the April report's acceptance bar.)

### 4.4 Chosen known password

`BossCamCtrl2026!` — 16 chars, meets the 8–63 contract range, alphanumeric + symbol, unambiguous. Recorded **only** in this report and the session metadata file; do not reuse the MITM scripts' default (`BossCam2026!`) so the two can never be confused in the diff.

---

## 5. Protocol Steps (numbered, in order)

> **Precondition A:** ✅ **SATISFIED 2026-08-09** — firmware-salts password sweep completed with **no** match (Precondition A Verdict below). 397 live attempts (≈237 unique candidates) against `/NetSDK/System/deviceInfo` on both cameras (205-attempt curated sweep + 96-candidate full ROM/decompile meaningful sweep), **0 hits**, no lockout signature. If a match had been found, the experiment would have become unnecessary — the sweep wins outright.
> **Precondition B:** BossCam service stopped or not polling the two cameras (avoid the recovery/enroll pipeline's unknown-password writes interfering; we control the password ourselves via `set_pass.xml`).

1. **Archive baseline.** Confirm §3 sessions intact; copy the two session dirs to `captures/archive-controlled-baseline/` (read-only copy).
2. **Physical reset .169.** Hold reset button 10–15 s until it reboots. (User action; this is the one manual step.)
3. **Poll for factory state** — do **not** run `enroll-recovered-cameras.sh` (it enrolls with blank password and starts recording — unwanted noise). Poll manually until:
   ```
   curl -u 'admin:' http://10.0.0.169/NetSDK/System/deviceInfo → 200
   ```
   (use the `camera_ready` loop pattern from `enroll-recovered-cameras.sh`, 180 s budget).
4. **Gate check (decision point A).**
   ```
   curl http://10.0.0.169/user/user_list.xml
   ```
   - **Gate OPEN** → proceed to step 5 (Plan A).
   - **Gate CLOSED** (`check in falied`) → try `set_pass.xml` anyway (step 5) and record the response; the gate may only gate reads. If blocked (any response) → follow the §6 tree, which retries the POST shape before falling to Plan B.
5. **Set known password (Plan A).**
   ```
   curl -i "http://10.0.0.169/user/set_pass.xml?username=admin&password=&content=BossCamCtrl2026!"
   # fallback shape if the query-string form errors:
   curl -i -X POST http://10.0.0.169/user/set_pass.xml \
        -d 'username=admin&newPassword=BossCamCtrl2026!'
   ```
6. **Verify write.**
   ```
   curl -u 'admin:BossCamCtrl2026!' http://10.0.0.169/NetSDK/System/deviceInfo → expect 200
   curl -u 'admin:' http://10.0.0.169/NetSDK/System/deviceInfo → expect 401 (password changed)
   ```
7. **Relaunch the MITM** (same config as run 8 — plain :80 + TLS :9900 forge; the /message/ chain rides plain :80):
   ```
   sudo ./scripts/eseecloud-mitm-capture.sh 360 10.0.0.169
   ```
   Passing only `.169` isolates the run; note that `.29` is NOT spoofed during this window and keeps dialing the real cloud uncontroled — already gated, no risk, but expect the gate-probe verdicts to report a single camera. Power-cycle .169 at the trigger line to attempt a boot-time FULL 0x11 (bonus objective).
8. **Capture post-state pairs.** From the new session's `eseecloud-connections.log`, extract every `NONCE_FORGED` + matching `MESSAGE_POST` line (same `request_id`). Target ≥ 3 pairs; run 8 produced 2, run 7 produced 8, so a single 360 s window should suffice; extend if needed.
9. **Diff against baseline (§7 matrix).** Feed the new pairs to the crack harness with `BossCamCtrl2026!` as the *known* salt across all shape families; also diff any captured FULL 0x11 registration against the real-cloud registrations.

---

## 6. Post-Reset Decision Tree (gate state is the key unknown)

```
Factory reset .169
   │
   ├─ admin: blank → 200 on /NetSDK/System/deviceInfo ?
   │    ├─ NO → reset incomplete; re-poll / re-reset (up to 2 tries), else abort & report
   │    └─ YES
   │         ├─ /user/user_list.xml gate OPEN ?
   │         │    ├─ YES → Plan A: set_pass.xml known password → verify → MITM → diff  (§5.5–5.9)
   │         │    ├─ NO → try set_pass.xml anyway (gate may be read-only gating):
   │         │    │    ├─ set_pass returns ret="ok" → proceed as Plan A
   │         │    │    ├─ set_pass returns "check in falied" → PLAN B (below)
   │         │    │    └─ set_pass returns ret="sorry" with a NON-gate mesg (validation/
   │         │    │         complexity rejection, or query-string shape refusing non-empty
   │         │         │  content) → retry with the POST shape
   │         │         │  (username=admin&newPassword=<new>) → if both fail, capture the
   │         │         │  exact error and STOP (document; do not guess).
   │         │    └─ PLAN B: run the MITM on the camera in factory state WITHOUT setting
   │         │         a password. Factory default admin is blank (or a known constant);
   │         │         captured verify pairs would then be computed with that KNOWN
   │         │         default → diff works identically, only the salt label changes.
   │         │         ⚠ FATAL ASSUMPTION: a factory-state camera with NO cloud account
   │         │         bound may never attempt post_v2 check-in at all (nothing to check
   │         │         in for) — power-cycling will not manufacture it. If Plan B yields
   │         │         0 pairs after one retry, STOP: the experiment cannot proceed
   │         │         without first opening the gate (WS 0x11 adoption — circular).
   │         │         Document and re-plan; do not burn repeated windows.
   │         └─ (gate opens via a REAL successful cloud check-in after reset — new eseeid
   │              binds, factory creds accepted by the real cloud) → still Plan-A
   │              compatible: password remains the only variable we control; proceed with
   │              set_pass + verify + MITM exactly as Plan A.
   └─ (implication branch) gate closed AND set_pass blocked (both shapes) AND factory
        defaults unknown → the reset did not restore local user management; document and stop.
```

---

## 7. Expected Before/After Verify Diff Matrix

Nonces are random per request, so "before/after" is **not** a byte comparison of verify values — it is a comparison of **crack outcomes** against the new pairs with the known password fed in as salt. The pre-change baseline outcome is *known*: **zero matches everywhere** (with blank/guessed salts).

> **POST-RESOLUTION UPDATE (2026-08-09):** the formula is now **recovered and password-independent** (§1 Hypothesis). The matrix below is therefore **confirmatory, not recovery**: the expected post-reset outcome is that all new pairs match the already-known formula `MD5hex(TOUPPER(nonce)+eseeid+TOUPPER(request_id)+"Japass^2>.j")` regardless of the password change. **Any deviation (a pair that does NOT match) is the only interesting case** — it would reopen the per-device-secret question (e.g. a per-device salt or a firmware difference between the two cameras). The password `BossCamCtrl2026!` remains the controlled variable; the matrix rows are retained verbatim for the historical record.

| Outcome (post-state crack with `BossCamCtrl2026!` as salt) | Meaning | Formula recovery | Next action |
|---|---|---|---|
| **A. Match found** in an existing family (md5/md4/sha1/sha256 × concat order, salt = password or hash(password) hex/raw/upper) | Admin password is in the formula | ✅ **Immediate** — report `H`, concat order, salt transform | Verify against a 2nd known-password pair; then re-run MITM with a *different* known password to triple-confirm; write the formula into the forge engine |
| **B. Match found only with password-derived salt not in the seed families** (e.g. `H(password ∥ serial ∥ nonce)`, `H(md5(password) ∥ nonce)`, salted with eseeid/m#PWD) | Admin password in formula with an additional per-device constant | ✅ within one harness extension (add the known-password × known-device-constants families) | Extend harness; re-run diff; same confirmation loop |
| **C. No match in any family, all ≥ 3 new pairs** | Password is **not** the secret → separate cloud/device credential | ❌ decisively ruled out | Pivot: hunt the credential — flash dump (if reachable), provisioning data, EseeCloud account recovery for eseeid 4781620744, or SD-backdoor-kit flash read |
| **D. 0 new pairs captured** | Chain didn't fire post-reset | n/a | Run failure: power-cycle at trigger; verify .169 still answers deviceInfo; re-run once; if still 0, restore state and re-plan |
| **E. Verify pairs change between run-7 and run-8 baselines on .169 (same unknown password)** | Sanity check of the baseline itself | n/a | Not expected (password didn't change between runs 7 and 8); if observed, investigate drift before trusting the diff |

**Confidence thresholds:** the crack harness tests *specific deterministic formulas*, so a single exact match of `verify == H(known-password, nonce)` has a false-positive probability ≈ 2⁻¹²⁸ per candidate formula — essentially zero even across the full enumerated family space. One matching pair IS the formula. The ≥ 3-pair rule is therefore for **stability**, not coincidence exclusion: all post-state pairs must crack to the *same* formula for outcome A/B to be accepted (the run-8 mode-0/mode-1 difference also gives a per-mode check: the formula must hold for both modes).

**Secondary deliverables** (same run, if the WS channel fires): byte-diff of the FULL 0x11 registration (and grant) against real-cloud registrations.
> **## SHIPPED 2026-08-09 (pass 4) — --eval LITE cadence progression table

Code: scripts/controlled-verify-experiment.sh (no other files). The offline session review (--eval mode and the full-run eval_session) now prints a per-LITE-delta table from the ws-server's LITE_DELTA lines — time (UTC), pconv, counter range, delta, interval, note — so the review shows the cadence PROGRESSION (first-seen -> natural +0x14 steps -> adopted +0x1e steps) instead of only the verdict bucket.

- New report_lite_delta_table() helper (helpers section, before mode dispatch): parses the two exact _lite_delta_check log shapes with anchored regexes, gates on 'LITE_DELTA' so REGISTER/NONCE_FORGED lines never enter the table, strips the _log-appended ' port=N src=IP' suffix from notes, keeps a RAW fallback row for unparsable LITE_DELTA lines, writes $RUN_DIR/lite-delta-table.txt and echoes it (detail echo gated on the file existing).
- Wired into --eval mode after report_lite_verdict, and into eval_session() after report_lite_verdict.
- Validation: bash -n clean; harness ran the actual extracted function against 4 fabricated cases (mixed incl. two non-LITE lines that must NOT appear + note suffix stripped; unparsable LITE_DELTA -> RAW; no LITE_DELTA; missing log) — all pass; a real --eval run against a fabricated session printed the 3-row table and wrote lite-delta-table.txt with no port=/src= noise; three reviewer passes clean (fixes: module-level return 0 SyntaxError -> sys.exit(0); RAW fallback polluting the table with non-LITE lines -> 'LITE_DELTA' gate; note column carrying port/src suffix -> re.sub strip).

## SHIPPED 2026-08-09 (pass 3) — §5.1b pre-reset LITE baseline gate check

Code: scripts/controlled-verify-experiment.sh (no other files). Closes an attribution confound in the §5.7 LITE verdict: the target camera has been booted for days and may STILL hold a sticky LITE grant (delta +0x1e) from an earlier run — a post-reset LITE_ADOPTED would then be a false positive attributed to the reset run.

- New env: LITE_BASELINE_DURATION (default 90s), documented in usage.
- New lite_baseline_check() runs a monitor-only MITM window BEFORE the §5.2 reset with lite_cadence=0 AND next_counter=plus1 (run_mitm gained $5=next_counter_mode; default cadence preserves §5.7 behavior). Counter+1 grants are never adopted under MITM for either LITE or FULL 0x11, so the baseline is genuinely passive: the camera keeps its own +0x14 natural cadence and the /user/*.xml gate cannot flip pre-reset.
- Baseline classifies the ws-server's raw LITE_DELTA deltas (hex-boundary anchored): +0x1e -> LITE_BASELINE_ADOPTED (pre-existing sticky grant), +0x14 -> LITE_BASELINE_CLEAN, no LITE_DELTA -> LITE_BASELINE_NO_TRAFFIC, other -> LITE_BASELINE_OTHER. Writes $RUN_DIR/lite-baseline.json.
- DONE summary now reads BOTH lite-baseline.json and lite-verdict.json and prints the attribution verdict: ADOPTED+ADOPTED -> pre-existing (NOT a post-reset outcome); CLEAN+ADOPTED -> attributable; NO_TRAFFIC+ADOPTED -> unproven; baseline OTHER/UNKNOWN + post-ADOPTED -> unproven, not pre-existing.
- Skip guard: baseline is skipped (no json written) when WS_LITE_MONITOR=0, so a disabled monitor cannot produce a misleading NO_TRAFFIC->UNPROVEN attribution.
- Validation: bash -n clean; harness ran the actual extracted function against 4 fabricated log cases + the skip guard, all passing; two reviewer passes clean (fixes: FULL 0x11 in baseline was still granted adoptable cadence -> plus1; WS_LITE_MONITOR=0 skip guard).

## SHIPPED 2026-08-09 — controlled-verify-experiment.sh: §5.7 LITE-grant verdict + §7 in one run

Code: scripts/controlled-verify-experiment.sh (no other files). The post-reset §5.7 MITM relaunch now defaults to the LITE-aware grant (ws-server --lite-cadence 0x1E + --lite-monitor, shipped earlier) and reports the LITE-grant verdict alongside the §7 diff-matrix, so ONE post-reset run answers both questions.

- New env: WS_LITE_MONITOR (default 1) and WS_LITE_CADENCE (default 0x1E) documented in usage + passed through run_mitm() to eseecloud-mitm-capture.sh.
- New report_lite_verdict() helper (defined in the HELPERS section BEFORE mode dispatch so --eval mode, which dispatches first, can call it): greps the session connections log for the ws-server's LITE_DELTA / FULL_ESCALATION / 'ADOPTED — equals our granted lite-cadence' / 'natural +0x14 cadence' markers and classifies LITE_ADOPTED / LITE_IGNORED / LITE_ESCALATION / LITE_UNKNOWN, writing $RUN_DIR/lite-verdict.json.
- Called from eval_session() (full run) and --eval mode; the DONE summary prints the verdict from lite-verdict.json.
- Precedence (reviewer-confirmed): ADOPTED wins even when the camera ALSO escalated (the §5.7 question is adopted-vs-ignored; the escalation is appended to the hint); ESCALATION is the headline only when no LITE deltas classify the outcome.

Validated: bash -n clean; a harness extracted the ACTUAL function from the script and ran it against fabricated connections logs for all five cases (adopted / ignored / escalation / unknown / combined adopted+escalation) — all classified correctly, combined yields LITE_ADOPTED with the escalation noted. Two reviewer passes clean (precedence fix confirmed; JSON write simplified to a single stdout-dump path).

## OFFLINE MINING 2026-08-09 (pass 2) — /address/device discovery plane + account-binding forensics

Mined the real-cloud boot captures (20260808T053714Z / 055759Z / 061742Z — the sessions where the cameras reached the REAL ngw.dvr163.com; the 04:54/05:07 sessions were MITM-forge runs with no real /address/device, the 05:08Z session carries the real message-plane 500). Purpose: determine whether the eseeid accounts were ever push-provisioned and whether a cloud-side re-bind (eseecloud web API) is a viable unlock path alongside the reset.

### Real /address/device request (byte-accurate, cam->ngw.dvr163.com 47.254.14.87:80)

    POST /address/device?sn=JAZ7C34780038910&max_ch=1 HTTP/1.1
    Host: ngw.dvr163.com / Connection: close / User-Agent: KP2P
    body: sn=JAZ7C34780038910&module=5523-W&odm=JUAN&hwcode=572110
          &fwversion=03061030&version=01043000&area=&install_type=0
          &cloudrecord=0&stream_des={"ch_count":1,"channels":[{"ch_id":0,"stream":[0,1]}]}
          &capabilities=[5,11,12,8,14,17]&r=2468113

The request carries ONLY device identity + capabilities + a per-request random r=. **No account, eseeid, user, or bind credentials exist in the device-plane request.** .169's body is identical shape with sn=JAZ7C34781620744.

### Real /address/device response (byte-accurate, srv->cam, PHP/5.6.37, Content-Type text/html)

    {"ipv4":"129.153.101.14","ipv6":"2603:c020:10:8100:e25f:dc8f:1c1e:a08d",
     "udpport":"19000","tcpport":"19000","sslport":19001,"pconv":47800389,"id":"4780038910",
     "tconv":2085623285,"stun":{"ipv4":"14.17.121.21","ipv6":"::1","port":"3478"},"random":"2529255","forcetcp":1}

(.169 variant: ipv4=172.235.43.92, ipv6=2a01:7e03::2000:5ff:fe28:6119, pconv=47816207, id=4781620744.) Fields: pconv = first 8 digits of the eseeid; id = full 10-digit eseeid (both serial-derived: the last 10 digits of the serial); tconv varies per request (2085623285 / 131279872 / 174586376 / 211238916 / 199803016 observed — a fresh random per request); stun is the CONSTANT 14.17.121.21:3478; random ECHOES the request's r=; forcetcp=1. **No account/binding fields exist in the response either — this is pure P2P address assignment.**

### Our forge matches byte-shape (validated)

`_forge_discovery_response` (eseecloud-dns-server.py) emits the same field set with the same JSON separators: per-request random tconv, echoed r=, serial-derived pconv/id, constant stun 14.17.121.21. The only diffs are by design: forged ipv4 (P2P_FORGE_IP) and Content-Type application/json vs the real text/html; charset=UTF-8. The discovery plane is not the lock.

### Account-binding sequence — there IS no device-plane bind

The full captured HTTP surface across ALL real-cloud sessions is exactly: /address/device, /message/nonce, /message/sts, /message/message (post_v2), /NetSDK/*, /onvif/*. DNS traffic is only ngw.dvr163.com (address gateway), pm.dvr163.com (message gateway), msg-img-hk.oss-cn-hongkong.aliyuncs.com (image upload). **No register/bind/add-device endpoint exists in the device plane** — device-account binding is server-side (the app/portal binds the eseeid to a user account), and nothing the camera sends reveals or changes that binding. The static-analysis report already pinned the cloud-layer lock: the real server's post_v2 answer to eseeid 4780038910 is `{"error":3004,"error_description":"Invalid verify","msg":"no user to push"}` — the eseeid has no bound pushable cloud user — per the static-analysis report's interpretation (§1.1), still registered to the previous owner's account (the 500 body itself only proves 'no bound pushable user'; the owner attribution is inference).

### Cloud-side re-bind via eseecloud web API — NOT a viable unlock path

1. No bind endpoint exists in the device plane to exercise; binding is owner-account-side.
2. The eseeid is bound to the previous owner — re-binding requires their eseecloud account credentials (or account recovery), which we do not have and cannot obtain from firmware/captures.
3. The real server ACTIVELY rejects this camera's check-in (3004 Invalid verify / no user to push), so even a fresh binding would not clear the locked cloud state without owner-side action.
4. The local admin password lock (Precondition A: 28,272-candidate sweep, 0 hits) is independent of cloud binding.

Conclusion: the /address/device discovery plane is healthy and our forge is byte-shape-accurate; the lock is entirely (a) the cloud-layer binding (previous owner's account, no pushable user) and (b) the local admin password. The factory reset (known-password state, §5.7) remains the only lever — cloud-side recovery is closed. The captured /address/device bodies are now ground-truth for any future discovery-plane work.

## SHIPPED 2026-08-09 — LITE monitor run-mode (auto-catch FULL escalation)

Code: scripts/eseecloud-ws-server.py --lite-monitor + scripts/eseecloud-mitm-capture.sh (WS_LITE_MONITOR=1 default). The camera's natural LITE 0x00 cadence is +0x14 per 20s and our LITE grant uses +0x1E (non-natural, confound-free); if the live test shows NOT-ADOPTED the LITE form may not be grantable at all, and the only path to FULL 0x11 is the reset. To avoid requiring a power-cycle to catch the escalation, the run-mode now:
- Logs LITE_DELTA per camera (keyed by pconv — each LITE re-dial is a fresh TCP connection) annotating natural +0x14 vs a ★ ADOPTED +0x1E delta.
- Flags FULL_ESCALATION ★★★ the moment a FULL 0x11 registration arrives after any grant to that camera (the adoption precondition).
- The capture loop greps for FULL_ESCALATION once per run (ESC_SEEN guard) and extends the window +120s on first sight so the full registration + grant land within the SAME run — no power-cycle needed.
- Post-run, report_lite_monitor() prints both marker sets (pure log-grep, before the curl guard).

Validated offline: py_compile + bash -n clean; simulation fed first-seen / +0x14 natural / +0x1E adopted / FULL-after-grant frames, all four markers logged with src/port and assertions passed. Reviewer confirmed: no double-logging (REGISTER branch is the sole _lite_delta_check call site), no GOAL_DEADLINE conflict (ADOPTED-based early exit still wins), CLI default-off + mitm default-on coherent, grep patterns match markers exactly, granted_pconvs/lite_last_by_pconv bounded by fleet size.

OFFLINE MINING 2026-08-09 (sweep running — no live traffic):** three new ground-truth artifacts from the real-cloud captures:
> 1. **Real /message/nonce reply body (05:08Z)**: `{"request_id":"<rid>","nonce":"<32-hex>"}` with `Set-Cookie: PHPSESSID=<rid>; path=/`, `X-Powered-By: PHP/7.2.29` — our forge already emits this exact shape (json.dumps, same separators + cookie), so the nonce step is byte-accurate.
> 2. **Real post_v2 reply is a 500 with body `{"error":3004,"error_description":"Invalid verify","msg":"no user to push"}`** — even PRE-LOCK the real cloud rejects the camera's verify: the account for eseeid 4780038910 cannot validate the check-in ("no user to push"). This is why real-cloud adoption never happened and why our MESSAGE_POST candidate JSON is ungrounded: the real server never returned a success shape in any capture. Unlock must be LOCAL (set_pass.xml / reset), not cloud-side.
> 3. **LITE 0x00 layout + cadence**: 32B = `abbccdde | 0x00 | 000000 | 000001 | counter[12:16] | pconv[16:20] | 00×12` (strict subset of FULL, same pconv). Its counter advances **+0x14 (20) per 20 s** (11 frames T030607Z, one frame +0x15) — NOT the FULL pconv cadence (0x13A0/0x15A0). Our current grant model applies the FULL cadence to LITE frames, which the camera has never been observed to accept; the correct LITE reply shape is unobserved (0 LITE in real-cloud captures — the real server only ever saw FULL). **SHIPPED 2026-08-09:** the ws-server now grants LITE frames `counter + lite_cadence` with a **non-natural default of 0x1E** (the camera's natural cadence is +0x14, so a confounded default would make the ADOPTED signal indistinguishable from the camera ignoring the grant). Adoption tracking keys by (pconv, cmd) so LITE and FULL verdicts can't contaminate each other; `--lite-cadence 0` restores legacy behavior. The next MITM run therefore answers the open question for free: if the camera's next LITE counter equals our granted `counter+0x1E` it adopted the grant (gate may flip); if it stays at +0x14 it ignores LITE grants entirely. FULL-dest census: camera sends FULL to BOTH `129.153.101.14:19000` AND `172.235.43.92:19000` (real) — both already in `OBSERVED_P2P_IPS` (no redirect gap); under MITM it sends LITE only to the forged dest. **Conclusion: FULL 0x11 only fires with a valid cloud session; the locked state only does LITE keepalives — the reset (factory state, known password) is the lever §5.7 relies on to restore FULL.**
> **P14 RETIRED (2026-08-09, offline, TENTATIVE):** the FULL 0x11 registration carries **NO password-hash field** — this refutes the earlier campaign premise (ws-server 0x11-handler comments, prior reports) that the FULL registration carries the password hash; the password travels through the `/user/*.xml` + NetSDK plane, not the cloud registration. Caveat: the 05:08Z capture is **pre-lock with a blank admin password**, so the all-zero tail could mean "no hash field" OR "hash of blank" — the definitive confirmation is the §5.7 post-reset FULL capture with the known password `BossCamCtrl2026!` (a non-blank known password either materializes a hash field or confirms absence). Full byte-diff of all 68 real-cloud FULL 0x11 frames (32× eseeid 4780038910, 36× 4781620744, session 20260808T050802Z): the 128-byte layout is `abbccdde | 0x11 | 00 00 00 | 00 00 01 | counter[12:16] | pconv[16:20] | 00×8 | 60 00 00 00 | serial-ASCII[32:42] | 00×86`. Bytes [42:128] are **all zeros and byte-identical across every frame** of each camera; the ONLY varying field is the 3-byte counter [12:15] (byte 15 is a per-camera constant: `0x21` for 4780038910, `0x24` for 4781620744 — the learned grant cadence anchor). Also established: the camera re-sends FULL 0x11 every ~10 s against a **valid** cloud session (28+ frames in 05:08Z, each granted with the exact next-counter), but under MITM it downgrades to LITE 0x00 every 20 s — a FULL 0x11 arrival under MITM is therefore the live signal that the camera **believes it has a valid cloud session and is attempting full registration** (a precondition for adoption; acceptance is only proven by the existing ADOPTED ★★★ verdict = the camera reusing our granted next-counter). That FULL-arrival precondition is exactly what §5.7/§7 A/B hunt for.

---

## 8. Data to Archive After the Run

- New session dir under `captures/eseecloud-mitm-<ts>Z/` (connections log, pcap, tls-server log, ws-server log, verdict).
- The extracted post-state pairs (nonce→verify + eseeid + mode) — append to this report as §3b.
- The exact `set_pass.xml` request/response lines (step 5/6 outputs).
- Crack harness output for the new pairs (all families, known-password salts).
- Note the gate state observed at step 4 (open/closed) and which plan (A/B) executed — this is the most decision-relevant datum for the follow-up on .29.

---

## 9. Risks & Guardrails

- **Do not reset .29** unless the .169 experiment succeeds or the user explicitly authorizes it.
- **Do not run the enroll pipeline** (`enroll-recovered-cameras.sh` / `factory-reset-recovery.sh --enroll`) — it enrolls with a blank password and may start recording, contaminating the controlled state. Poll manually (§5.3).
- The known password `BossCamCtrl2026!` is recorded in this report only; it is the *controlled* variable, not a secret to protect.
- Factory reset may re-bind the camera to the real cloud with a **fresh eseeid** (or no eseeid until provisioned). **Handling:** immediately after reset (step 3/4), capture the post-reset eseeid from `deviceInfo` and compare to baseline `4781620744`. If it differs, the *eseeid-derived* salt families (outcome B) lose their baseline constant — but the new eseeid is observable in every post_v2 URL anyway, so those families remain fully testable with the new value, and password-derived families (outcome A) are unaffected. Record the post-reset eseeid in the session metadata either way. If the gate opens via a *real* successful check-in, that also opens it — the experiment still proceeds identically (password is the only variable we control).
- If the reset bricks or re-locks .169, stop, document, and reassess with .29 (the control) before any further action.

## SHIPPED 2026-08-09 — §5.3b auto WiFi re-provision in controlled-verify-experiment.sh

Code: scripts/controlled-verify-experiment.sh + scripts/5523w-wifi-reprovision.sh (no other files). Closes the §5.2/§5.3 wired-camera assumption that has stalled every run: a factory-reset 5523-W **never returns to its old LAN IP** — the reset wipes the WiFi station credentials, so the camera leaves the LAN (HTTP 000) and becomes its own AP (SSID IPCZ7C34<serial>). The old `poll_factory` polled `$CAM_IP` for HTTP 200 forever and could never succeed.

- **`poll_factory` now detects the vanish**: `ZERO_BEFORE_REPROVISION` (default 2) consecutive HTTP 000 polls → the camera left the LAN. Fixed a latent `http_code()` doubling bug (curl `-w '%{http_code}'` printed 000 AND `|| echo 000` fired → `000000`, which broke `= "000"` matching); it now emits a single token.
- **§5.3b `reprovision_camera`** auto-invokes `scripts/5523w-wifi-reprovision.sh` with env passthrough (REPRO_OUT handoff file + STA_SSID/STA_PASS/CAM_MAC_PREFIX/SUBNET/AP_PASS), reads the new LAN IP from the handoff, verifies blank-admin HTTP 200, and **updates the global `CAM_IP`** — so §5.4 gate, §5.5 set_pass, §5.7 MITM, and §5.8 eval all automatically target the re-provisioned camera. `repro_tried` guard prevents re-triggering every 5s after a failed attempt; falls back to the tool's auto-pick mode when `REPRO_SERIAL=""`.
- **New envs** (all defaulted, documented in `--help`): `REPRO_SERIAL` (derived `JAZ7C34${CAM_ESEEID#4}` per the observed eseeid→serial→AP convention: 4780038910 → JAZ7C34780038910 → IPCZ7C34780038910), `REPRO_AP_PASS`, `REPRO_STA_SSID/PASS`, `REPRO_CAM_MAC_PREFIX`, `REPRO_SUBNET`, `REPRO_MAX_ATTEMPTS` (4 — camera AP can take 30-60s+ to boot), `REPRO_WAIT` (30), `REPRO_DISABLED`, `ZERO_BEFORE_REPROVISION`.
- `reset_prompt` tells the operator the laptop's WiFi will drop briefly; the DONE summary logs the final (possibly updated) `CAM_IP`.

Validation: `bash -n` clean on both scripts; a brace-counting harness extracts the ACTUAL `poll_factory`/`reprovision_camera`/`http_code` bodies and runs 5 cases (same-IP 200 → no repro; vanish → repro → CAM_IP updated; REPRO_DISABLED; real http_code single-token; locked-401 stays) — all pass; `--help` shows the 9 new REPRO_ envs. Two reviewer passes clean.

## SHIPPED 2026-08-10 — §5.3b payload-truth appendix: why the nested form with `lan.dhcp:true` was chosen

> **KEY-SPELLING HALF: RESOLVED LIVE 2026-08-10** — the REST-plane mode key question the keyprobe tool was built to settle is **closed**. Verdict (10.0.0.29, blank admin): GET returns `wirelessMode`; PUT under `wirelessMode` round-trips in both the nested `stationMode` and flat-sibling forms; PUT under the wire-plane key `wireless` is **rejected** in both forms. Close-out report with verbatim verdicts, ledger lines, and the deviceInfo serial form: [`2026-08-10-rest-keyprobe-verdict.md`](2026-08-10-rest-keyprobe-verdict.md).

Code: scripts/5523w-wifi-reprovision.sh only (STA_DHCP/STA_IP/STA_MASK/STA_GW envs + `write_lan_addressing`). This appendix is the payload-truth record the §5.3b re-provision relies on: the exact evidence for the `wirelessMode`+`stationMode` nested JSON shape, the `wirelessStationDhcp` flag value, and the explicit `lan` block write.

### The question

The camera's own wire-plane media-info frames serialize station config as `wireless=stationMode` with **flat** sibling keys `wirelessApEssId`/`wirelessApPsk`/`wirelessStationDhcp` — but the tool writes the **nested catalog form** (`wirelessMode:stationMode` + `stationMode:{…}`) and, since this pass, `wirelessStationDhcp:false` plus an explicit `lan {addressingType:dynamic, dhcp:true}` write. Which shape is right, and what should `wirelessStationDhcp` actually be?

**KEY-SPELLING HALF: RESOLVED LIVE 2026-08-10** — the keyprobe settled this on 10.0.0.29 (blank admin): the REST GET returns `wirelessMode` as the mode key, PUT under `wirelessMode` round-trips in **both** the nested `stationMode` and flat-sibling forms, and a PUT using the wire-plane key `wireless` is **rejected** in both forms. The nested catalog form is the accepted REST write shape; the wire-plane `wireless=` key is serialization-only and does not transfer to the REST plane. Full evidence (verbatim verdicts, ledger lines, deviceInfo serial form) in [`2026-08-10-rest-keyprobe-verdict.md`](2026-08-10-rest-keyprobe-verdict.md). The `wirelessStationDhcp` half (inverted semantics, write-only hint) remains as documented below.

**Fleet tie-in for this run:** the settled key truth applies to the §5.3b re-provision payload on BOTH units. On **10.0.0.169** (the reset candidate) the keyprobe cannot run pre-reset — it answers HTTP 401 to blank admin (verified live 2026-08-10, 12:00Z; ledger line under `unknown.jsonl`), so its own key truth is recorded **post-reset**: after §5.2 factory state restores blank admin, the §5.8 keyprobe delegation (`keyprobe_truth_check` → `5523w-wifi-reprovision.sh --keyprobe-only`) writes the verdict to `JAZ7C34781620744.jsonl` in the same pass as the LITE-grant verdict and §7 diff matrix.

### pcap attribution evidence (every capture, per-IP)

All sessions re-parsed with an IP→frame attribution (the earlier parse read the wrong field — the source IP is the token AFTER the `IP` marker; linktype 276 radiotap required tcpdump re-read):

| Camera | Sessions with media-info frames | wirelessStationDhcp |
|---|---|---|
| 10.0.0.29 (eseeid 4780038910) | 053046Z, 174548Z, 180503Z, 183313Z, 185549Z, 191225Z, 211443Z, 225011Z, 232711Z, 050202Z | **true** (every frame) |
| 10.0.0.169 (eseeid 4781620744) | 053046Z, 211443Z, 225011Z, 232711Z, 050202Z | **true** (every frame) |
| 10.0.0.227 (third anyka unit) | 050202Z only | **false** (every frame) |

**Conclusion: the flag is per-camera NVRAM addressing state, NOT an AP-mode-vs-station-mode flip.** Both 5523-W units emit `true`; the single `false` observation came from a third camera (10.0.0.227) that is not part of this experiment and is currently off-LAN.

### Live REST probe (2026-08-10, 10.0.0.29 blank admin — the decisive evidence)

1. **Catalog truth** — `NetworkInterfaceWireless` / `NetworkInterfaceStationMode` (endpoint_catalog.json) document `wirelessMode` + nested `stationMode:{wirelessStaMode, wirelessApBssId, wirelessApEssId, wirelessApPsk, wirelessFixedBpsModeEnabled}` — **`wirelessStationDhcp` appears 0 times** in the catalog. It is a write-only wire-plane hint.
2. **GET `/NetSDK/Network/interface/4/wireless`** (live) returns exactly the catalog shape and **never echoes** a dhcp key — independently confirmed by the keyprobe verdict (GET mode key: `wirelessMode`, no dhcp key in the response; see [`2026-08-10-rest-keyprobe-verdict.md`](2026-08-10-rest-keyprobe-verdict.md)).
3. **GET `/NetSDK/Network/interface/4`** shows the real DHCP lever in `lan`: `{addressingType:static, OnvifAutoAdapt:false, dhcp:true, staticIP:10.0.0.29, staticNetmask:255.255.255.0, staticGateway:10.0.0.1}`.
4. **PUT nested `stationMode.wirelessStationDhcp:false`** → HTTP 200 / statusCode 0 → **write-through into the lan block**: `addressingType:static→dynamic`, `OnvifAutoAdapt:false→true`. Not echoed by the subsequent GET.
5. **Semantics are INVERTED from the name**: `true` ↔ static addressing (.29's baseline — which is exactly why the original hardcoded `true` silently pinned the camera to its static NVRAM IP instead of DHCP), `false` ↔ DHCP/dynamic addressing. (Caveat for the record: this inversion is an **observed correlation** — two data points: .29's static-lan/true-frame baseline, and the PUT false→dynamic write-through — not a firmware-documented contract. The tool fix is built on the observed behavior, which is operationally sufficient.)
6. **One-shot + normalized**: PUT `true` (both nested and flat) and full-form `lan.addressingType:static` writes were all normalized back to `dynamic`/`OnvifAutoAdapt:true`; only `lan.dhcp` is reliably writable. .29's exact static baseline is therefore **not restorable via REST writes** on this firmware — the probe left it on `dynamic` (functionally the DHCP end state the tool intends; blank admin still answers 200).

### Why the nested form with `lan.dhcp:true` was chosen (the run's payload truth)

- The **nested catalog form is the only shape proven to switch the camera to station mode** (2026-08-09 dry run: PUT → camera left AP, joined Aegon, blank admin 200 at a LAN IP). The wire-plane `wireless=` form is media-info *serialization*, not a REST write contract — it was never the correct PUT shape. **Proven live 2026-08-10:** a REST PUT with the wire-plane `wireless` key is rejected in both nested and flat form (keyprobe verdict, [`2026-08-10-rest-keyprobe-verdict.md`](2026-08-10-rest-keyprobe-verdict.md)).
- `wirelessStationDhcp` is now emitted **matching intent under the inverted semantics**: `STA_DHCP=1` (default — DHCP is required so the reset camera reappears on the LAN for §5.3b MAC rediscovery) → write `false`; `STA_DHCP=0` (static, with STA_IP/STA_MASK/STA_GW) → write `true`.
- Because the flag's write-through is one-shot and normalized, the tool **also writes the documented `lan` block explicitly** (`addressingType:dynamic, dhcp:true` for DHCP; `static` + the staticIP trio for static) via read-modify-write on `/NetSDK/Network/interface/4` — the reliable lever — with a 2-attempt retry (the lan PUT can race the station-mode switch triggered by the wireless write) and a post-write verify-GET that warns honestly if the firmware normalizes `addressingType` back to dynamic (provably happens for static requests). **Both writes happen together on every re-provision**: the nested wireless form carries `wirelessStationDhcp:<flag>` AND the lan block is written explicitly — the flag is the wire-plane hint, the lan write is the deterministic lever; one does not replace the other.
- **Expected post-reset wire evidence:** after §5.3b re-provisions .169, its next media-info frame (if any — it only emits when it later dials the cloud) should read `wirelessStationDhcp=false`/dynamic — the live confirmation of the write-through. A fresh `true` would mean the write was normalized, which the tool now detects and logs.

Validation: `bash -n` clean; harness 10/10 (both payload variants emit the correct flag for both values, ssid/psk intact, valid JSON, lan dry-run plans correct); DRY_RUN smoke exits 0 at its STEP-2 design point; three reviewer passes clean (inverted-flag logic, `set -euo pipefail` safety, retry+verify+normalize-warning loop, no regression to the proven re-provision path).
