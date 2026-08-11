#!/usr/bin/env python3
"""
eseecloud-parser.py — Binary protocol structure analyzer for captured
EseeCloud check-in traffic.

Takes one or more pcap files from capture-eseecloud-boot.sh and reverse-
engineers the binary protocol format: magic bytes, length fields, checksums,
and fixed-vs-variable byte positions. Produces an annotated protocol
specification, a Python struct format string, and a builder function.

USAGE:
  # Single capture analysis
  python3 scripts/eseecloud-parser.py analyze captures/eseecloud-*.pcap \
      --camera-ip 10.0.0.227

  # Multi-capture comparison (most powerful — finds variable fields)
  python3 scripts/eseecloud-parser.py analyze \
      captures/cam1/eseecloud-boot-*.pcap \
      captures/cam2/eseecloud-boot-*.pcap \
      --camera-ip 10.0.0.227

  # Output the protocol spec as JSON
  python3 scripts/eseecloud-parser.py analyze *.pcap --camera-ip X -o spec.json

DEPENDENCIES: Python 3 stdlib only. Requires tcpdump on PATH.
"""

import argparse
import json
import re
import socket
import struct
import subprocess
import sys
import time
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional

# ═══════════════════════════════════════════════════════════════════════════
# PCAP PARSER (same approach as eseecloud-forge.py, self-contained)
# ═══════════════════════════════════════════════════════════════════════════

@dataclass
class Packet:
    timestamp: float
    src_ip: str
    src_port: int
    dst_ip: str
    dst_port: int
    protocol: str
    flags: str
    seq: int
    length: int
    payload: bytes


@dataclass
class Stream:
    key: str
    src_ip: str
    src_port: int
    dst_ip: str
    dst_port: int
    protocol: str
    camera_ip: str = ""
    packets: list = field(default_factory=list)

    @property
    def first_ts(self) -> float:
        return self.packets[0].timestamp if self.packets else 0

    @property
    def duration(self) -> float:
        return (self.packets[-1].timestamp - self.packets[0].timestamp) if self.packets else 0

    @property
    def is_outbound(self) -> bool:
        if not self.camera_ip:
            return False
        if self.src_ip == self.camera_ip:
            return _is_private(self.src_ip) and not _is_private(self.dst_ip)
        return _is_private(self.dst_ip) and not _is_private(self.src_ip)

    @property
    def total_sent(self) -> int:
        if not self.camera_ip:
            return 0
        return sum(p.length for p in self.packets if p.src_ip == self.camera_ip)

    @property
    def total_recv(self) -> int:
        if not self.camera_ip:
            return 0
        return sum(p.length for p in self.packets if p.dst_ip == self.camera_ip)


def _is_private(ip: str) -> bool:
    try:
        parts = [int(x) for x in ip.split(".")]
        if len(parts) != 4:
            return False
        if parts[0] == 10:
            return True
        if parts[0] == 172 and 16 <= parts[1] <= 31:
            return True
        if parts[0] == 192 and parts[1] == 168:
            return True
        return False
    except (ValueError, AttributeError):
        return False


ESEECLOUD_PORTS = {8800, 35000, 37777, 37778, 34567, 12366, 15001, 15002,
                   15003, 15004, 18004, 34569, 5000, 5050, 5540, 7050,
                   8050, 9050, 10080, 18080, 19080, 20000, 25000}


def parse_pcap(pcap_path: str, camera_ip: Optional[str] = None) -> list[Stream]:
    """Parse a pcap file via tcpdump -X and return reconstructed streams."""
    cmd = ["tcpdump", "-r", pcap_path, "-nn", "-X", "-tt"]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        output = result.stdout or result.stderr
    except subprocess.TimeoutExpired:
        print(f"ERROR: tcpdump timed out on {pcap_path}", file=sys.stderr)
        return []
    except FileNotFoundError:
        print("ERROR: tcpdump not found.", file=sys.stderr)
        sys.exit(1)

    packets = _parse_packets(output)
    return _build_streams(packets, camera_ip)


