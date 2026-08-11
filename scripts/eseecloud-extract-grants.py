#!/usr/bin/env python3
"""eseecloud-extract-grants.py — pull REAL server grants out of a capture pcap.

Ground-truth generator for eseecloud-replay-test.py. Parses a raw pcap
(Ethernet / Linux-cooked / RAW link layers, IPv4 TCP only), reassembles each
TCP connection's payload in sequence order, unmasks WebSocket frames using the
exact same parser the live ws-server uses, and pairs each FULL abbccdde 11
registration the camera sent to the REAL cloud server with the server's
100-byte abbccdde 12 grant that echoes the same counter+pconv.

The paired (registration, grant) hex pairs are the byte-accurate reference
that eseecloud-replay-test.py replays through our CheckinReplay builder to
verify our grant matches the real server's before the next live run.

Usage:
  python3 scripts/eseecloud-extract-grants.py \\
      captures/eseecloud-mitm-20260808T050802Z/capture.pcap \\
      captures/eseecloud-mitm-20260808T053046Z/capture.pcap \\
      --out scripts/eseecloud-real-grants.json
"""

import argparse
import importlib.util
import json
import struct
import sys
from pathlib import Path

WS_SERVER = Path(__file__).resolve().parent / "eseecloud-ws-server.py"
_spec = importlib.util.spec_from_file_location("eseecloud_ws_server", WS_SERVER)
ws = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ws)
parse_ws_frame = ws.parse_ws_frame
ABBCCDDE = ws.ABBCCDDE_MAGIC


def read_pcap(path: Path):
    """Yield (timestamp, linktype, packet_data) records from a classic pcap."""
    with open(path, "rb") as f:
        magic = f.read(4)
        if magic in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1"):
            endian = "<"
        elif magic in (b"\xa1\xb2\xc3\xd4", b"\xa1\xb2\x3c\x4d"):
            endian = ">"
        else:
            sys.exit(f"{path.name}: not a classic pcap (magic {magic.hex()})")
        # Global header is 24 bytes total: magic(4) + version(4) + thiszone(4)
        # + sigfigs(4) + snaplen(4) + network(4). After the magic there are
        # exactly 20 more bytes before the first record — any extra read here
        # silently shifts every record header by 4 bytes (the "linktype" then
        # reads as the first record's ts_sec, as happened in development).
        f.read(16)  # version major/minor, thiszone, sigfigs, snaplen
        net = struct.unpack(endian + "I", f.read(4))[0]
        while True:
            hdr = f.read(16)
            if len(hdr) < 16:
                break
            ts_sec, ts_usec, incl, _orig = struct.unpack(endian + "IIII", hdr)
            data = f.read(incl)
            if len(data) < incl:
                break
            yield ts_sec + ts_usec / 1e6, net, data


def link_to_ip(net: int, data: bytes):
    """Strip the link-layer header, returning the IPv4 packet or None."""
    if net == 1:  # ethernet
        if len(data) < 14:
            return None
        etype = struct.unpack(">H", data[12:14])[0]
        off = 14
        if etype == 0x8100:  # VLAN tag
            if len(data) < 18:
                return None
            etype = struct.unpack(">H", data[16:18])[0]
            off = 18
        return data[off:] if etype == 0x0800 else None
    if net == 101:  # RAW IP
        return data
    if net == 113:  # Linux cooked (tcpdump -i any)
        if len(data) < 16:
            return None
        proto = struct.unpack(">H", data[14:16])[0]
        off = 16
        if proto == 0x8100:
            if len(data) < 20:
                return None
            proto = struct.unpack(">H", data[18:20])[0]
            off = 20
        return data[off:] if proto == 0x0800 else None
    if net == 276:  # Linux cooked v2 / SLL2 (modern tcpdump -i any)
        if len(data) < 20:
            return None
        proto = struct.unpack(">H", data[0:2])[0]
        if proto == 0x8100:
            if len(data) < 24:
                return None
            proto = struct.unpack(">H", data[22:24])[0]
            return data[24:] if proto == 0x0800 else None
        return data[20:] if proto == 0x0800 else None
    return None


def ip_tcp(data: bytes):
    """Parse IPv4+TCP, returning (src_ip, src_port, dst_ip, dst_port, seq, body)."""
    if len(data) < 20:
        return None
    if (data[0] >> 4) != 4:
        return None  # IPv4 only (cloud/camera traffic is v4)
    ihl = (data[0] & 0x0F) * 4
    if ihl < 20:
        return None
    frag = struct.unpack(">H", data[6:8])[0]
    if frag & 0x1FFF:  # non-initial fragment: no payload reassembly here
        return None
    if data[9] != 6:  # TCP
        return None
    total_len = struct.unpack(">H", data[2:4])[0]
    end = min(total_len, len(data)) if total_len > ihl else len(data)
    tcp = data[ihl:end]
    if len(tcp) < 20:
        return None
    sport, dport = struct.unpack(">HH", tcp[0:4])
    seq = struct.unpack(">I", tcp[4:8])[0]
    doff = (tcp[12] >> 4) * 4
    if doff < 20 or doff > len(tcp):
        return None
    src = ".".join(str(b) for b in data[12:16])
    dst = ".".join(str(b) for b in data[16:20])
    return src, sport, dst, dport, seq, tcp[doff:]


MAX_GAP = 1 << 20  # 1 MiB — anything larger is a capture gap, not real data


