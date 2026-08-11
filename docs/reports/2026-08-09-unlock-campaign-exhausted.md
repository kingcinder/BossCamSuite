# 2026-08-09 — Unlock Campaign Exhausted: Every Software Vector Tried, Gates Still Closed

Verdict: **ALL SOFTWARE-ONLY VECTORS EXHAUSTED** — the `.169` / `.29` / `.227` 5523-W
gates remain closed (`/user/user_list.xml` → `mesg="check in falied"`). The 401 is an
**application-level ticket gate**, not a credential check: `$.Auth.ticket` is set only by a
cloud check-in that **cannot succeed** because the account binding is dead server-side
(`error:3004 "no user to push"`), and under MITM the camera only ever emits the
never-adopted LITE 0x00 registration form. The guaranteed remaining routes are **physical**
(SD-card injection / factory reset) or **operator-supplied credentials**.

---

## 1. Fleet state (verified live 2026-08-09 08:35Z)

| Camera | NetSDK HTTP :80 | RTSP :554 | eseecloud :8899 | proprietary :34567 | `/user` gate |
|--------|-----------------|-----------|-----------------|--------------------|--------------|
| 10.0.0.169 | 401 (gated) | **open** | closed | closed | **GATED** `"check in falied"` |
| 10.0.0.227 | dark (no HTTP) | closed | closed | closed | NO-RESPONSE |
| 10.0.0.29  | dark (no HTTP) | closed | closed | closed | NO-RESPONSE |

`.227` is the one camera with working credentials (blank-password NetSDK plane, enrolled,
recording job `e38aee4c`); `.29`'s snapshot job was running before it went dark. `.169`
carries a live RTSP server on 554 but its control plane is ticket-gated.

The fleet monitor (`scripts/fleet-unlock-monitor.sh`, added this session) polls all three
cameras every 5 min for 1 hour and auto-resumes the full-pool sweep on any camera that
returns with the gate still closed.

---

## 2. Vector-by-vector results

### 2.1 ONVIF default credentials — SUCCESS (discovery) / INERT (unlock)

- `OnvifCredentialScanner` discovered **`admin:admin`** working on the ONVIF device
  service of `.169` and `.227` (GetCapabilities → 200). Credential pair: `admin:admin`.
- **SetUser password reset via ONVIF proved inert**: the ONVIF plane and the
  web/NetSDK plane are independent; resetting the ONVIF password does **not** grant
  web-plane access. The reset was reverted (`admin:admin` restored) to keep the
  discovered credential stable for future scans.

### 2.2 NetSDK REST Basic-auth — 0 hits across 45,661 candidates

