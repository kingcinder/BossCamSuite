#!/usr/bin/env python3
"""beacon-payload-extract.py — dump + diff the cameras' 255.255.255.255 UDP
discovery-beacon payloads across all captured MITM pcaps.

The 760-byte(-ish) broadcasts from the fleet (src ports :8002 / :18002) are
the only camera-originated egress the dead-cloud campaign ever observed.
This tool extracts the raw UDP payloads (SLL/SLL2-aware, no scapy needed),
prints hex + ASCII, searches for embedded serial/eseeid/pconv/protocol
signatures, byte-diffs every emission against the first to expose constant
vs varying fields, and — across the whole fleet — builds a nonce timeline
that answers whether the 40-hex session-nonce flips are fleet-synchronized
or per-device.

Cameras are parameterized: a registry maps IP -> serial / eseeid tokens, so
the string scan and identity fields are correct for each unit. By default
ALL cameras are scanned; --cam selects one.

Usage:
  python3 scripts/beacon-payload-extract.py                # all cameras
  python3 scripts/beacon-payload-extract.py --cam 10.0.0.169
  python3 scripts/beacon-payload-extract.py --cam 10.0.0.29 --dump-session 20260808T053046Z
  python3 scripts/beacon-payload-extract.py [PCAP...]
    (default: every captures/eseecloud-mitm-*/capture.pcap)
"""

import argparse
import glob
import os
import re
import struct
import sys

BCAST = "255.255.255.255"

# ── camera registry ────────────────────────────────────────────────────────
# ip -> {label, serial (full JAZ form), eseeid, pconv (eseeid[:8] as int)}
CAM_REGISTRY = {
    "10.0.0.169": {"label": "169", "serial": "JAZ7C34781620744",
                   "eseeid": "4781620744"},
    "10.0.0.29": {"label": "29", "serial": "JAZ7C34780038910",
                  "eseeid": "4780038910"},
    "10.0.0.227": {"label": "227", "serial": "JAZ7C34781634738",
                   "eseeid": "4781634738"},
}


def cam_pconv(cam: dict) -> int:
    """pconv = int(eseeid[:8]) — the serial-derived uint32 (e.g. .169 -> 0x02d99e0f)."""
    return int(cam["eseeid"][:8])


def read_pcap(path):
    """Yield (epoch, linktype, raw_packet) for each record in a classic pcap."""
    with open(path, "rb") as f:
        gh = f.read(24)
        if len(gh) < 24:
            return
        magic = gh[:4]
        if magic == b"\xd4\xc3\xb2\xa1":  # little-endian
            endian = "<"
        elif magic == b"\xa1\xb2\xc3\xd4":  # big-endian
            endian = ">"
        else:
            return  # nanosecond pcap or pcapng: skip
        linktype = struct.unpack(endian + "I", gh[20:24])[0]
        while True:
            rh = f.read(16)
            if len(rh) < 16:
                return
            ts_sec, ts_usec, incl, _orig = struct.unpack(endian + "IIII", rh)
            data = f.read(incl)
            if len(data) < incl:
                return
            yield ts_sec + ts_usec / 1e6, linktype, data


def parse_packet(linktype, data):
    """Return (ip_src, ip_dst, proto, sport, dport, udp_payload) or None."""
    # --- link layer ---
    if linktype == 276:  # LINUX_SLL2: 20 bytes, proto at [0:2]
        proto = struct.unpack(">H", data[0:2])[0]
        off = 20
    elif linktype == 113:  # LINUX_SLL: 16 bytes, proto at [14:16]
        proto = struct.unpack(">H", data[14:16])[0]
        off = 16
    elif linktype == 1:  # Ethernet
        if len(data) < 14:
            return None
        proto = struct.unpack(">H", data[12:14])[0]
        off = 14
    else:
        return None
    if proto != 0x0800 or len(data) < off + 20:  # IPv4
        return None
    ip = data[off:]
    ihl = (ip[0] & 0x0F) * 4
    ip_proto = ip[9]
    src = ".".join(str(b) for b in ip[12:16])
    dst = ".".join(str(b) for b in ip[16:20])
    if ip_proto != 17 or len(ip) < ihl + 8:  # UDP
        return None
    udp = ip[ihl:]
    sport, dport, ulen = struct.unpack(">HHH", udp[0:6])
    payload = udp[8:ulen]  # ulen includes the 8-byte UDP header
    return src, dst, sport, dport, payload