def _parse_packets(output: str) -> list[Packet]:
    """Parse tcpdump -tt -nn -X output into Packet objects."""
    packets = []
    lines = output.split("\n")
    ip_summary_pat = re.compile(r"^\d+\.\d+\s+IP\s+")
    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if not line:
            i += 1
            continue
        m = re.match(
            r"^(\d+\.\d+)\s+IP\s+"
            r"(\d+\.\d+\.\d+\.\d+)\.(\d+)\s+>\s+"
            r"(\d+\.\d+\.\d+\.\d+)\.(\d+):\s*(.*)", line
        )
        if not m:
            i += 1
            continue

        ts = float(m.group(1))
        src_ip = m.group(2)
        src_port = int(m.group(3))
        dst_ip = m.group(4)
        dst_port = int(m.group(5))
        rest = m.group(6)

        protocol = "UDP" if "UDP" in rest else "TCP"
        flags = ""
        flags_m = re.search(r"Flags\s+\[([^\]]*)\]", rest)
        if flags_m:
            flags = flags_m.group(1)
        length = 0
        length_m = re.search(r"length\s+(\d+)", rest)
        if length_m:
            length = int(length_m.group(1))
        seq = 0
        seq_m = re.search(r"seq\s+(\d+):(\d+)", rest)
        if seq_m:
            seq = int(seq_m.group(1))

        payload_lines = []
        j = i + 1
        while j < len(lines):
            hex_line = lines[j]
            if re.match(r"^\s+0x[0-9a-fA-F]+:", hex_line):
                payload_lines.append(hex_line)
                if (j + 1 < len(lines)
                        and not re.match(r"^\s+0x[0-9a-fA-F]+:", lines[j + 1])
                        and lines[j + 1].strip()
                        and not ip_summary_pat.match(lines[j + 1].strip())):
                    j += 1
                j += 1
            else:
                break

        payload = _extract_payload(payload_lines)
        if len(payload) > length:
            payload = payload[:length]

        packets.append(Packet(ts, src_ip, src_port, dst_ip, dst_port,
                              protocol, flags, seq, length, payload))
        i = j

    return packets


def _extract_payload(hex_lines: list[str]) -> bytes:
    """Extract raw bytes from tcpdump hex dump lines (2-char hex tokens only)."""
    result = bytearray()
    for line in hex_lines:
        m = re.search(r"0x[0-9a-f]+:\s+(.*)", line)
        if not m:
            continue
        for token in m.group(1).split():
            token = token.strip()
            if len(token) == 2 and all(c in "0123456789abcdefABCDEF" for c in token):
                try:
                    result.append(int(token, 16))
                except ValueError:
                    break
            else:
                break
    return bytes(result)


def _build_streams(packets: list[Packet], camera_ip: Optional[str]) -> list[Stream]:
    """Group packets into streams by 4-tuple."""
    streams: dict[str, Stream] = {}
    cam_ip = camera_ip or ""
    filtered = packets
    if camera_ip:
        filtered = [p for p in packets if p.src_ip == camera_ip or p.dst_ip == camera_ip]

    for pkt in filtered:
        if (pkt.src_ip, pkt.src_port) < (pkt.dst_ip, pkt.dst_port):
            key = f"{pkt.src_ip}:{pkt.src_port}->{pkt.dst_ip}:{pkt.dst_port}/{pkt.protocol}"
            if key not in streams:
                streams[key] = Stream(key, pkt.src_ip, pkt.src_port,
                                      pkt.dst_ip, pkt.dst_port, pkt.protocol, cam_ip)
        else:
            key = f"{pkt.dst_ip}:{pkt.dst_port}->{pkt.src_ip}:{pkt.src_port}/{pkt.protocol}"
            if key not in streams:
                streams[key] = Stream(key, pkt.dst_ip, pkt.dst_port,
                                      pkt.src_ip, pkt.src_port, pkt.protocol, cam_ip)
        streams[key].packets.append(pkt)

    for s in streams.values():
        s.packets.sort(key=lambda p: p.timestamp)
    return list(streams.values())


def find_checkin_stream(streams: list[Stream], camera_ip: str) -> Optional[Stream]:
    """Score streams and return the best check-in candidate."""
    scored = []
    for s in streams:
        score = 0
        has_outbound = any(p.src_ip == camera_ip and p.length > 0 for p in s.packets)
        if not has_outbound:
            continue
        if s.is_outbound:
            score += 10
        if s.dst_port in ESEECLOUD_PORTS:
            score += 20
        payload_count = sum(1 for p in s.packets if p.length > 0)
        score += payload_count * 2
        has_send = any(p.src_ip == camera_ip and p.length > 0 for p in s.packets)
        has_recv = any(p.dst_ip == camera_ip and p.length > 0 for p in s.packets)
        if has_send and has_recv:
            score += 15
        if s.dst_port not in (80, 443, 8080, 8443, 53, 5353):
            score += 5
        if s.duration < 5.0:
            score += 5
        scored.append((score, s))
    scored.sort(key=lambda x: x[0], reverse=True)
    return scored[0][1] if scored else None