- `.227` answers `/NetSDK/System/deviceInfo` with **blank `admin:`** — that plane is
  genuinely open (why it's enrolled). `.169` / `.29` 401 every credential.
- **Full-pool sweep** (`sweep-full-pool.sh`, 500 ms pacing, checkpoint/resume with pool
  md5 guard): all **28,272 ROM/decompile strings** completed on **both** `.169` and
  `.29` — **0 hits** (checkpoints `28272/28272`, pool fp `ecc75fe619e86c652e75be73cfc514e1`).
- **Targeted brute** (`brute-netdsk.py`, 17,389 candidates: operator's known EseeCloud
  password `Toadnemesis2021~`, serial/MAC/PSK-derived variants): **0 hits** on both.
- **Manual high-value credentials** (`BossCam2026!`, `operator`, firmware strings):
  all 401.

### 2.3 EseeCloud MITM (elevated, full 420 s windows) — gates stayed closed

- Latest run (2026-08-09T05:02Z) against all three cameras: dozens of connections and
  data chunks captured, one fresh `/message/` verify hash from `.169` — every gate still
  closed, no ADOPTED FULL registration, no admin added.
- **Across every MITM session of the campaign: `LITE>0 FULL=0`** (confirmed by a
  campaign-wide scan of all session logs, and re-spotted this session in
  `20260809T050202Z` (22 LITE, 0 FULL) and `20260809T030607Z` (11 LITE, 0 FULL)) —
  the camera *never* sends the FULL 0x11 registration that would set
  `$.Auth.ticket`. It only emits the LITE 0x00 form, which is never adopted.

### 2.4 Verify formulas — CONFIRMED, but salt is a fixed constant, not the password

- **post_v2 check-in**: `verify = MD5hex(TOUPPER(nonce) + eseeid + TOUPPER(request_id) + "Japass^2>.j")`
  — decompile-resolved (`oc_cal_verify @ 0x23ce00`, salt `@ 0x41b6f7`) and validated on
  **11/11 pairs** across both 5523-W cameras (1 real-cloud anchor from `20260808T050802Z`
  + 10 controlled-run pairs, each 1-in-2^128 odds). Tooling:
  `local-camera-recovery/tools/validate_verify_pairs.py`,
  `local-camera-recovery/tools/eval_verify_matrix.py`.
- **sts message token** (2-field variant) also confirmed; AWS variant
  (`oc_cal_verify_aws @ 0x23d074`, salt `"ds*aFjjK.^<1"`) matched **zero** live pairs.
- **Critical**: the salt is a fixed firmware constant (`Japass^2>.j`), *not* the
  password — so the HTTP check-in can be forged byte-accurately, but the password can
  never be recovered from a verify hash, and the forged check-in does not unlock the gate.

### 2.5 Dead cloud binding — the root blocker (evidence)

The camera's check-in to the **real** EseeCloud is rejected server-side:

```
POST /message/message?method=post_v2
HTTP/1.1 500 Internal Server Error
{"error":3004,"error_description":"Invalid verify","msg":"no user to push"}
```

- Captured live from `.29` in `captures/eseecloud-mitm-20260808T050802Z/` (documented in
  `local-camera-recovery/verify-algo-static-analysis-2026-08-08.md` §1.1).
- Interpretation: the account binding is dead — **no user is bound to push to** these
  cameras. The camera *knows* its cloud account is unbound, which is exactly why it
  stays in the never-adopted LITE path and why every unlock attempt that depends on a
  successful check-in fails before it starts.

### 2.6 CGI fuzz — no bypass

`CgiFuzzer` swept the `/user/*.xml`, `/cgi-bin/*.cgi`, `/param/*`, and `/NetSDK/*`
endpoint families with method/path/header mutations and bypass headers. No endpoint on
the locked cameras served data without auth. `/cgi-bin/gw2.cgi` answers 200 on `.227`
with full device identity (serial `Z7C34781634738`, fw `2.4.2(3.6.103.5721106)`) but
only under the same blank `admin:` plane — not an unlock of `.169`/`.29`.

### 2.7 Backdoor ports — none open

HiSilicon 9530 telnet-backdoor POCs checked against the fleet; no backdoor ports
(23/backdoor, 8899, 34567) are open on any camera (`local-camera-recovery/
plan-execution-findings.md`: "HiSilicon 9530 backdoor POCs do not apply to this SoC
family" — the 5523-W runs a different HiSilicon/Anyka build). Only 80 and 554 answer on
`.169`.

---

## 3. The definitive mechanism

1. The 401 is **not** a username/password verdict — it is a **ticket gate**: `$.Auth.ticket`
   must be set, and it is set **only** by a successful cloud check-in.
2. The cloud check-in **cannot succeed**: the account binding is dead server-side
   (`3004 "no user to push"`), so the camera never receives a grant.
3. Under MITM the camera **downgrades to the LITE 0x00 path** and never emits the FULL
   0x11 registration that would adopt our forged grant — `LITE>0 FULL=0` across all 12
   sessions.
4. Therefore no HTTP shape, no credential, no bypass, and no forged protocol exchange
   can flip the gate. 45,661 candidate passwords, full CGI fuzzing, backdoor port
   sweeps, ONVIF SetUser resets, and the elevated MITM are all exhausted and validated.

---

## 4. What still works (unaffected by the gates)

- `.227`: was enrolled via blank-password NetSDK and continuously recording (job
  `e38aee4c`) with live playback verified as of the prior session — **currently dark**
  (no HTTP/TCP response at 08:35Z), so recording is suspended until it returns.
- `.29`: snapshot job was running until it went dark.
- The unlock infrastructure is complete and battle-tested for the moment a real
  credential or physical access arrives:
  - grant replay validated against **51 real cloud pairs** (the `pairs` array of
    `scripts/eseecloud-real-grants.json`)
  - verify formulas confirmed (post_v2 + sts) with the fixed-salt caveat
  - sweep harness with resumable, fingerprint-guarded checkpoints
  - fleet monitor auto-resuming sweeps on camera return
  - ONVIF `admin:admin` scanner + CGI fuzzer wired into the service

---

## 5. Remaining routes (the honest list)

| Route | Feasibility | Notes |
|-------|-------------|-------|
| Operator-supplied credential | High | The documented, guaranteed path. Any valid web-plane credential unlocks immediately. |
| SD-card injection (firmware/`/user` payload) | Physical | Requires physical access to the camera's SD slot. |
| Factory reset | Physical | Clears the dead cloud binding; camera re-binds under a fresh account. |
| Real-cloud account takeover / re-bind | External | Requires control of the bound EseeCloud account (out of scope, no user is bound). |
| New firmware with different gate logic | Vendor | Out of our control. |

**No further software-only vector remains that has not been tried and evidenced.**
The campaign's tooling stays in place and the fleet monitor continues to watch for a
camera return or gate flip for the next hour; any future unlock starts from the
physical/credential routes above.
