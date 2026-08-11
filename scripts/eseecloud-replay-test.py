#!/usr/bin/env python3
"""eseecloud-replay-test.py — replay REAL cloud grants through our builder.

Ground truth: scripts/eseecloud-real-grants.json, containing 51 (registration,
grant) hex pairs extracted by eseecloud-extract-grants.py from captures of the
5523-W cameras talking to the REAL cloud P2P servers (172.235.43.92:19000 /
129.153.101.14:19000, sessions 20260808T050802Z/053046Z).

For every pair this script:
  1. Feeds the exact 128-byte abbccdde 11 registration the camera sent into
     CheckinReplay — the SAME state machine eseecloud-ws-server.py runs live
     in --reply-mode replay.
  2. Compares the generated 100-byte abbccdde 12 grant against the real
     server's grant byte-for-byte, reporting every differing offset.

Acceptance semantics: the static layout (magic, cmd, echoed counter/pconv,
0x44 marker, trailing zeros) MUST be byte-identical to the real server's — that
is the part the camera's parser validates. The next-counter [32:36] is the one
field that legitimately varies: the real server derives it from its clock, and
the delta is PER-CAMERA — pconv 0x02d96045 (the 10.0.0.29 unit) advances
~0x13A0 per ~10s check-in while pconv 0x02d99e0f (the 10.0.0.169 unit) advances
~0x15A0, each jittering with the actual check-in interval. A fleet-wide
constant cannot serve both cameras (the old fixed 0x13A0 cadence failed the
.169 pairs), so the builder uses a calibrated per-pconv cadence seeded from the
ground-truth median and refined live by the ws-server's cadence learning.

Judging:
  * cadence (default): our emitted delta for each pair must land INSIDE that
    pconv's OWN observed delta range — a +1 grant was never adopted under MITM,
    and a delta outside the camera's rate-derived band is equally implausible.
  * strict: additionally require our delta to equal the pconv's calibrated
    (median) cadence byte-for-byte — a deterministic check that the builder
    applies the calibration exactly (guards against a cadence regression that
    still happens to fall inside the range).

Usage:
  python3 scripts/eseecloud-replay-test.py
  python3 scripts/eseecloud-replay-test.py --next-counter plus1
  python3 scripts/eseecloud-replay-test.py --strict
Exit code 0 = static layout matches for every pair (and cadence/median rules
hold), 1 otherwise.
"""

import argparse
import importlib.util
import json
import statistics
import struct
import sys
from collections import defaultdict
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
WS_SERVER = SCRIPT_DIR / "eseecloud-ws-server.py"
DEFAULT_PAIRS = SCRIPT_DIR / "eseecloud-real-grants.json"

_spec = importlib.util.spec_from_file_location("eseecloud_ws_server", WS_SERVER)
ws = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ws)
CheckinReplay = ws.CheckinReplay

STATIC_FIELDS = [
    ("magic+cmd [0:12]", slice(0, 12)),
    ("counter echo [12:16]", slice(12, 16)),
    ("pconv echo [16:20]", slice(16, 20)),
    ("zeros [20:28]", slice(20, 28)),
    ("0x44 marker [28:32]", slice(28, 32)),
    ("zeros [36:100]", slice(36, 100)),
]
NEXT_SLICE = slice(32, 36)


def pconv_int(p: dict) -> int:
    """Decode a pair's pconv (stored as LE-byte hex) to its uint32 value."""
    return struct.unpack("<I", bytes.fromhex(p["pconv"]))[0]


def pair_delta(p: dict) -> int:
    """The real server's next-counter delta for a pair (counter [12:16] -> next [32:36])."""
    ctr = struct.unpack("<I", bytes.fromhex(p["reg_hex"])[12:16])[0]
    nxt = struct.unpack("<I", bytes.fromhex(p["grant_hex"])[32:36])[0]
    return (nxt - ctr) & 0xFFFFFFFF


def per_pconv_stats(pairs: list) -> dict:
    """Group observed next-counter deltas by pconv.

    Returns {pconv_int: (lo, hi, median)}. lo/hi bound that camera's plausible
    per-check-in advance; the median is its calibrated cadence (what the builder
    and the live server's cadence learning converge on).
    """
    by = defaultdict(list)
    for p in pairs:
        by[pconv_int(p)].append(pair_delta(p))
    stats = {}
    for pc, ds in by.items():
        ds.sort()
        stats[pc] = (ds[0], ds[-1], int(statistics.median(ds)))
    return stats


def diff_offsets(got: bytes, real: bytes) -> list:
    return [i for i in range(len(real)) if i >= len(got) or got[i] != real[i]]