def hexdump(b, width=16):
    lines = []
    for i in range(0, len(b), width):
        chunk = b[i : i + width]
        hexpart = " ".join(f"{c:02x}" for c in chunk)
        asc = "".join(chr(c) if 32 <= c < 127 else "." for c in chunk)
        lines.append(f"{i:04x}  {hexpart:<{width*3}}  |{asc}|")
    return "\n".join(lines)


def find_strings(b, min_len=4):
    """ASCII runs of >= min_len printable chars, with offsets."""
    out, cur, start = [], [], None
    for i, c in enumerate(b):
        if 32 <= c < 127:
            if start is None:
                start = i
            cur.append(chr(c))
        else:
            if cur and len(cur) >= min_len:
                out.append((start, "".join(cur)))
            cur, start = [], None
    if cur and len(cur) >= min_len:
        out.append((start, "".join(cur)))
    return out


def collect_emissions(paths, cam_ip):
    """All beacon packets (src==cam_ip, dst BCAST, sport 8002/18002) from pcaps."""
    emissions = []  # (session, epoch, sport, dport, payload)
    for p in paths:
        sess = os.path.basename(os.path.dirname(p)).replace("eseecloud-mitm-", "")
        for epoch, lt, data in read_pcap(p):
            r = parse_packet(lt, data)
            if not r:
                continue
            src, dst, sport, dport, pl = r
            if src == cam_ip and dst == BCAST and sport in (8002, 18002):
                emissions.append((sess, epoch, sport, dport, pl))
    return emissions


def group_emissions(emissions):
    """Group the :8002 + :18002 pair (fires ~8 ms apart) into one emission."""
    grouped = []
    for e in sorted(emissions, key=lambda x: x[1]):
        if grouped and e[1] - grouped[-1][0][1] < 2.0:
            grouped[-1].append(e)
        else:
            grouped.append([e])
    return grouped


def dump_emission(emission):
    first = emission[0]
    print(f"\n--- {first[0]} {first[1]:.6f} src:{first[2]} dst:{first[3]} "
          f"len={len(first[4])} ({len(emission)} packets) ---")
    for e in emission:
        print(f"  [{e[0]} {e[1]:.6f} src:{e[2]} dst:{e[3]} len={len(e[4])}]")
    print(hexdump(first[4]))


def scan_payload(payload, cam):
    """Print embedded strings + search for this camera's identity tokens."""
    strs = find_strings(payload)
    print(f"    @0x0000: {strs[0][1]!r} ..." if strs else "    (no ASCII strings)")
    for off, s in strs:
        print(f"    @0x{off:04x}: {s!r}")
    for token in (cam["serial"].encode(), cam["eseeid"].encode(),
                  b"d9ffcc", b"abbccdde"):
        idx = payload.find(token)
        print(f"    search {token!r}: {'FOUND @0x%04x' % idx if idx >= 0 else 'absent'}")
    m = re.search(rb"nonce=([0-9a-f]{40})", payload)
    print(f"    nonce: {m.group(1).decode() if m else 'ABSENT'}")


