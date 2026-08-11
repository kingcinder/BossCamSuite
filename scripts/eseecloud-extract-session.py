#!/usr/bin/env python3
"""eseecloud-extract-session.py — full real-cloud session miner.

Reconstructs EVERY channel of a captured camera conversation — not just the
:19000 grant pairs — in chronological order with per-frame timestamps, so we
can answer the questions that the pair-extractor can't:

  * What did the REAL server send right before the camera's first FULL 0x11?
    (the trigger we have never reproduced under MITM)
  * Did the camera ever send LITE 0x00 to the real server, and what did the
    real server reply to it? (we currently blind-grant LITE — possibly a trap)
  * Is the next-counter time-derived? (correlate per-registration timestamps
    with the grant deltas to fit the real clock formula)
  * Is the /message/ HTTP exchange plaintext in the captures, and what does
    the real server return on /message/nonce and /message/sts?
  * What P2P/cloud IPs did the camera actually dial, and what did
    /address/device return? (seed OBSERVED_P2P_IPS from data, not memory)

Reuses the pcap/link-layer/TCP-reassembly machinery from
eseecloud-extract-grants.py (same proven parser), then walks every TCP stream:

  * :19000 -> unmask WebSocket frames, tag each with its segment timestamp
  * TLS (first byte 0x16) -> flagged as encrypted (ClientHello noted)
  * HTTP (GET/POST/HTTP/1.1) -> request line + headers + body summary
  * everything else -> hex head with direction

Output: a merged chronological event log (stdout) and structured JSON (--out).

Usage:
  python3 scripts/eseecloud-extract-session.py \
      captures/eseecloud-mitm-20260808T050802Z/capture.pcap \
      captures/eseecloud-mitm-20260808T053046Z/capture.pcap \
      --out /tmp/session-real.json
"""

import argparse
import importlib.util
import json
import struct
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
GRANTS = SCRIPT_DIR / "eseecloud-extract-grants.py"
WS_SERVER = SCRIPT_DIR / "eseecloud-ws-server.py"

_spec = importlib.util.spec_from_file_location("eg", GRANTS)
eg = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(eg)
read_pcap = eg.read_pcap
link_to_ip = eg.link_to_ip
ip_tcp = eg.ip_tcp
MAX_GAP = eg.MAX_GAP

_spec2 = importlib.util.spec_from_file_location("w", WS_SERVER)
w = importlib.util.module_from_spec(_spec2)
_spec2.loader.exec_module(w)
parse_ws_frame = w.parse_ws_frame
ABBCCDDE = w.ABBCCDDE_MAGIC
CEFAFFE = w.CEFAFFE_MAGIC
D9FFCC = w.HELLO_MAGIC


def is_private(ip: str) -> bool:
    parts = [int(x) for x in ip.split(".")]
    if parts[0] == 10:
        return True
    if parts[0] == 172 and 16 <= parts[1] <= 31:
        return True
    if parts[0] == 192 and parts[1] == 168:
        return True
    return False


def reassemble_with_ts(segs):
    """Concatenate (ts, seq, body) segments, tracking the timestamp at which
    each output offset was laid down. Mirrors eg.reassemble()'s gap policy."""
    out = bytearray()
    ts_at = []  # (offset, ts) markers as data is appended
    expect = None
    for ts, seq, body in sorted(segs, key=lambda s: s[1]):
        if expect is None:
            ts_at.append((len(out), ts))
            out += body
            expect = seq + len(body)
            continue
        if seq > expect and (seq - expect) <= MAX_GAP:
            out += b"\x00" * (seq - expect)
            expect = seq
        if seq <= expect and expect - seq < len(body):
            start = expect - seq
            ts_at.append((len(out), ts))
            out += body[start:]
            expect = seq + len(body)
    return bytes(out), ts_at


def ts_at_offset(ts_at, offset):
    best = None
    for o, t in ts_at:
        if o <= offset:
            best = t
        else:
            break
    return best if best is not None else (ts_at[0][1] if ts_at else 0.0)


def ws_frames(stream, ts_at):
    """Yield (timestamp, offset, opcode, payload) for every WS frame."""
    pos = 0
    frames = []
    while pos < len(stream):
        opcode, payload, consumed, err = parse_ws_frame(stream[pos:])
        if opcode is None or consumed <= 0:
            break
        frames.append((ts_at_offset(ts_at, pos), pos, opcode, payload))
        pos += consumed
    return frames


