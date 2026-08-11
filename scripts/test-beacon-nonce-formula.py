#!/usr/bin/env python3
"""test-beacon-nonce-formula.py — feed the leaked beacon nonce through the
EseeCloud verify/grant derivations.

The HDS/1.0 beacon (docs/reports/2026-08-10-beacon-payload-decode.md) carries a
40-hex (20-byte) `nonce=` that flipped exactly once, coincident with the
12:01-15:02 dialing window. This tool tests the hypothesis that the beacon
nonce is the cloud-session nonce the verify/grant formula consumes:

  confirmed post_v2 verify = MD5hex(UPPER(nonce) + eseeid + UPPER(rid) + salt)
  (oc_cal_verify @ 0x23ce00, salt "Japass^2>.j" / AWS "ds*aFjjK.^<1")
  sts variant            = MD5hex(UPPER(nonce) + rid + salt)   (no eseeid)

and the grant layout: abbccdde 12 [12:16]=counter [16:20]=pconv [32:36]=next.

Tests:
  A. verify-formula sweep: every (beacon-nonce variant, eseeid, rid, salt)
     MD5 compared against known verify hashes AND the registration counters /
     grant next-counters (as 8-hex).
  B. digest-slice sweep: every 4-byte window (LE+BE) of MD5/SHA1/SHA256 of a
     battery of (nonce, eseeid, rid, salt) combinations compared against every
     counter / next-counter / pconv in the 51-pair ground truth.
  C. SHA1-structure test: the beacon nonce is 40 hex = SHA1 digest length —
     is it SHA1(serial | eseeid | MAC | pconv | combos + salts)?
  D. nonce↔nonce relations: new == SHA1(old)? first-32 == MD5(candidate)?
     any captured /message/nonce match? any slice == pconv?

USAGE:
  python3 scripts/test-beacon-nonce-formula.py
Exit code 0 = all checks ran clean (findings printed, negative is a finding too).
"""

import hashlib
import itertools
import json
import struct
import sys

SALT_STANDARD = "Japass^2>.j"
SALT_AWS = "ds*aFjjK.^<1"
SALTS = [SALT_STANDARD, SALT_AWS]

BEACON_NONCES = {
    # era -> 40-hex value from the beacon payload decode
    "old (Aug 8 05:32 -> Aug 9 11:00)": "2ce8497827b90fa34600657493928156601214e9",
    "new (Aug 9 12:01 -> Aug 10 04:01)": "8671353c7ec2c76ae2d009cd1776445bdec5ff98",
}

# .169 eseeid (from serial JAZ7C34781620744) and .29 eseeid (JAZ7C34780038910)
ESEES = {"169": "4781620744", "29": "4780038910"}
# Real-cloud anchor rid + .169/.29 rids from the built-in validated pair table
RIDS = [
    "202608080509552747802",   # real-cloud anchor (05:08Z, .29)
    "202608090025083425434",   # run 8 (.169)
    "202608090026413962644",   # run 8 (.169)
    "202608082329030052613",   # run 7 (.29)
]

# Known-good (nonce, verify) references — real-cloud anchor + validated pairs
KNOWN_VERIFIES = {
    "bbeb66fc651e2d37a9c80ec56efaea1a": "0b93ccb64b348dee83f1c8e79e22e31b",  # anchor
    "898a5175a15bd5ff1e2e9d7bdfc4cdb0": "f201d7aad83ea62e2cf19b94fa06b704",  # .169
    "2b764fcc343a2b89acaeced915786385": "de5e83ea19253ecedcf745b62d052222",  # .169
    "1711ff544f64221ce06ecbbf596ca290": "5b575c1df6239410740f72e7c412fcc9",  # .29
}

# Ground truth: 51 registration->grant pairs from the REAL cloud
PAIRS_PATH = "scripts/eseecloud-real-grants.json"


def md5hex(b: bytes) -> str:
    return hashlib.md5(b).hexdigest()


def sha1hex(b: bytes) -> str:
    return hashlib.sha1(b).hexdigest()


def load_pairs():
    with open(PAIRS_PATH) as f:
        raw = json.load(f)
    pairs = raw["pairs"]
    counters = set()
    nexts = set()
    pconvs = set()
    regs, grants = [], []
    for p in pairs:
        reg = bytes.fromhex(p["reg_hex"])
        grant = bytes.fromhex(p["grant_hex"])
        regs.append(reg)
        grants.append(grant)
        counters.add(struct.unpack("<I", reg[12:16])[0])
        pconvs.add(struct.unpack("<I", reg[16:20])[0])
        nexts.add(struct.unpack("<I", grant[32:36])[0])
    return pairs, counters, nexts, pconvs, regs, grants


def nonce_variants(n: str):
    """The plausible ways the 40-hex beacon nonce could enter the 32-hex-slot
    formula: full 40, first 32, last 32, and the raw 20 bytes hex'd."""
    return {
        "full40": n,
        "first32": n[:32],
        "last32": n[32:],
        "raw20hex": bytes.fromhex(n).hex(),
    }