# ═══════════════════════════════════════════════════════════════════════════
# BINARY PROTOCOL ANALYZER
# ═══════════════════════════════════════════════════════════════════════════

@dataclass
class FieldAnnotation:
    """A discovered field within a protocol message."""
    offset: int
    length: int
    name: str
    description: str
    confidence: str  # "high", "medium", "low"
    sample_value_hex: str = ""
    endian: str = ""  # "big", "little", ""


@dataclass
class ProtocolMessage:
    """One side (send or receive direction) of a protocol conversation."""
    direction: str  # "send" or "recv"
    payloads: list[bytes]  # multiple samples (from multi-pcap)
    annotations: list[FieldAnnotation] = field(default_factory=list)
    fixed_mask: Optional[bytes] = None  # bytes where all samples agree (0xFF = fixed)
    variable_mask: Optional[bytes] = None  # bytes where samples differ (0xFF = variable)


class ProtocolAnalyzer:
    """
    Analyzes binary protocol payloads to discover:
    - Magic bytes (common prefixes)
    - Length fields (values matching remaining payload)
    - Checksums (CRC, XOR, sum variants)
    - Fixed vs variable regions (from multi-pcap comparison)
    """

    # CRC polynomials to try
    CRC_POLYS = {
        "CRC-16/XMODEM":  (16, 0x1021, 0x0000, 0x0000, False, False),
        "CRC-16/CCITT":   (16, 0x1021, 0xFFFF, 0x0000, False, False),
        "CRC-16/IBM":     (16, 0x8005, 0x0000, 0x0000, True, True),
        "CRC-16/DNP":     (16, 0x3D65, 0x0000, 0xFFFF, True, True),
        "CRC-32":         (32, 0x04C11DB7, 0xFFFFFFFF, 0xFFFFFFFF, True, True),
        "CRC-32/BZIP2":   (32, 0x04C11DB7, 0xFFFFFFFF, 0xFFFFFFFF, False, False),
        "CRC-32/MPEG2":   (32, 0x04C11DB7, 0xFFFFFFFF, 0x00000000, False, False),
    }

    def __init__(self, send_payloads: list[bytes], recv_payloads: list[bytes]):
        self.send = ProtocolMessage("send", send_payloads)
        self.recv = ProtocolMessage("recv", recv_payloads)

    def analyze(self) -> dict:
        """Run all analyses and return a protocol specification dict."""
        # Multi-sample analysis
        self._compute_fixed_masks()
        self._find_magic_bytes()
        self._find_length_fields()
        self._find_checksums()
        self._classify_variable_fields()

        return self._build_spec()

    def _compute_fixed_masks(self):
        """Compare multiple payloads to find fixed vs variable bytes."""
        for msg in [self.send, self.recv]:
            if len(msg.payloads) < 2:
                msg.fixed_mask = None
                msg.variable_mask = None
                continue

            # Find max length to align
            max_len = max(len(p) for p in msg.payloads)
            fixed = bytearray([0xFF] * max_len)
            variable = bytearray([0x00] * max_len)

            for i in range(max_len):
                vals = set()
                for p in msg.payloads:
                    if i < len(p):
                        vals.add(p[i])
                    else:
                        vals.add(None)
                if len(vals) == 1:
                    fixed[i] = 0xFF
                    variable[i] = 0x00
                else:
                    fixed[i] = 0x00
                    variable[i] = 0xFF

            msg.fixed_mask = bytes(fixed)
            msg.variable_mask = bytes(variable)

    def _find_magic_bytes(self):
        """Find common prefix (magic bytes) across all same-direction packets."""
        for msg in [self.send, self.recv]:
            if not msg.payloads or len(msg.payloads[0]) == 0:
                continue

            # Find longest common prefix across all payloads
            prefix_len = len(msg.payloads[0])
            for p in msg.payloads[1:]:
                common = 0
                for a, b in zip(msg.payloads[0], p):
                    if a == b:
                        common += 1
                    else:
                        break
                prefix_len = min(prefix_len, common)

            if prefix_len >= 2:
                magic_val = msg.payloads[0][:prefix_len]
                # Determine if it looks like a protocol header
                is_ascii = all(32 <= b < 127 for b in magic_val)
                label = "ASCII magic" if is_ascii else "binary header"
                msg.annotations.append(FieldAnnotation(
                    offset=0, length=prefix_len,
                    name="magic",
                    description=f"Protocol {label}: {magic_val.hex()} "
                                f"{chr(magic_val[0]) if is_ascii else ''}",
                    confidence="high",
                    sample_value_hex=magic_val.hex(),
                ))

    def _find_length_fields(self):
        """Detect fields whose value equals remaining payload length."""
        for msg in [self.send, self.recv]:
            for p in msg.payloads:
                if len(p) < 3:
                    continue
                for offset in range(min(8, len(p) - 1)):
                    # Try 1-byte length
                    if offset + 1 < len(p):
                        val8 = p[offset]
                        remaining = len(p) - offset - 1
                        total = len(p)
                        conf8 = "medium" if val8 > 5 else "low"
                        if val8 == remaining:
                            msg.annotations.append(FieldAnnotation(
                                offset=offset, length=1, name="length8_remaining",
                                description=f"uint8 @ +{offset} = {val8} (remaining bytes after field)",
                                confidence=conf8, sample_value_hex=f"{val8:02x}",
                            ))
                        if val8 == total:
                            msg.annotations.append(FieldAnnotation(
                                offset=offset, length=1, name="length8_total",
                                description=f"uint8 @ +{offset} = {val8} (total packet length)",
                                confidence=conf8, sample_value_hex=f"{val8:02x}",
                            ))

                    # Try 2-byte length (big-endian)
                    if offset + 2 < len(p):
                        val16be = struct.unpack(">H", p[offset:offset + 2])[0]
                        val16le = struct.unpack("<H", p[offset:offset + 2])[0]
                        remaining2 = len(p) - offset - 2
                        for endian, val16 in [("big", val16be), ("little", val16le)]:
                            # Lower confidence for small values that could be coincidental
                            conf = "high" if val16 > 15 else "low"
                            if val16 == remaining2:
                                msg.annotations.append(FieldAnnotation(
                                    offset=offset, length=2, name="length16_remaining",
                                    description=f"uint16_{endian} @ +{offset} = {val16} (remaining bytes)",
                                    confidence=conf,
                                    sample_value_hex=p[offset:offset + 2].hex(),
                                    endian=endian,
                                ))
                            if val16 == len(p):
                                msg.annotations.append(FieldAnnotation(
                                    offset=offset, length=2, name="length16_total",
                                    description=f"uint16_{endian} @ +{offset} = {val16} (total length)",
                                    confidence=conf, endian=endian,
                                    sample_value_hex=p[offset:offset + 2].hex(),
                                ))

                    # Try 4-byte length (big-endian)
                    if offset + 4 < len(p):
                        val32be = struct.unpack(">I", p[offset:offset + 4])[0]
                        val32le = struct.unpack("<I", p[offset:offset + 4])[0]
                        remaining4 = len(p) - offset - 4
                        for endian, val32 in [("big", val32be), ("little", val32le)]:
                            conf = "high" if val32 > 15 else "low"
                            if val32 == remaining4:
                                msg.annotations.append(FieldAnnotation(
                                    offset=offset, length=4, name="length32_remaining",
                                    description=f"uint32_{endian} @ +{offset} = {val32} (remaining bytes)",
                                    confidence=conf,
                                    sample_value_hex=p[offset:offset + 4].hex(),
                                    endian=endian,
                                ))

        # Deduplicate annotations
        for msg in [self.send, self.recv]:
            seen = set()
            unique = []
            for a in msg.annotations:
                key = (a.offset, a.length, a.name)
                if key not in seen:
                    seen.add(key)
                    unique.append(a)
            msg.annotations = unique

    def _find_checksums(self):
        """Detect checksum fields in packet payloads."""
        for msg in [self.send, self.recv]:
            for p in msg.payloads:
                if len(p) < 4:
                    continue

                # Try last 1, 2, and 4 bytes as checksum
                for csum_len in [1, 2, 4]:
                    if len(p) <= csum_len:
                        continue
                    csum_val = int.from_bytes(p[-csum_len:], "big")
                    data = p[:-csum_len]

                    # XOR checksum
                    xor_val = 0
                    for b in data:
                        xor_val ^= b
                    if csum_len == 1 and xor_val == csum_val:
                        msg.annotations.append(FieldAnnotation(
                            offset=len(p) - csum_len, length=csum_len,
                            name=f"checksum_xor{csum_len*8}",
                            description=f"XOR-{csum_len*8} checksum (verified)",
                            confidence="high",
                            sample_value_hex=p[-csum_len:].hex(),
                        ))
                    if csum_len == 2 and (xor_val & 0xFF) == (csum_val & 0xFF):
                        msg.annotations.append(FieldAnnotation(
                            offset=len(p) - csum_len, length=csum_len,
                            name=f"checksum_xor8_in_16",
                            description=f"XOR-8 stored in uint16 (verified)",
                            confidence="medium",
                            sample_value_hex=p[-csum_len:].hex(),
                        ))

                    # Sum checksum (mod 256, mod 65536)
                    sum_val = sum(data)
                    if csum_len == 1 and (sum_val & 0xFF) == csum_val:
                        msg.annotations.append(FieldAnnotation(
                            offset=len(p) - csum_len, length=csum_len,
                            name=f"checksum_sum8",
                            description=f"Sum mod-256 checksum (verified)",
                            confidence="high",
                            sample_value_hex=p[-csum_len:].hex(),
                        ))
                    if csum_len == 2 and (sum_val & 0xFFFF) == csum_val:
                        msg.annotations.append(FieldAnnotation(
                            offset=len(p) - csum_len, length=csum_len,
                            name=f"checksum_sum16",
                            description=f"Sum mod-65536 checksum (verified)",
                            confidence="high",
                            sample_value_hex=p[-csum_len:].hex(),
                        ))

                    # CRC checks
                    for crc_name, (bits, poly, init, xor_out, refin, refout) in self.CRC_POLYS.items():
                        if bits != csum_len * 8:
                            continue
                        computed = _crc_n(data, bits, poly, init, xor_out, refin, refout)
                        le_val = int.from_bytes(p[-csum_len:], "little")
                        be_val = int.from_bytes(p[-csum_len:], "big")
                        if computed == be_val or computed == le_val:
                            endian = "big" if computed == be_val else "little"
                            msg.annotations.append(FieldAnnotation(
                                offset=len(p) - csum_len, length=csum_len,
                                name=f"checksum_{crc_name.replace('/', '_').replace('-', '_')}",
                                description=f"{crc_name} ({endian}-endian, verified)",
                                confidence="high",
                                sample_value_hex=p[-csum_len:].hex(),
                                endian=endian,
                            ))

        # Deduplicate all annotations: for checksums, dedup by (offset, length);
        # for everything else, dedup by (offset, length, name). Single-pass.
        for msg in [self.send, self.recv]:
            seen_checksums = {}   # (offset, length) → annotation
            seen_other = set()    # (offset, length, name)
            merged = []
            for a in msg.annotations:
                if a.name.startswith("checksum_"):
                    key = (a.offset, a.length)
                    if key not in seen_checksums or a.confidence == "high":
                        seen_checksums[key] = a
                else:
                    key = (a.offset, a.length, a.name)
                    if key not in seen_other:
                        seen_other.add(key)
                        merged.append(a)
            # Append deduped checksums
            merged.extend(seen_checksums.values())
            msg.annotations = sorted(merged, key=lambda a: a.offset)

        # TODO: also try checksum positions in the packet header (offsets 4, 6, 8, 12)
        # rather than only the last N bytes. Some protocols embed checksums mid-packet
        # before a variable-length payload.

    def _classify_variable_fields(self):
        """Classify variable byte regions from multi-pcap comparison."""
        for msg in [self.send, self.recv]:
            if not msg.variable_mask or len(msg.payloads) < 2:
                continue

            # Find contiguous variable regions
            i = 0
            while i < len(msg.variable_mask):
                if msg.variable_mask[i] == 0x00:
                    i += 1
                    continue
                start = i
                while i < len(msg.variable_mask) and msg.variable_mask[i] == 0xFF:
                    i += 1
                length = i - start

                if length == 0:
                    continue

                # Heuristic classification of variable field
                region_bytes = [p[start:start + length] for p in msg.payloads
                                if start + length <= len(p)]
                if not region_bytes:
                    continue

                # Check if it's a device serial (printable ASCII)
                all_ascii = all(
                    all(32 <= b < 127 for b in rb) for rb in region_bytes
                )
                # Check if it's a counter/timestamp (monotonically increasing?)
                # Check if it's a MAC-like value (6 bytes)
                # Check if it's random (wide variance)

                if all_ascii and length >= 4:
                    name = "device_id_ascii"
                    desc = f"Device identifier (ASCII, {length}B) — varies per camera"
                elif length == 6:
                    name = "device_mac"
                    desc = f"MAC address ({length}B) — varies per camera"
                elif length == 4:
                    name = "variable_uint32"
                    desc = f"Variable 4-byte field — could be counter, timestamp, or ID"
                elif length <= 2:
                    name = "variable_short"
                    desc = f"Variable {length}B field"
                else:
                    name = "variable_blob"
                    desc = f"Variable {length}B region — camera-specific data"

                msg.annotations.append(FieldAnnotation(
                    offset=start, length=length, name=name,
                    description=desc,
                    confidence="high" if msg.variable_mask is not None else "medium",
                    sample_value_hex=region_bytes[0].hex() if region_bytes else "",
                ))

    def _build_spec(self) -> dict:
        """Build the full protocol specification dict."""
        def msg_to_dict(msg: ProtocolMessage, label: str) -> dict:
            payloads_hex = [p.hex() for p in msg.payloads]
            return {
                "direction": msg.direction,
                "sample_count": len(msg.payloads),
                "min_length": min((len(p) for p in msg.payloads), default=0),
                "max_length": max((len(p) for p in msg.payloads), default=0),
                "sample_payloads": payloads_hex[:3],  # up to 3 samples
                "fixed_mask": msg.fixed_mask.hex() if msg.fixed_mask else None,
                "variable_mask": msg.variable_mask.hex() if msg.variable_mask else None,
                "fields": [
                    {
                        "offset": a.offset,
                        "length": a.length,
                        "name": a.name,
                        "description": a.description,
                        "confidence": a.confidence,
                        "sample_value_hex": a.sample_value_hex,
                        "endian": a.endian or None,
                    }
                    for a in msg.annotations
                ],
            }

        return {
            "analyzed_at": datetime.now(timezone.utc).isoformat(),
            "send": msg_to_dict(self.send, "send"),
            "recv": msg_to_dict(self.recv, "recv"),
        }