def reassemble(segs):
    """Concatenate (seq, body) TCP segments into one ordered byte stream,
    skipping duplicate/overlapping bytes. Gaps larger than MAX_GAP are treated
    as capture losses (not zero-filled — a real sequence gap here would mean
    the stream is unusable anyway, and zero-filling a 4 GiB wrap would hang)."""
    out = bytearray()
    expect = None
    for seq, body in sorted(segs):
        if expect is None:
            out += body
            expect = seq + len(body)
            continue
        if seq > expect and (seq - expect) <= MAX_GAP:
            out += b"\x00" * (seq - expect)
            expect = seq
        if seq <= expect and expect - seq < len(body):
            start = expect - seq
            out += body[start:]
            expect = seq + len(body)
        # Intentional: after a >MAX_GAP gap, `expect` never advances, so every
        # later segment is also dropped — a stream with a capture gap that big
        # is unusable anyway, and keeping the first half costs nothing.
    return bytes(out)


def ws_frames(stream: bytes):
    """Yield every binary/text WebSocket payload in a reassembled stream."""
    pos = 0
    while pos < len(stream):
        opcode, payload, consumed, err = parse_ws_frame(stream[pos:])
        if opcode is None or consumed <= 0:
            break  # partial/truncated frame at stream end
        pos += consumed
        if opcode in (0x1, 0x2):
            yield payload


def extract(pcap_paths):
    """Return {reg: {..}, grants: [...]} for all matched pairs across pcaps."""
    regs, grants = [], []
    for idx, p in enumerate(pcap_paths):
        streams = {}
        for _ts, net, data in read_pcap(Path(p)):
            ip = link_to_ip(net, data)
            if ip is None:
                continue
            tcp = ip_tcp(ip)
            if tcp is None:
                continue
            src, sport, dst, dport, seq, body = tcp
            if not body:
                continue
            key = (src, sport, dst, dport)
            streams.setdefault(key, []).append((seq, body))
        # Only :19000 connections carry the abbccdde check-in frames; skipping
        # everything else keeps reassembly fast and avoids pathological HTTP
        # streams (huge seq gaps) that would make the run crawl or hang.
        for (src, sp, dst, dp), segs in streams.items():
            if sp != 19000 and dp != 19000:
                continue
            for payload in ws_frames(reassemble(segs)):
                if len(payload) < 4 or payload[:4] != ABBCCDDE:
                    continue
                if len(payload) < 5:
                    continue
                cmd = payload[4]
                if cmd == 0x11 and len(payload) >= 32:  # FULL registration
                    regs.append({"src": src, "dst": dst,
                                 "counter": payload[12:16].hex(),
                                 "pconv": payload[16:20].hex(),
                                 "pcap": idx,
                                 "hex": payload.hex()})
                elif cmd == 0x12 and len(payload) == 100:  # server grant
                    grants.append({"src": src, "dst": dst,
                                   "counter": payload[12:16].hex(),
                                   "pconv": payload[16:20].hex(),
                                   "next": payload[32:36].hex(),
                                   "pcap": idx,
                                   "hex": payload.hex()})
    # Pair grants to registrations by counter+pconv echo. Prefer a same-pcap,
    # same-connection registration (cross-pcap counter collisions are
    # essentially impossible since the counter advances every check-in, but
    # scoping by pcap index removes the class entirely).
    paired = []
    for g in grants:
        match = None
        for r in regs:
            if r["counter"] == g["counter"] and r["pconv"] == g["pconv"]:
                match = r
                if r["pcap"] == g["pcap"] and r["src"] == g["dst"] \
                        and r["dst"] == g["src"]:
                    break  # same session AND connection — best possible match
        if match is None:
            continue
        # counter/next are stored as the RAW wire bytes at [12:16]/[32:36],
        # which are little-endian u32 (verified: real deltas cluster
        # ~0x1390..0x13B2 per ~10s check-in, matching our cadence model).
        # Computing the delta from the raw big-endian ints would produce
        # meaningless huge values — decode as LE.
        g_ctr = struct.unpack("<I", bytes.fromhex(g["counter"]))[0]
        g_next = struct.unpack("<I", bytes.fromhex(g["next"]))[0]
        delta = (g_next - g_ctr) & 0xFFFFFFFF
        paired.append({
            "reg_hex": match["hex"],
            "grant_hex": g["hex"],
            "counter": g["counter"],
            "pconv": g["pconv"],
            "next_counter": g["next"],
            "delta_next": delta,
        })
    return paired


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("pcaps", nargs="+", help="capture.pcap file(s)")
    ap.add_argument("--out", default="", help="write JSON to this file (default stdout)")
    args = ap.parse_args()
    pairs = extract([Path(p) for p in args.pcaps])
    report = {
        # Session directory (parent of capture.pcap) so provenance survives:
        # two sessions both named capture.pcap would otherwise be indistinguishable.
        "source": [str(Path(p).parent.name) for p in args.pcaps],
        "pairs": pairs,
    }
    if args.out:
        with open(args.out, "w") as f:
            json.dump(report, f, indent=2)
        print(f"wrote {len(pairs)} registration->grant pairs to {args.out}")
    else:
        print(json.dumps(report, indent=2))
    print(f"  delta_next values: "
          f"{sorted({p['delta_next'] for p in pairs})}")


if __name__ == "__main__":
    main()