def byte_diff_all(grouped):
    """Byte-diff every emission vs the first captured (per camera)."""
    print("\n══════ byte-diff across emissions (vs first captured) ══════")
    base = grouped[0][0]
    base_p = base[4]
    print(f"baseline: [{base[0]} {base[1]:.6f} src:{base[2]}] len={len(base_p)}")
    for g in grouped:
        e = g[0]
        p = e[4]
        n = min(len(base_p), len(p))
        diffs = [i for i in range(n) if base_p[i] != p[i]]
        if len(base_p) != len(p):
            print(f"[{e[0]} {e[1]:.6f} src:{e[2]}] LEN DIFF base={len(base_p)} "
                  f"this={len(p)}")
        elif not diffs:
            print(f"[{e[0]} {e[1]:.6f} src:{e[2]}] IDENTICAL to baseline")
        else:
            ranges = []
            for i in diffs:
                if ranges and i == ranges[-1][-1] + 1:
                    ranges[-1].append(i)
                else:
                    ranges.append([i])
            summary = ", ".join(
                f"{r[0]:04x}-{r[-1]:04x}({len(r)}B)" if len(r) > 1 else f"{r[0]:04x}"
                for r in ranges
            )
            print(f"[{e[0]} {e[1]:.6f} src:{e[2]}] {len(diffs)} diff bytes: {summary}")
            for r in ranges[:4]:
                i = r[0]
                print(f"      @0x{i:04x} base={base_p[i]:02x} this={p[i]:02x}")


def nonce_of(payload):
    m = re.search(rb"nonce=([0-9a-f]{40})", payload)
    return m.group(1).decode() if m else None


def fleet_nonce_timeline(cam_results):
    """Per-camera nonce over time + synchronized-vs-per-device verdict.

    cam_results: {ip: (label, grouped_emissions)}. For each emission we
    record (epoch, nonce). A camera 'flips' when its nonce changes between
    consecutive emissions. Verdict logic:
      * if only ONE camera ever flips (or flips are at different wall-clock
        moments with the others emitting simultaneously on a different
        nonce) -> per-device.
      * if two+ cameras flip at the same moment -> fleet-synchronized.
    """
    print("\n" + "═" * 72)
    print("FLEET NONCE TIMELINE (40-hex session nonce per emission)")
    print("═" * 72)
    per_cam = {}
    for ip, (label, grouped) in cam_results.items():
        per_cam[ip] = []
        for g in grouped:
            e = g[0]
            n = nonce_of(e[4])
            if n:
                per_cam[ip].append((e[1], n, e[0]))
    # union of all emission epochs for a time-ordered table; bucket emissions
    # from different cameras that land within <1.0s of the previous bucket
    # anchor (the :8002+:18002 pair fires ~8ms apart per camera, and two
    # cameras can fire in the same window) into ONE row so the wall-clock is
    # readable. Anchor-based (compare against the last APPENDED epoch, not the
    # previous raw epoch) so a 0/0.99/1.98s chain merges correctly.
    raw_epochs = sorted({e[0] for rows in per_cam.values() for e in rows})
    all_epochs = []
    for ep in raw_epochs:
        if not all_epochs or ep - all_epochs[-1] >= 1.0:
            all_epochs.append(ep)
    # Column headers are the camera LABELS (cam_results[ip][0]), not each
    # camera's first-emission epoch.
    header = f"{'epoch':>14}  " + "  ".join(
        f"{cam_results[ip][0]:>7}" for ip in cam_results)
    print(header)
    flips = {ip: [] for ip in cam_results}
    prev = {ip: None for ip in cam_results}
    for ep in all_epochs:
        row = f"{ep:>14.3f}  "
        for ip in cam_results:
            val = next((n for (t, n, _s) in per_cam[ip] if abs(t - ep) < 1.0), None)
            if val is None:
                row += f"{'-':>7}  "
            else:
                mark = ""
                if prev[ip] is not None and prev[ip] != val:
                    mark = " ←FLIP"
                    flips[ip].append(ep)
                row += f"{val[:7]}{mark:>7}  "
                prev[ip] = val
        print(row)
    print()
    # verdict
    n_cams = len(cam_results)
    flip_cams = {ip for ip, fl in flips.items() if fl}
    if not flip_cams:
        print(f"verdict: no nonce flips observed in any of the {n_cams} cameras "
              f"during their captured emission windows")
    elif len(flip_cams) == 1:
        ip = next(iter(flip_cams))
        label = cam_results[ip][0]
        print(f"verdict: only {label} ({ip}) flipped its nonce "
              f"({len(flips[ip])} flip(s) at {[f'{t:.0f}' for t in flips[ip]]}); "
              f"the other camera(s) held constant nonces across their windows — "
              f"flips are PER-DEVICE, not fleet-synchronized")
    else:
        # multiple cameras flipped — check simultaneity
        sync = all(len(flips[a]) == len(flips[b]) and
                   all(abs(x - y) < 60 for x, y in zip(flips[a], flips[b]))
                   for a in flip_cams for b in flip_cams)
        print(f"verdict: {len(flip_cams)} cameras flipped nonce; "
              f"{'flips are FLEET-SYNCHRONIZED' if sync else 'flips are NOT '
               'simultaneous — per-device timing'}")