def verify_real_grant(grant_hex: str) -> str:
    """Sanity-check a REAL grant against the decoded layout invariants."""
    g = bytes.fromhex(grant_hex)
    issues = []
    if g[:4] != ws.ABBCCDDE_MAGIC:
        issues.append("magic != abbccdde")
    if g[4] != 0x12:
        issues.append("cmd != 0x12")
    if g[5:10] != b"\x00" * 5:
        issues.append("bytes [5:10] not zero")
    if g[10:12] != b"\x00\x01":
        issues.append("[10:12] != 0001")
    if g[20:28] != b"\x00" * 8:
        issues.append("[20:28] not zero")
    if g[28] != 0x44:
        issues.append("[28] != 0x44")
    if g[29:32] != b"\x00\x00\x00":
        issues.append("[29:32] not zero")
    if g[36:] != b"\x00" * 64:
        issues.append("tail [36:100] not zero")
    return "; ".join(issues) if issues else "OK"


def adoption_stats(pairs: list) -> tuple:
    """Count how many consecutive same-pconv pairs show the camera adopting
    the server's granted next-counter (the acceptance signal)."""
    by_pconv = {}
    for p in pairs:
        by_pconv.setdefault(pconv_int(p), []).append(p)
    adopted = total = 0
    for pc, ps in by_pconv.items():
        prev_next = None
        for p in ps:
            if prev_next is not None:
                total += 1
                if p["counter"] == prev_next:
                    adopted += 1
            prev_next = p["next_counter"]
    return adopted, total


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pairs", default=str(DEFAULT_PAIRS),
                    help="ground-truth JSON (default: %(default)s)")
    ap.add_argument("--next-counter", choices=["cadence", "plus1"], default="cadence",
                    help="grant builder mode to test (default: %(default)s)")
    ap.add_argument("--strict", action="store_true",
                    help="additionally require our next-counter delta to equal the "
                         "pconv's calibrated (median) cadence byte-for-byte")
    args = ap.parse_args()

    pairs_path = Path(args.pairs)
    if not pairs_path.exists():
        print(f"FATAL: ground truth {pairs_path} not found. Generate it from a "
              f"capture first:\n"
              f"  python3 scripts/eseecloud-extract-grants.py "
              f"captures/<session>/capture.pcap --out {pairs_path.name}\n"
              f"(see eseecloud-extract-grants.py --help)", file=sys.stderr)
        sys.exit(1)
    report = json.loads(pairs_path.read_text())
    pairs = report["pairs"]
    print(f"ground truth: {pairs_path}  ({len(pairs)} pairs, source "
          f"{report.get('source')})")
    # An EMPTY ground truth must never be a green light: a wrong pcap, a
    # misparse, or a camera that never reached the real cloud would otherwise
    # silently "PASS" right when the operator most needs a red flag.
    if not pairs:
        print("FATAL: ground truth contains 0 registration->grant pairs — "
              "nothing verified. Check the capture/session before running the "
              "live MITM.", file=sys.stderr)
        sys.exit(1)
    print(f"builder mode: --next-counter {args.next_counter}  "
          f"{'(STRICT: next-counter must equal calibrated per-pconv cadence byte-for-byte)' if args.strict else ''}")
    print("=" * 72)

    # Sanity-check the ground truth itself against the decoded layout.
    for p in pairs:
        verdict = verify_real_grant(p["grant_hex"])
        if verdict != "OK":
            print(f"  !! ground-truth grant FAILS layout check: {p['grant_hex']}")
            print(f"     {verdict}")

    # Adoption-chain proof in the ground truth (camera uses server's next value).
    adopted, total = adoption_stats(pairs)
    # The misses are inherent NEW-CONN rows: after a disconnect the camera
    # re-dials with an independently advanced counter, so those registrations
    # legitimately differ from our last granted next.
    print(f"ground-truth adoption chain: {adopted}/{total} consecutive "
          f"same-pconv registrations used the server's granted next-counter "
          f"({'PROVEN' if total and adopted == total else 'partial (misses are NEW-CONN re-dials — expected)'})")

    # Per-camera cadence calibration from the ground truth: each pconv gets its
    # OWN plausible delta band [lo, hi] and calibrated median cadence. The
    # builder is fed these medians exactly as the live server seeds them.
    stats = per_pconv_stats(pairs)
    cadence_by_pconv = {pc: med for pc, (_, _, med) in stats.items()}
    print("\nper-camera cadence (from ground truth, median = calibrated):")
    for pc, (lo, hi, med) in sorted(stats.items()):
        print(f"  pconv 0x{pc:08x}: observed 0x{lo:04x}..0x{hi:04x} "
              f"({hi - lo + 1} span)  calibrated median 0x{med:04x}")

    # Cross-check the LIVE server's seed constants, not just the medians: the
    # ws-server seeds CALIBRATED_CADENCE (0x13A0/0x15A0) while this test feeds
    # the ground-truth medians (0x139c/0x15b1), so a drift of the production
    # constants outside a camera's observed band would otherwise slip through
    # a green suite and emit implausible grants live. Any calibrated pconv that
    # appears in the ground truth must sit inside its own observed band.
    calibrated_bad = []
    for pc, seed in sorted(ws.CALIBRATED_CADENCE.items()):
        if pc not in stats:
            continue  # calibrated for a camera not in this ground truth — fine
        lo, hi, _ = stats[pc]
        if not (lo <= seed <= hi):
            calibrated_bad.append((pc, seed, lo, hi))
    if calibrated_bad:
        for pc, seed, lo, hi in calibrated_bad:
            print(f"  !! CALIBRATED_CADENCE[0x{pc:08x}] = 0x{seed:04x} is "
                  f"OUTSIDE that pconv's observed band 0x{lo:04x}..0x{hi:04x} "
                  f"— the live server would grant implausible counters")
    else:
        print("live-seed check: every CALIBRATED_CADENCE entry present in the "
              "ground truth sits inside its pconv's observed band (OK)")

    static_pass = 0
    all_median_exact = True
    cadence_fail_pairs = 0
    print()
    for i, p in enumerate(pairs):
        reg = bytes.fromhex(p["reg_hex"])
        real = bytes.fromhex(p["grant_hex"])
        assert len(reg) == 128, f"pair {i}: registration is {len(reg)}B, want 128B"
        assert reg[:4] == ws.ABBCCDDE_MAGIC and reg[4] == 0x11, \
            f"pair {i}: registration is not an abbccdde 0x11 FULL form " \
            f"(magic={reg[:4].hex()} cmd={reg[4]:02x}) — fixture is corrupt"
        # Feed the builder the SAME calibrated per-pconv map the live server
        # seeds (so a regression to a fleet-wide constant is caught here too).
        replay = CheckinReplay(args.next_counter,
                               cadence_by_pconv=cadence_by_pconv)
        got = replay.next_reply(reg)
        assert got is not None, f"pair {i}: builder returned no reply"
        assert len(got) == 100, f"pair {i}: builder returned {len(got)}B, want 100B"
        assert got[4] == 0x12, f"pair {i}: builder returned cmd {got[4]:02x}"

        # 1) static layout must be byte-identical: every non-next-counter field
        # (STATIC_FIELDS) must match exactly; only [32:36] (time-derived) may
        # differ, and diff_offsets + the slice guard proves it.
        diffs = diff_offsets(got, real)
        # NOTE: slice objects do not support `in` — compare bounds explicitly.
        static_ok = all(got[s] == real[s] for _, s in STATIC_FIELDS) \
            and all(NEXT_SLICE.start <= i_ < NEXT_SLICE.stop for i_ in diffs)
        static_pass += static_ok

        # 2) next-counter analysis — judged PER-CAMERA, not against a global band.
        real_ctr = struct.unpack("<I", real[12:16])[0]
        real_next = struct.unpack("<I", real[32:36])[0]
        our_next = struct.unpack("<I", got[32:36])[0]
        real_delta = (real_next - real_ctr) & 0xFFFFFFFF
        our_delta = (our_next - real_ctr) & 0xFFFFFFFF
        pc = pconv_int(p)
        lo, hi, med = stats[pc]
        cadence_ok = lo <= our_delta <= hi
        median_exact = our_delta == med
        if not cadence_ok:
            cadence_fail_pairs += 1
        if not median_exact:
            all_median_exact = False

        if our_delta == real_delta:
            next_label = "exact"
        elif median_exact:
            next_label = "calib"
        elif cadence_ok:
            next_label = "in-range"
        else:
            next_label = "OUTSIDE"
        status = "PASS" if (static_ok and cadence_ok
                            and (not args.strict or median_exact)) else "FAIL"
        print(f"[{status}] pair {i:02d}  static={'OK' if static_ok else 'DIFF'}"
              f"  next={next_label}"
              f"  pconv=0x{pc:08x}"
              f"  real_delta=0x{real_delta:04x} our_delta=0x{our_delta:04x} "
              f"band=0x{lo:04x}..0x{hi:04x} med=0x{med:04x}")
        if not static_ok:
            print(f"    reg   : {p['reg_hex']}")
            print(f"    real  : {p['grant_hex']}")
            print(f"    ours  : {got.hex()}")
            print(f"    diff @: {[f'{o:02d}' for o in diffs]}")

    print("=" * 72)
    print(f"static layout byte-for-byte: {static_pass}/{len(pairs)} pairs match "
          f"(all fields except time-derived next-counter)")
    # The next-counter is time-derived on the real server (deltas jitter with
    # the actual check-in interval), so byte equality with the REAL value is
    # only a curiosity. What matters for acceptance is that our delta falls in
    # THAT camera's observed band — a grant outside it (or the +1 legacy mode)
    # was never adopted under MITM.
    print(f"per-camera cadence band: {len(pairs) - cadence_fail_pairs}/{len(pairs)} "
          f"pairs inside their pconv's observed band")
    ok = static_pass == len(pairs) and cadence_fail_pairs == 0 \
        and not calibrated_bad
    if args.strict:
        print(f"median-exact (strict): "
              f"{'ALL pairs emit the calibrated cadence' if all_median_exact else 'some pairs deviate (!!)'}")
        ok = ok and all_median_exact
    print(f"\nOVERALL: {'PASS' if ok else 'FAIL'}")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