def _crc_n(data: bytes, bits: int, poly: int, init: int,
           xor_out: int, refin: bool, refout: bool) -> int:
    """Generic CRC-N calculator."""
    if bits == 16:
        return _crc16(data, poly, init, xor_out, refin, refout)
    elif bits == 32:
        return _crc32_generic(data, poly, init, xor_out, refin, refout)
    return 0


def _crc16(data: bytes, poly: int, init: int, xor_out: int,
           refin: bool, refout: bool) -> int:
    """CRC-16 with configurable parameters."""
    crc = init
    for byte in data:
        b = byte
        if refin:
            b = int(f"{b:08b}"[::-1], 2)
        crc ^= b << 8
        for _ in range(8):
            if crc & 0x8000:
                crc = ((crc << 1) ^ poly) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF
    if refout:
        crc = int(f"{crc:016b}"[::-1], 2)
    return (crc ^ xor_out) & 0xFFFF


def _crc32_generic(data: bytes, poly: int, init: int, xor_out: int,
                   refin: bool, refout: bool) -> int:
    """CRC-32 with configurable parameters."""
    crc = init
    for byte in data:
        b = byte
        if refin:
            b = int(f"{b:08b}"[::-1], 2)
        crc ^= b << 24
        for _ in range(8):
            if crc & 0x80000000:
                crc = ((crc << 1) ^ poly) & 0xFFFFFFFF
            else:
                crc = (crc << 1) & 0xFFFFFFFF
    if refout:
        crc = int(f"{crc:032b}"[::-1], 2)
    return (crc ^ xor_out) & 0xFFFFFFFF