def classify_ws(payload: bytes) -> str:
    if len(payload) >= 4 and payload[:4] == ABBCCDDE:
        cmd = payload[4] if len(payload) > 4 else 0
        kind = {0x00: "LITE 0x00", 0x11: "FULL 0x11", 0x12: "GRANT 0x12",
                0x04: "VER 0x04"}.get(cmd, f"cmd={cmd:02x}")
        if cmd == 0x11 and len(payload) >= 32:
            return f"{kind} 128B counter={payload[12:16].hex()} " \
                   f"pconv={payload[16:20].hex()} serial={payload[32:42]!r}"
        if cmd == 0x12 and len(payload) >= 36:
            return f"{kind} 100B counter={payload[12:16].hex()} " \
                   f"next={payload[32:36].hex()}"
        return f"{kind} {len(payload)}B"
    if len(payload) >= 4 and payload[:4] == CEFAFFE:
        return f"hello cefaeffe {len(payload)}B"
    if len(payload) >= 16 and payload[:16] == D9FFCC:
        return f"probe d9ffcc {len(payload)}B"
    return f"frame {len(payload)}B"


def fmt_ts(ts: float) -> str:
    import datetime
    return datetime.datetime.fromtimestamp(ts, datetime.timezone.utc) \
        .strftime("%H:%M:%S.%f")[:-3] + "Z"


def analyze_pcap(path: Path):
    streams = {}
    for ts, net, data in read_pcap(path):
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
        streams.setdefault(key, []).append((ts, seq, body))
    events = []
    for (src, sp, dst, dp), segs in streams.items():
        cam_side = src if is_private(src) else dst
        stream_bytes, ts_at = reassemble_with_ts(segs)
        if not stream_bytes:
            continue
        first_ts = min(s[0] for s in segs)
        if sp == 19000 or dp == 19000:
            for fts, off, opcode, payload in ws_frames(stream_bytes, ts_at):
                direction = "cam->srv" if is_private(src) else "srv->cam"
                events.append({
                    "ts": fts, "stream": f"{src}:{sp}->{dst}:{dp}",
                    "dir": direction, "port": sp if sp == 19000 else dp,
                    "kind": "WS", "summary": classify_ws(payload),
                    "hex": payload.hex(),
                })
        elif stream_bytes[:1] == b"\x16":  # TLS handshake record
            direction = "cam->srv" if is_private(src) else "srv->cam"
            events.append({
                "ts": first_ts, "stream": f"{src}:{sp}->{dst}:{dp}",
                "dir": direction, "port": dp,
                "kind": "TLS", "summary": f"TLS {len(stream_bytes)}B "
                                          f"(encrypted — head {stream_bytes[:16].hex()})",
                "hex": stream_bytes[:64].hex(),
            })
        elif stream_bytes[:4] in (b"GET ", b"POST", b"HEAD") or \
                stream_bytes[:8] == b"HTTP/1.1":
            direction = "cam->srv" if is_private(src) else "srv->cam"
            head = stream_bytes.split(b"\r\n\r\n", 1)[0]
            first_line = head.split(b"\r\n", 1)[0].decode("latin-1", "replace")
            events.append({
                "ts": first_ts, "stream": f"{src}:{sp}->{dst}:{dp}",
                "dir": direction, "port": dp,
                "kind": "HTTP", "summary": first_line,
                "hex": stream_bytes[:256].hex(),
            })
        else:
            direction = "cam->srv" if is_private(src) else "srv->cam"
            events.append({
                "ts": first_ts, "stream": f"{src}:{sp}->{dst}:{dp}",
                "dir": direction, "port": dp,
                "kind": "BIN", "summary": f"bin {len(stream_bytes)}B",
                "hex": stream_bytes[:64].hex(),
            })
    events.sort(key=lambda e: e["ts"])
    return events


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pcaps", nargs="+", help="capture.pcap file(s)")
    ap.add_argument("--out", default="", help="write structured JSON here")
    args = ap.parse_args()

    all_events = []
    for p in args.pcaps:
        print(f"── {Path(p).parent.name} ({Path(p).name}) ──")
        evs = analyze_pcap(Path(p))
        for e in evs:
            print(f"  {fmt_ts(e['ts'])} {e['dir']:<8} :{e['port']:<5} "
                  f"{e['kind']:<4} {e['summary']}")
        all_events.extend(evs)
    all_events.sort(key=lambda e: e["ts"])

    if args.out:
        with open(args.out, "w") as f:
            json.dump({"events": all_events}, f, indent=2)
        print(f"\nwrote {len(all_events)} events to {args.out}")


if __name__ == "__main__":
    main()