def test_a_verify_formula(counters, nexts):
    print("═" * 72)
    print("A. verify-formula sweep (post_v2 + sts) with beacon nonce variants")
    print("═" * 72)
    counter_hex = {f"{c:08x}" for c in counters}
    next_hex = {f"{c:08x}" for c in nexts}
    known = set(KNOWN_VERIFIES.values())
    found = []
    for era, n in BEACON_NONCES.items():
        for vname, nv in nonce_variants(n).items():
            for cam, eid in ESEES.items():
                for rid in RIDS:
                    for salt in SALTS:
                        # post_v2: MD5(UPPER(nonce)+eseeid+UPPER(rid)+salt)
                        h = md5hex((nv.upper() + eid + rid.upper() + salt).encode())
                        checks = {
                            "known-verify": h in known,
                            # Substring (not equality): an MD5 that EMBEDS the
                            # 8-hex counter/next would still be a production hit.
                            "counter-substr": any(c in h for c in counter_hex),
                            "next-substr": any(c in h for c in next_hex),
                        }
                        if any(checks.values()):
                            found.append((era, vname, cam, rid, salt, h, checks))
                        # sts: MD5(UPPER(nonce)+rid+salt)  (no eseeid)
                        h2 = md5hex((nv.upper() + rid.upper() + salt).encode())
                        if h2 in known or any(c in h2 for c in counter_hex) \
                                or any(c in h2 for c in next_hex):
                            found.append((era, vname, "STS", rid, salt, h2, "sts"))
    if found:
        print("  !! MATCHES:")
        for f in found:
            print(f"    {f}")
    else:
        print("  no MD5 of any (beacon-nonce variant, eseeid, rid, salt) "
              "matches a known verify, embeds a registration counter, or "
              "embeds a grant next.")
    return found


def test_b_digest_slices(counters, nexts, pconvs):
    print()
    print("═" * 72)
    print("B. digest-slice sweep: any 4-byte window of MD5/SHA1/SHA256 of")
    print("   (nonce, eseeid, rid, salt) combos == counter / next / pconv?")
    print("═" * 72)
    found = []
    for era, n in BEACON_NONCES.items():
        raw = bytes.fromhex(n)
        base_inputs = {
            "nonce-raw": raw,
            "nonce-ascii-upper": n.upper().encode(),
            "nonce-ascii-lower": n.lower().encode(),
            "nonce20+std": raw + SALT_STANDARD.encode(),
            "nonce20+aws": raw + SALT_AWS.encode(),
            "nonce40hex+std": n.encode() + SALT_STANDARD.encode(),
        }
        for cam, eid in ESEES.items():
            base_inputs[f"nonce20+{cam}eseeid"] = raw + eid.encode()
            base_inputs[f"nonce20+{cam}eseeid+std"] = raw + eid.encode() + SALT_STANDARD.encode()
            for rid in RIDS[:2]:
                base_inputs[f"nonce20+{cam}eseeid+{rid[:8]}"] = raw + eid.encode() + rid.encode()
                base_inputs[f"nonce20+{cam}eseeid+{rid[:8]}+std"] = (
                    raw + eid.encode() + rid.encode() + SALT_STANDARD.encode())
        for iname, ib in base_inputs.items():
            for aname, alg in (("md5", hashlib.md5), ("sha1", hashlib.sha1),
                               ("sha256", hashlib.sha256)):
                d = alg(ib).digest()
                for off in range(len(d) - 3):
                    for endian in ("<", ">"):
                        v = struct.unpack(endian + "I", d[off:off + 4])[0]
                        if v in counters:
                            found.append((era, iname, aname, off, endian, "COUNTER", hex(v)))
                        if v in nexts:
                            found.append((era, iname, aname, off, endian, "NEXT", hex(v)))
                        if v in pconvs:
                            found.append((era, iname, aname, off, endian, "PCONV", hex(v)))
    if found:
        print("  !! MATCHES:")
        for f in found:
            print(f"    {f}")
    else:
        print("  no 4-byte window (LE/BE) of any digest matches any ground-truth "
              "counter / next / pconv.")
    return found