# ═══════════════════════════════════════════════════════════════════════════
# OUTPUT FORMATTERS
# ═══════════════════════════════════════════════════════════════════════════

def print_annotated_hex(msg: ProtocolMessage, label: str):
    """Print an annotated hex dump with discovered fields highlighted."""
    if not msg.payloads:
        print(f"\n  [{label}] (no payloads)")
        return

    payload = msg.payloads[0]
    print(f"\n  [{label}] {len(payload)} bytes "
          f"(from {len(msg.payloads)} sample{'s' if len(msg.payloads) != 1 else ''})")
    print(f"  {'─' * 68}")

    # Build offset → annotation map
    field_map = {}
    for a in msg.annotations:
        for off in range(a.offset, a.offset + a.length):
            field_map[off] = a

    bytes_per_row = 16
    for row_start in range(0, len(payload), bytes_per_row):
        chunk = payload[row_start:row_start + bytes_per_row]
        hex_str = " ".join(f"{b:02x}" for b in chunk)
        ascii_str = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)

        # Build offset ruler
        print(f"  {row_start:04x}  {hex_str:<{bytes_per_row*3}}  |{ascii_str}|")

        # Show field annotations on the next line
        annotations_in_row = set()
        for off in range(row_start, row_start + bytes_per_row):
            if off in field_map:
                a = field_map[off]
                if a.offset not in annotations_in_row:
                    annotations_in_row.add(a.offset)
                    marker = " " * (6 + (off - row_start) * 3)
                    bar = "─" * (a.length * 3 - 1) if a.length > 1 else "▲"
                    print(f"  {marker}{bar} {a.name} ({a.description[:50]})")

    # Variable bytes heatmap (if available)
    if msg.variable_mask and len(msg.payloads) > 1:
        print(f"\n  Variable bytes (from {len(msg.payloads)} captures):")
        var_positions = [
            i for i, v in enumerate(msg.variable_mask) if v == 0xFF
        ]
        if var_positions:
            for row_start in range(0, len(msg.variable_mask), bytes_per_row):
                indicators = ""
                for off in range(row_start, min(row_start + bytes_per_row, len(msg.variable_mask))):
                    if msg.variable_mask[off] == 0xFF:
                        indicators += "VV "
                    else:
                        indicators += "   "
                print(f"  {row_start:04x}  {indicators}")
        else:
            print("  (all bytes are fixed across captures)")
        print("  V = varies between captures (camera-specific), space = fixed (protocol)")

    # Summary of discovered fields
    if msg.annotations:
        print(f"\n  ── Discovered fields ──")
        for a in sorted(msg.annotations, key=lambda x: x.offset):
            conf_marker = {"high": "✓", "medium": "~", "low": "?"}.get(a.confidence, "?")
            print(f"  {conf_marker} +{a.offset:04x}  [{a.length:3d}B]  {a.name:25s}  {a.description}")


