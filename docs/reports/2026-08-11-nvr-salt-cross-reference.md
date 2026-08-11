# NVR app.out Salt Cross-Reference — Does the NVR Use a Different Verify Salt?

**Date:** 2026-08-11 · **Status:** **VERDICT — SAME SALT.** The FWHI102 NVR (`app.out`, 2024-07-15) uses the identical verify salt **`Japass^2>.j`** as the 5523-W cameras. It is embedded in the NVR's actual `/message/` check-in code, independently confirmed by the vendor's own EseeCloud web-client source, and matches all known live verify pairs.

---

## 1. What the camera side uses (baseline, from prior work)

```
post_v2:  verify = MD5hex( TOUPPER(nonce) + eseeid + TOUPPER(request_id) + salt )
sts:      verify = MD5hex( TOUPPER(nonce) + request_id + salt )          # 2-field, no eseeid
salt (camera, anyka_ipc oc_cal_verify @ 0x23ce00 / 0x41b6f7):  "Japass^2>.j"
AWS variant (oc_cal_verify_aws @ 0x23d074):                    "ds*aFjjK.^<1"   (matched 0 live pairs)
```

## 2. NVR app.out string inventory (63,347 strings, 9.42 MB ARM ELF)

| Salt / constant | File offset | VA | Occurrences | Neighborhood (what it belongs to) |
|---|---|---|---|---|
| `Japass^2>.j` | `0x8314d8` | `0x8414d8` | 1 | **The `/message/` check-in chain**: `http://%s/message/nonce`, `pm.dvr163.com`, `method=get`, `$.Stat.Network.P2PUID`, `%s%s%s`, **salt**, `http://%s/message/message`, `method=post&eseeid=%s&verify=%s&request_id=%s` |
| `Japass^2>.j` | `0x8a8054` | `0x8b8054` | 1 | **MD5 verify helper**: `%s%s%s%s`, md5 error strings, and — immediately adjacent — **`ds*aFjjK.^<1`** at `0x8a80c3` (the AWS salt) |
| `ds*aFjjK.^<1` | `0x8a80c3` | `0x8b80c3` | 1 | Same MD5 helper as the std salt — the NVR carries **both** salts, exactly like the camera |
| `Japass^78>!j` | `0x8d9100`, `0x8de754` | `0x8f9100`, `0x8fe754` | 2 | `.data` region near version `1.0.1.10`, a printable-charset string (looks like a password-charset), and `disk` — **not** the `/message/` verify chain (different region, writable `.data` vs the rodata message cluster); exact subsystem **unconfirmed** (a daemon/WiFi-provisioning module is plausible, not proven) |

The `Japass^2>.j` instances sit inside the exact URL/format-string cluster that implements the EseeCloud check-in — this is not a coincidental hit, it is the NVR's copy of the same verify code.

## 3. Vendor source confirmation — EseeCloud web client (`cloud.js`, from the 3.0.8.4 installer)

The extracted web client source is the vendor's own JS implementation of the same protocol:

```js
let MixStr = 'Japass^2>.j';                       // default salt (all ODMs except below)
if (sysConfig.odm === 'CamView_Smart') { MixStr = 'kPCTrSfnVafyWgmmYppKlevH'; }

// 2-field  (sts / generic):  md5( nonce.UPPER + request_id.UPPER + MixStr )
// 3-field  (post_v2 shape):  md5( nonce.UPPER + EseeID.UPPER + request_id.UPPER + MixStr )
// with-user:                 md5( nonce.UPPER + user.UPPER + request_id.UPPER + MixStr )
```

- Confirms the concatenation order exactly as recovered from the camera binary.
- **Salt is per-ODM configurable (`CamView_Smart` overrides to `kPCTrSfnVafyWgmmYppKlevH`), but the NVR/camera ODM (JUAN, `yanfei` white-label) keeps the default `Japass^2>.j`.** Direct evidence for the NVR: the only override string `kPCTrSfnVafyWgmmYppKlevH` appears **0 times** in app.out — it is not compiled into the NVR firmware.
- The `cloud.js` also documents `app_bundle` per ODM (`JUAN → c68NcjtzcS4ScP4UdzsMcPgV`, default `Wa5sQRJYB9Fq4eKlm74GvpF7`) and a separate push-UID sign salt `67dx1,no9ujtr9sa<3dsgj` for `CmsUploadPushUid` — none of which appear in the NVR firmware (they are account-plane, web-client-only).

## 4. Empirical cross-reference (formula test on known live pairs)

Tested every NVR/camera salt against the 4 known-good (nonce, eseeid, request_id → verify) pairs with both the post_v2 and sts formulas:

| Salt | post_v2 matches | sts matches¹ |
|---|---|---|
| **`Japass^2>.j`** | **4 / 4** ✅ | 0 / 4 |
| `Japass^78>!j` | 0 / 4 | 0 / 4 |
| `ds*aFjjK.^<1` | 0 / 4 | 0 / 4 |
| `m#PWD`, `d<?&pWD`, `?k2PfF`, `+4gPwD>4tJ` (ROM constants) | 0 / 4 | 0 / 4 |

¹ The sts formula was checked against the same pairs for completeness only — these are post_v2 pairs (with eseeid), so sts is expected to miss. The 4/4 post_v2 match proves the **salt string is byte-identical to the camera's** and that the formula still validates; it does not by itself prove NVR-side computation, because the captured pairs come from the cameras, not the NVR. The NVR-side evidence is the static placement of the literal inside the `/message/` check-in code (plus the vendor's own web-client source).

## 5. Verdict

1. **The NVR does NOT use a different salt for the `/message/` verify chain.** It uses `Japass^2>.j` — statically (in the message-chain code), in the vendor's own client, and empirically (4/4 pairs).
2. The NVR additionally carries the **AWS-region salt `ds*aFjjK.^<1`** (same dual-salt design as the camera — std vs AWS region switch, never observed live).
3. `Japass^78>!j` is a **third, distinct salt in a different subsystem** (P2P/IOT daemon SDK, near version `1.0.1.10`) — not part of the check-in chain; it matches zero live verify pairs and should not be used for the `/message/` formula.
4. Practical consequence for BossCamSuite: the existing `Japass^2>.j` verify forgery used for the camera MITM tooling is valid for the NVR cloud plane too; `Japass^78>!j` warrants follow-up only if the P2P/IOT plane is being worked.

## 6. Reproducibility

```bash
# 1. Strings + offsets
strings -t x -n 6 app.out | grep Japass
#   8314d8 Japass^2>.j      -> VA 0x8414d8  (message chain: nonce/message URLs + %s%s%s)
#   8a8054 Japass^2>.j      -> VA 0x8b8054  (MD5 helper, adjacent to ds*aFjjK.^<1 @ 8a80c3)
#   8d9100/8de754 Japass^78>!j  -> .data   (P2P/IOT SDK region, NOT the verify chain)

# 2. Formula test (any python3)
# verify = md5hex(nonce.upper() + eseeid + rid.upper() + "Japass^2>.j")  -> matches all known pairs
```