def test_c_sha1_structure():
    print()
    print("═" * 72)
    print("C. SHA1-structure test (40-hex nonce == SHA1 digest of identity?)")
    print("═" * 72)
    serials = ["JAZ7C34781620744", "Z7C34781620744", "4781620744",
               "JAZ7C34780038910", "4780038910"]
    macs = ["9c:a3:a9:bc:6f:ec", "9ca3a9bc6fec"]
    pconvs = ["47816207", "02d99e0f", "0f9ed902", "47800389", "4560d902"]
    cands = list(serials) + list(macs) + list(pconvs)
    for a, b in itertools.product(serials + macs + pconvs, repeat=2):
        cands.append(a + b)
    # Binary encodings too: firmware hashes often run over packed bytes rather
    # than ASCII. .169 pconv 0x02D99E0F (LE 0f 9e d9 02), .29 0x02D96045.
    binary_cands = [
        struct.pack("<I", 0x02D99E0F), struct.pack("<I", 0x02D96045),
        b"JAZ7C34781620744", b"Z7C34781620744", b"4781620744",
        b"4780038910", bytes.fromhex("9ca3a9bc6fec"),
        struct.pack("<I", 0x02D99E0F) + b"4781620744",
    ]
    for c in cands:
        for suffix in ("", SALT_STANDARD, SALT_AWS):
            h = sha1hex((c + suffix).encode())
            for era, n in BEACON_NONCES.items():
                if h == n:
                    print(f"  !! SHA1 MATCH: {era} == SHA1({c!r}{'+salt' if suffix else ''})")
                    return True
    for bc in binary_cands:
        for suffix in (b"", SALT_STANDARD.encode(), SALT_AWS.encode()):
            h = sha1hex(bc + suffix)
            for era, n in BEACON_NONCES.items():
                if h == n:
                    print(f"  !! SHA1 MATCH: {era} == SHA1(binary {bc.hex()}{'+salt' if suffix else ''})")
                    return True
    print("  no SHA1 of serial/eseeid/MAC/pconv/combos(+salts) in ASCII or "
          "binary form equals either beacon nonce.")
    return False


def test_d_relations():
    print()
    print("═" * 72)
    print("D. nonce-nonce relations + pconv slice + captured-nonce match")
    print("═" * 72)
    old = bytes.fromhex(BEACON_NONCES["old (Aug 8 05:32 -> Aug 9 11:00)"])
    new = bytes.fromhex(BEACON_NONCES["new (Aug 9 12:01 -> Aug 10 04:01)"])
    found = []
    # new == SHA1(old) or old == SHA1(new) or variants
    for label, a, b in (("new=SHA1(old)", old, new), ("old=SHA1(new)", new, old)):
        if sha1hex(a) == b.hex():
            found.append(label)
    # first-32 of either nonce == a captured /message/nonce? (32-hex slots only;
    # the 8-hex tail cannot match a 32-hex nonce, so only the prefix is checked)
    captured_nonces = list(KNOWN_VERIFIES.keys())
    for era, n in BEACON_NONCES.items():
        if n[:32] in captured_nonces:
            found.append(f"{era}: first-32 slice matches a captured /message/nonce")
    # any 4-byte window of the raw nonce == pconv (.169 0x02d99e0f / .29 0x02d96045)?
    pconv_169 = 0x02D99E0F
    pconv_29 = 0x02D96045
    for era, n in BEACON_NONCES.items():
        raw = bytes.fromhex(n)
        for off in range(len(raw) - 3):
            for endian in ("<", ">"):
                v = struct.unpack(endian + "I", raw[off:off + 4])[0]
                if v in (pconv_169, pconv_29):
                    found.append(f"{era}: raw-nonce window @{off}{endian} == pconv {hex(v)}")
    if found:
        print("  !! RELATIONS:")
        for f in found:
            print(f"    {f}")
    else:
        print("  no new==SHA1(old) relation, no captured-nonce slice match, "
              "no pconv slice in either beacon nonce.")
    return found


def main():
    pairs, counters, nexts, pconvs, regs, grants = load_pairs()
    print(f"ground truth: {len(pairs)} real registration->grant pairs "
          f"({len(counters)} counters, {len(nexts)} nexts, {len(pconvs)} pconvs)")
    print(f"beacon nonces: {len(BEACON_NONCES)} eras")
    print(f"verify formula: MD5(UPPER(nonce)+eseeid+UPPER(rid)+salt) "
          f"salts={SALTS}")
    print()
    a = test_a_verify_formula(counters, nexts)
    b = test_b_digest_slices(counters, nexts, pconvs)
    c = test_c_sha1_structure()
    d = test_d_relations()
    print()
    print("═" * 72)
    print("VERDICT")
    print("═" * 72)
    print(f"  A. verify-formula sweep:  {'MATCHES (!!)' if a else 'negative'}")
    print(f"  B. digest-slice sweep:    {'MATCHES (!!)' if b else 'negative'}")
    print(f"  C. SHA1 structure:        {'MATCH (!!)' if c else 'negative'}")
    print(f"  D. nonce relations:       {'MATCHES (!!)' if d else 'negative'}")
    print()
    if a or b or c or d:
        print("  The beacon nonce DOES appear related to the verify/grant channel.")
        return 0
    print("  The beacon nonce does NOT produce the registration counter or grant")
    print("  bytes through any tested derivation — consistent with it being a")
    print("  LAN-advertisement session token, not the /message/ verify nonce.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