def print_struct_format(spec: dict):
    """Generate and print Python struct format strings for parsing."""
    print(f"\n{'='*70}")
    print(f"  Python struct format strings")
    print(f"{'='*70}")

    for direction in ["send", "recv"]:
        msg = spec[direction]
        print(f"\n  # {direction.upper()} message ({msg['min_length']}–{msg['max_length']} bytes)")
        if not msg["fields"]:
            print(f"  # (no fields discovered)")
            continue

        format_parts = []
        field_names = []
        last_end = 0

        for f in sorted(msg["fields"], key=lambda x: x["offset"]):
            # Add padding before this field
            if f["offset"] > last_end:
                pad_len = f["offset"] - last_end
                format_parts.append(f"{pad_len}s")
                field_names.append(f"_pad_{last_end:04x}")

            # Map field to struct format
            if f["length"] == 1:
                fmt = "B"
            elif f["length"] == 2:
                fmt = ">H" if f.get("endian") == "big" else "<H"
            elif f["length"] == 4:
                fmt = ">I" if f.get("endian") == "big" else "<I"
            else:
                fmt = f"{f['length']}s"
            format_parts.append(fmt)
            field_names.append(f["name"])
            last_end = f["offset"] + f["length"]

        # Add trailing data (skip if max_length is 0 or already fully covered)
        if msg["max_length"] > 0 and last_end < msg["max_length"]:
            format_parts.append(f"{msg['max_length'] - last_end}s")
            field_names.append(f"_trailing")

        fmt_str = "".join(format_parts)
        print(f"  _FMT_{direction.upper()} = \"{fmt_str}\"")
        print(f"  _NAMES_{direction.upper()} = {field_names}")
        print(f"  # Usage: fields = struct.unpack(_FMT_{direction.upper()}, data)")
        print(f"  #        parsed = dict(zip(_NAMES_{direction.upper()}, fields))")