def process_camera(cam_ip, paths, dump_sessions, show_hexdump=True,
                   show_strings=True, show_diff=True):
    cam = CAM_REGISTRY.get(cam_ip)
    if not cam:
        print(f"WARNING: {cam_ip} not in registry — scanning without identity "
              f"tokens ({sorted(CAM_REGISTRY)} available)")
        cam = {"label": cam_ip, "serial": "", "eseeid": ""}
    label = cam["label"]
    emissions = collect_emissions(paths, cam_ip)
    if not emissions:
        print(f"\n[{label} {cam_ip}] no beacons found")
        return None
    grouped = group_emissions(emissions)
    print(f"\n[{label} {cam_ip}] beacon packets: {len(emissions)} | "
          f"emissions: {len(grouped)} | pconv: 0x{cam_pconv(cam):08x}")
    lens = sorted({len(e[4]) for e in emissions})
    print(f"  payload lengths: {lens}")
    # hexdump: requested sessions, else the first emission
    if show_hexdump:
        done = set()
        for g in grouped:
            first = g[0]
            if (not dump_sessions) or any(
                    s in first[0] for s in dump_sessions):
                if first[0] in done:
                    continue
                print(f"\n══════ {label} emission @ {first[1]:.6f} "
                      f"({len(g)} packets) ══════")
                dump_emission(g)
                done.add(first[0])
                if not dump_sessions:
                    break
    if show_strings:
        print(f"\n══════ {label} embedded ASCII strings (first emission) ══════")
        scan_payload(grouped[0][0][4], cam)
    if show_diff and len(grouped) > 1:
        byte_diff_all(grouped)
    return label, grouped


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pcaps", nargs="*",
                    help="explicit pcaps (default: every "
                         "captures/eseecloud-mitm-*/capture.pcap)")
    ap.add_argument("--cam", action="append", default=[],
                    help="camera IP to scan (repeatable; default: all registry "
                         "cameras present in the pcaps)")
    ap.add_argument("--dump-session", action="append", default=[],
                    help="session ts to hexdump (repeatable; default: first "
                         "emission per camera)")
    ap.add_argument("--no-hexdump", action="store_true",
                    help="skip the hex dump sections (timeline-only)")
    ap.add_argument("--no-strings", action="store_true",
                    help="skip the ASCII string scan")
    ap.add_argument("--no-diff", action="store_true",
                    help="skip the byte-diff sections")
    args = ap.parse_args()

    paths = args.pcaps or sorted(
        glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..",
                               "captures", "eseecloud-mitm-*", "capture.pcap")))
    if not paths:
        print("no pcaps found")
        return 1

    # which cameras to scan: explicit --cam list, else all registry cameras
    # that actually have beacons in the corpus
    if args.cam:
        cam_ips = args.cam
    else:
        cam_ips = []
        for ip in CAM_REGISTRY:
            if collect_emissions(paths, ip):
                cam_ips.append(ip)

    cam_results = {}
    for ip in cam_ips:
        r = process_camera(ip, paths, args.dump_session,
                           show_hexdump=not args.no_hexdump,
                           show_strings=not args.no_strings,
                           show_diff=not args.no_diff)
        if r:
            cam_results[ip] = r

    if len(cam_results) > 1:
        fleet_nonce_timeline(cam_results)
    elif len(cam_results) == 1:
        print("\n(single camera — fleet nonce timeline needs >= 2 cameras)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