def print_builder(spec: dict):
    """Generate Python builder function for creating protocol packets."""
    print(f"\n{'='*70}")
    print(f"  Python Payload Builder")
    print(f"{'='*70}")

    for direction in ["send", "recv"]:
        msg = spec[direction]
        if not msg["fields"]:
            continue

        print(f"\n  def build_{direction}(**kwargs) -> bytes:")
        print(f'      """Build a {direction} protocol message."""')

        # Find the canonical payload (first sample)
        if msg.get("sample_payloads"):
            default_hex = msg["sample_payloads"][0]
            default = bytes.fromhex(default_hex)
            print(f"      # Template from capture: {default_hex[:40]}...")
            print(f"      buf = bytearray(bytes.fromhex(\"{default_hex}\"))")
        else:
            max_len = msg["max_length"]
            print(f"      buf = bytearray({max_len})  # {max_len} bytes, zero-filled")

        print()

        for f in sorted(msg["fields"], key=lambda x: x["offset"]):
            print(f"      # +{f['offset']:04x}  [{f['length']}B]  {f['name']}")
            print(f"      # {f['description']}")

        print()
        print(f"      # TODO: Apply kwargs to the buffer at the offsets above")
        print(f"      return bytes(buf)")

    print(f"\n  # Usage:")
    print(f"  #   checkin_pkt = build_send(device_id_ascii=b\"LOCKEDCAM123\")")
    print(f"  #   sock.sendall(checkin_pkt)")


# ═══════════════════════════════════════════════════════════════════════════
# CLI
# ═══════════════════════════════════════════════════════════════════════════

def cmd_analyze(args):
    """Analyze one or more pcap files and produce a protocol specification."""
    pcaps = args.pcaps
    camera_ip = args.camera_ip

    if not pcaps:
        print("ERROR: No pcap files provided.", file=sys.stderr)
        sys.exit(1)

    print(f"Analyzing {len(pcaps)} pcap file(s)...")

    all_send_payloads = []
    all_recv_payloads = []
    server_info = {"ip": "", "port": 0}

    for i, pcap_path in enumerate(pcaps):
        print(f"\n  [{i+1}/{len(pcaps)}] {pcap_path}")
        streams = parse_pcap(pcap_path, camera_ip)

        if not streams:
            print(f"    WARNING: No streams found in {pcap_path}")
            continue

        checkin = find_checkin_stream(streams, camera_ip)
        if not checkin:
            print(f"    WARNING: No check-in stream identified in {pcap_path}")
            continue

        # Determine server from the stream
        if checkin.is_outbound:
            server_info["ip"] = checkin.dst_ip
            server_info["port"] = checkin.dst_port
        else:
            server_info["ip"] = checkin.src_ip
            server_info["port"] = checkin.src_port

        # Extract send and receive payloads from this capture
        send_payloads = [
            p.payload for p in checkin.packets
            if p.src_ip == camera_ip and p.length > 0
        ]
        recv_payloads = [
            p.payload for p in checkin.packets
            if p.dst_ip == camera_ip and p.length > 0
        ]

        all_send_payloads.extend(send_payloads)
        all_recv_payloads.extend(recv_payloads)

        print(f"    Server: {server_info['ip']}:{server_info['port']}")
        print(f"    Packets: {checkin.packets.__len__()} total, "
              f"{len(send_payloads)} send, {len(recv_payloads)} recv")

    if not all_send_payloads and not all_recv_payloads:
        print("\nERROR: No payload data extracted from any pcap.", file=sys.stderr)
        sys.exit(1)

    # Analyze
    print(f"\n{'='*70}")
    print(f"  Protocol Analysis — {server_info['ip']}:{server_info['port']}")
    print(f"  Send samples: {len(all_send_payloads)}  |  Recv samples: {len(all_recv_payloads)}")
    print(f"{'='*70}")

    analyzer = ProtocolAnalyzer(all_send_payloads, all_recv_payloads)
    spec = analyzer.analyze()

    # Print annotated hex
    print_annotated_hex(analyzer.send, "SEND (camera → server)")
    print_annotated_hex(analyzer.recv, "RECV (server → camera)")

    # Print struct format
    print_struct_format(spec)

    # Print builder skeleton
    print_builder(spec)

    # Save to file if requested
    if args.output:
        spec["server_ip"] = server_info["ip"]
        spec["server_port"] = server_info["port"]
        spec["source_pcaps"] = pcaps
        with open(args.output, "w") as f:
            json.dump(spec, f, indent=2)
        print(f"\nSpecification saved to: {args.output}")

    print(f"\n{'='*70}")
    print(f"  Analysis complete.")
    print(f"{'='*70}")


def main():
    parser = argparse.ArgumentParser(
        description="EseeCloud binary protocol structure analyzer"
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_analyze = sub.add_parser("analyze", help="Analyze pcap(s) and produce protocol spec")
    p_analyze.add_argument("pcaps", nargs="+", help="One or more pcap files")
    p_analyze.add_argument("--camera-ip", required=True, help="Camera IP address")
    p_analyze.add_argument("-o", "--output", help="Save protocol spec as JSON")

    args = parser.parse_args()

    if args.command == "analyze":
        cmd_analyze(args)


if __name__ == "__main__":
    main()
