#!/usr/bin/env python3
"""
eseecloud-forge.py — Analyze captured EseeCloud boot check-in protocol and
forge check-in packets for locked cameras.

WORKFLOW:
  1. Capture the boot-time traffic with capture-eseecloud-boot.sh
  2. Analyze the pcap to identify the check-in protocol:
       python3 scripts/eseecloud-forge.py analyze captures/eseecloud-10.0.0.227-*/eseecloud-boot-*.pcap
  3. (Optional) Provide deviceInfo JSON to auto-detect identity fields:
       python3 scripts/eseecloud-forge.py analyze --device-info deviceInfo.json captures/...pcap
  4. The tool saves a protocol template JSON.
  5. Forge a check-in for a locked camera:
       python3 scripts/eseecloud-forge.py forge template.json --serial ABC123 --mac 00:11:22:33:44:55

COMMANDS:
  analyze <pcap>       Parse pcap, show streams, extract protocol template
  forge <template>     Send forged check-in using a saved template
  replay <template>    Replay the exact captured check-in (dry-run if --no-send)
  streams <pcap>       Quick list of all streams in the pcap with summary

DEPENDENCIES: Only Python 3 stdlib (struct, socket, json, subprocess, re).
Requires tcpdump on PATH for reading pcap files.
"""

import argparse
import json
import os
import re
import socket
import struct
import subprocess
import sys
import time
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional


# ═══════════════════════════════════════════════════════════════════════════
# PCAP PARSER — Reads tcpdump -X output and extracts packet payloads
# ═══════════════════════════════════════════════════════════════════════════

@dataclass
class Packet:
    """A single packet extracted from tcpdump -X output."""
    timestamp: float
    src_ip: str
    src_port: int
    dst_ip: str
    dst_port: int
    protocol: str  # "TCP" or "UDP"
    flags: str     # TCP flags like "P.", "S", "F", "R", etc.
    seq: int       # TCP sequence number (0 for UDP)
    length: int    # payload length in bytes
    payload: bytes  # raw payload


@dataclass
class Stream:
    """A reassembled TCP or UDP conversation."""
    key: str       # "src_ip:src_port->dst_ip:dst_port/proto"
    src_ip: str
    src_port: int
    dst_ip: str
    dst_port: int
    protocol: str
    camera_ip: str = ""  # the IP of the camera in this conversation
    packets: list = field(default_factory=list)

    @property
    def first_ts(self) -> float:
        return self.packets[0].timestamp if self.packets else 0

    @property
    def last_ts(self) -> float:
        return self.packets[-1].timestamp if self.packets else 0

    @property
    def duration(self) -> float:
        return self.last_ts - self.first_ts

    @property
    def total_bytes_sent(self) -> int:
        """Bytes sent FROM the camera."""
        if not self.camera_ip:
            return 0
        return sum(p.length for p in self.packets if p.src_ip == self.camera_ip)

    @property
    def total_bytes_recv(self) -> int:
        """Bytes sent TO the camera."""
        if not self.camera_ip:
            return 0
        return sum(p.length for p in self.packets if p.dst_ip == self.camera_ip)

    @property
    def packet_count(self) -> int:
        return len(self.packets)

    @property
    def is_outbound(self) -> bool:
        """True if the camera-side IP is private and the other side is public."""
        if not self.camera_ip:
            return False
        if self.src_ip == self.camera_ip:
            return is_private_ip(self.src_ip) and not is_private_ip(self.dst_ip)
        else:
            return is_private_ip(self.dst_ip) and not is_private_ip(self.src_ip)

    @property
    def payload_preview(self) -> bytes:
        """First 64 bytes of the first payload-bearing packet."""
        for p in self.packets:
            if p.length > 0:
                return p.payload[:64]
        return b""


def is_private_ip(ip: str) -> bool:
    """Check if an IP is in a private range."""
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


class PcapReader:
    """
    Reads a pcap file by shelling out to tcpdump -r -nn -X and parsing
    the human-readable output. This avoids any external Python dependencies.
    """

    def __init__(self, pcap_path: str, camera_ip: Optional[str] = None):
        self.pcap_path = pcap_path
        self.camera_ip = camera_ip
        self.packets: list[Packet] = []
        self.streams: dict[str, Stream] = {}

    def parse(self) -> list[Stream]:
        """Parse the pcap and return reconstructed streams."""
        output = self._run_tcpdump()
        self.packets = self._parse_output(output)
        self.streams = self._build_streams()
        return list(self.streams.values())

    def _run_tcpdump(self) -> str:
        """Run tcpdump and return its output."""
        cmd = [
            "tcpdump", "-r", self.pcap_path, "-nn", "-X", "-tt"
        ]
        try:
            result = subprocess.run(
                cmd, capture_output=True, text=True, timeout=30
            )
            # tcpdump writes to stderr for some versions
            output = result.stdout or result.stderr
            return output
        except subprocess.TimeoutExpired:
            print("ERROR: tcpdump timed out.", file=sys.stderr)
            sys.exit(1)
        except FileNotFoundError:
            print("ERROR: tcpdump not found. Install it with: sudo apt install tcpdump",
                  file=sys.stderr)
            sys.exit(1)

    def _parse_output(self, output: str) -> list[Packet]:
        """Parse tcpdump -tt -nn -X output into Packet objects."""
        packets = []
        lines = output.split("\n")

        i = 0
        while i < len(lines):
            line = lines[i].strip()
            if not line:
                i += 1
                continue

            # Check for summary line
            m = re.match(
                r"^(\d+\.\d+)\s+IP\s+"
                r"(\d+\.\d+\.\d+\.\d+)\.(\d+)\s+>\s+"
                r"(\d+\.\d+\.\d+\.\d+)\.(\d+):\s*"
                r"(.*)",
                line
            )
            if not m:
                # Might be an ARP or other non-IP packet
                i += 1
                continue

            ts = float(m.group(1))
            src_ip = m.group(2)
            src_port = int(m.group(3))
            dst_ip = m.group(4)
            dst_port = int(m.group(5))
            rest = m.group(6)

            # Determine protocol — check for UDP keyword in the rest of the line
            protocol = "UDP" if "UDP" in rest else "TCP"

            # Extract flags and length
            flags = ""
            length = 0

            flags_m = re.search(r"Flags\s+\[([^\]]*)\]", rest)
            if flags_m:
                flags = flags_m.group(1)

            length_m = re.search(r"length\s+(\d+)", rest)
            if length_m:
                length = int(length_m.group(1))

            # Extract sequence number for TCP
            seq = 0
            seq_m = re.search(r"seq\s+(\d+):(\d+)", rest)
            if seq_m:
                seq = int(seq_m.group(1))

            # Now read the hex dump lines that follow.
            # tcpdump -X format: hex line, then optional ASCII line, then next hex line.
            # The ASCII line must NOT look like a new packet summary (no IP address pattern).
            ip_summary_pattern = re.compile(r"^\d+\.\d+\s+IP\s+")
            payload_lines = []
            j = i + 1
            while j < len(lines):
                hex_line = lines[j]
                # Check if it's a hex dump line: starts with whitespace + 0x
                if re.match(r"^\s+0x[0-9a-fA-F]+:", hex_line):
                    payload_lines.append(hex_line)
                    # Next line is ASCII representation if it exists, is non-empty,
                    # doesn't start another hex dump, and doesn't start a new packet.
                    if (j + 1 < len(lines)
                            and not re.match(r"^\s+0x[0-9a-fA-F]+:", lines[j + 1])
                            and lines[j + 1].strip()
                            and not ip_summary_pattern.match(lines[j + 1].strip())):
                        j += 1
                    j += 1
                else:
                    break

            # Extract raw payload bytes from hex dump
            payload = self._extract_payload(payload_lines)

            # Truncate to declared length
            if len(payload) > length:
                payload = payload[:length]

            packets.append(Packet(
                timestamp=ts,
                src_ip=src_ip,
                src_port=src_port,
                dst_ip=dst_ip,
                dst_port=dst_port,
                protocol=protocol,
                flags=flags,
                seq=seq,
                length=length,
                payload=payload,
            ))
            i = j

        return packets

    def _extract_payload(self, hex_lines: list[str]) -> bytes:
        """Extract raw bytes from tcpdump hex dump lines.

        tcpdump -X outputs hex as 2-character groups separated by spaces
        in the hex column, followed by the ASCII column. We stop extracting
        at the first non-hex token to avoid accidentally consuming ASCII
        representation characters.
        """
        result = bytearray()
        for line in hex_lines:
            # Line format: "    0x0000:  4500 008b 0001 0000  ..."
            m = re.search(r"0x[0-9a-f]+:\s+(.*)", line)
            if not m:
                continue
            hex_part = m.group(1)
            # Extract only 2-char hex pairs; stop at first non-hex token
            for token in hex_part.split():
                token = token.strip()
                if len(token) == 2 and all(c in "0123456789abcdefABCDEF" for c in token):
                    try:
                        result.append(int(token, 16))
                    except ValueError:
                        break
                else:
                    # Hit non-hex token (likely the ASCII column) — done with this line
                    break
        return bytes(result)

    def _build_streams(self) -> dict[str, Stream]:
        """Group packets into streams by 4-tuple."""
        streams: dict[str, Stream] = {}

        # Filter to camera traffic if camera_ip is specified
        filtered = self.packets
        if self.camera_ip:
            filtered = [
                p for p in self.packets
                if p.src_ip == self.camera_ip or p.dst_ip == self.camera_ip
            ]

        # Group by (src_ip, src_port, dst_ip, dst_port, protocol).
        # Normalize key for dedup but preserve the actual IP roles using camera_ip.
        cam_ip = self.camera_ip
        for pkt in filtered:
            # Normalize key so both directions end up in the same stream
            if (pkt.src_ip, pkt.src_port) < (pkt.dst_ip, pkt.dst_port):
                key = f"{pkt.src_ip}:{pkt.src_port}->{pkt.dst_ip}:{pkt.dst_port}/{pkt.protocol}"
                if key not in streams:
                    streams[key] = Stream(
                        key=key,
                        src_ip=pkt.src_ip,
                        src_port=pkt.src_port,
                        dst_ip=pkt.dst_ip,
                        dst_port=pkt.dst_port,
                        protocol=pkt.protocol,
                        camera_ip=cam_ip or "",
                    )
            else:
                key = f"{pkt.dst_ip}:{pkt.dst_port}->{pkt.src_ip}:{pkt.src_port}/{pkt.protocol}"
                if key not in streams:
                    streams[key] = Stream(
                        key=key,
                        src_ip=pkt.dst_ip,
                        src_port=pkt.dst_port,
                        dst_ip=pkt.src_ip,
                        dst_port=pkt.src_port,
                        protocol=pkt.protocol,
                        camera_ip=cam_ip or "",
                    )

            streams[key].packets.append(pkt)

        # Sort packets within each stream by timestamp
        for stream in streams.values():
            stream.packets.sort(key=lambda p: p.timestamp)

        return streams


# ═══════════════════════════════════════════════════════════════════════════
# STREAM ANALYZER — Identifies check-in candidates and extracts templates
# ═══════════════════════════════════════════════════════════════════════════

@dataclass
class VariableRange:
    """A byte range in a payload that contains camera-specific data."""
    offset: int
    length: int
    field_name: str
    original_value_hex: str
    original_value_ascii: str


@dataclass
class ConversationStep:
    """One step in a protocol conversation (a send or receive)."""
    direction: str  # "send" or "recv"
    payload_hex: str
    payload_ascii: str
    variable_ranges: list = field(default_factory=list)
    timing_delta_ms: float = 0.0  # time since previous step


@dataclass
class ProtocolTemplate:
    """A captured and annotated protocol conversation template."""
    source_pcap: str
    camera_ip: str
    camera_serial: str = ""
    camera_mac: str = ""
    camera_model: str = ""
    protocol: str = "TCP"
    dst_ip: str = ""
    dst_port: int = 0
    steps: list = field(default_factory=list)
    capture_ts: str = ""

    def to_dict(self) -> dict:
        return {
            "source_pcap": self.source_pcap,
            "camera_ip": self.camera_ip,
            "camera_serial": self.camera_serial,
            "camera_mac": self.camera_mac,
            "camera_model": self.camera_model,
            "protocol": self.protocol,
            "dst_ip": self.dst_ip,
            "dst_port": self.dst_port,
            "capture_ts": self.capture_ts,
            "steps": [
                {
                    "direction": s.direction,
                    "payload_hex": s.payload_hex,
                    "payload_ascii": s.payload_ascii,
                    "timing_delta_ms": s.timing_delta_ms,
                    "variable_ranges": [
                        {
                            "offset": v.offset,
                            "length": v.length,
                            "field_name": v.field_name,
                            "original_value_hex": v.original_value_hex,
                        }
                        for v in s.variable_ranges
                    ],
                }
                for s in self.steps
            ],
        }

    @classmethod
    def from_dict(cls, d: dict) -> "ProtocolTemplate":
        template = cls(
            source_pcap=d.get("source_pcap", ""),
            camera_ip=d.get("camera_ip", ""),
            camera_serial=d.get("camera_serial", ""),
            camera_mac=d.get("camera_mac", ""),
            camera_model=d.get("camera_model", ""),
            protocol=d.get("protocol", "TCP"),
            dst_ip=d.get("dst_ip", ""),
            dst_port=d.get("dst_port", 0),
            capture_ts=d.get("capture_ts", ""),
        )
        for step_d in d.get("steps", []):
            step = ConversationStep(
                direction=step_d["direction"],
                payload_hex=step_d["payload_hex"],
                payload_ascii=step_d.get("payload_ascii", ""),
                timing_delta_ms=step_d.get("timing_delta_ms", 0.0),
                variable_ranges=[
                    VariableRange(
                        offset=v["offset"],
                        length=v["length"],
                        field_name=v["field_name"],
                        original_value_hex=v["original_value_hex"],
                        original_value_ascii="",
                    )
                    for v in step_d.get("variable_ranges", [])
                ],
            )
            template.steps.append(step)
        return template


class StreamAnalyzer:
    """
    Analyzes captured streams to identify which one is the EseeCloud check-in
    and extracts a replayable protocol template.
    """

    # Known EseeCloud-related ports (common across IP camera ecosystems)
    ESEECLOUD_PORTS = {8800, 35000, 37777, 37778, 34567, 12366, 15001, 15002,
                       15003, 15004, 18004, 34569, 5000, 5050, 5540, 7050,
                       8050, 9050, 10080, 18080, 19080, 20000, 25000}

    def __init__(self, streams: list[Stream], camera_ip: str,
                 device_info: Optional[dict] = None):
        self.streams = streams
        self.camera_ip = camera_ip
        self.device_info = device_info or {}

    def find_checkin_candidates(self) -> list[Stream]:
        """
        Score streams by likelihood of being the EseeCloud check-in.
        Returns candidates sorted by score (highest first).
        """
        scored = []
        for stream in self.streams:
            score = 0

            # Must have outbound packets
            has_outbound = any(
                p.src_ip == self.camera_ip and p.length > 0
                for p in stream.packets
            )
            if not has_outbound:
                continue

            # Prefer streams to external (non-private) IPs
            if stream.is_outbound:
                score += 10

            # Prefer streams on known camera/cloud ports
            if stream.dst_port in self.ESEECLOUD_PORTS:
                score += 20

            # Prefer streams with payload data (not just TCP handshakes)
            payload_packets = [p for p in stream.packets if p.length > 0]
            if len(payload_packets) > 0:
                score += len(payload_packets) * 2

            # Prefer streams that have both send and receive with payload
            has_send_payload = any(
                p.src_ip == self.camera_ip and p.length > 0
                for p in stream.packets
            )
            has_recv_payload = any(
                p.dst_ip == self.camera_ip and p.length > 0
                for p in stream.packets
            )
            if has_send_payload and has_recv_payload:
                score += 15

            # Prefer streams that aren't HTTP/HTTPS (port 80/443 — likely web UI)
            if stream.dst_port not in (80, 443, 8080, 8443):
                score += 5

            # Prefer streams that aren't DNS (port 53)
            if stream.dst_port not in (53, 5353):
                score += 5

            # Prefer streams with short duration (< 5s = typical check-in)
            if stream.duration < 5.0:
                score += 5

            scored.append((score, stream))

        scored.sort(key=lambda x: x[0], reverse=True)
        return [s for _, s in scored]

    def print_stream_summary(self, stream: Stream, index: int):
        """Print a one-line summary of a stream."""
        has_send = any(p.src_ip == self.camera_ip and p.length > 0
                       for p in stream.packets)
        has_recv = any(p.dst_ip == self.camera_ip and p.length > 0
                       for p in stream.packets)

        direction = "→"
        if has_send and has_recv:
            direction = "⇄"
        elif has_recv:
            direction = "←"

        preview = ""
        for p in stream.packets:
            if p.length > 0 and p.src_ip == self.camera_ip:
                preview = p.payload[:20].hex()
                break

        is_cloud = "☁" if stream.is_outbound else " "

        print(
            f"  [{index:2d}] {is_cloud} {direction} "
            f"{stream.src_ip}:{stream.src_port} → {stream.dst_ip}:{stream.dst_port} "
            f"({stream.protocol})  "
            f"pkts:{stream.packet_count:3d}  "
            f"sent:{stream.total_bytes_sent:5d}B  "
            f"recv:{stream.total_bytes_recv:5d}B  "
            f"dur:{stream.duration:.2f}s  "
            f"first:{preview}"
        )

    def print_stream_detail(self, stream: Stream):
        """Print detailed hex dump of a stream's conversation."""
        print(f"\n{'='*70}")
        print(f"Stream: {stream.key}")
        print(f"Duration: {stream.duration:.4f}s  Packets: {stream.packet_count}")
        print(f"Sent: {stream.total_bytes_sent}B  Recv: {stream.total_bytes_recv}B")
        print(f"{'='*70}")

        for i, pkt in enumerate(stream.packets):
            direction = "SEND" if pkt.src_ip == self.camera_ip else "RECV"
            marker = ">>>" if direction == "SEND" else "<<<"

            if pkt.length == 0:
                print(f"\n  [{i}] {marker} {direction} (TCP control: {pkt.flags})")
                continue

            print(f"\n  [{i}] {marker} {direction} {pkt.length}B  "
                  f"ts={pkt.timestamp:.6f}  flags={pkt.flags}")
            self._print_hex_dump(pkt.payload)

    def _print_hex_dump(self, data: bytes, bytes_per_line: int = 16):
        """Print a hex+ASCII dump of data."""
        for offset in range(0, len(data), bytes_per_line):
            chunk = data[offset:offset + bytes_per_line]
            hex_str = " ".join(f"{b:02x}" for b in chunk)
            ascii_str = "".join(
                chr(b) if 32 <= b < 127 else "."
                for b in chunk
            )
            print(f"    {offset:04x}  {hex_str:<{bytes_per_line*3}}  |{ascii_str}|")

    def detect_variable_ranges(self, payload: bytes,
                                direction: str) -> list[VariableRange]:
        """
        Find byte ranges in payload that match known camera identity values
        (serial, MAC, model) from device_info.
        """
        ranges = []
        search_values = {}

        # Gather identity values to search for
        serial = self.device_info.get("serialNumber", "")
        if serial:
            search_values["serial"] = serial.encode("utf-8")

        # Search for MAC in various formats
        mac = self.device_info.get("macAddress", "")
        if mac:
            # Try colon-separated ASCII
            search_values["mac_ascii"] = mac.encode("ascii")
            # Try raw bytes
            try:
                mac_bytes = bytes(int(b, 16) for b in mac.split(":") if b)
                search_values["mac_raw"] = mac_bytes
            except (ValueError, AttributeError):
                pass

        model = self.device_info.get("model", "")
        if model:
            search_values["model"] = model.encode("utf-8")

        # Search for each identity value in the payload
        for field_name, search_bytes in search_values.items():
            if not search_bytes:
                continue
            start = 0
            while True:
                pos = payload.find(search_bytes, start)
                if pos == -1:
                    break
                ranges.append(VariableRange(
                    offset=pos,
                    length=len(search_bytes),
                    field_name=field_name,
                    original_value_hex=search_bytes.hex(),
                    original_value_ascii=search_bytes.decode("ascii", errors="replace"),
                ))
                start = pos + 1

        return sorted(ranges, key=lambda r: r.offset)

    def extract_template(self, stream: Stream) -> ProtocolTemplate:
        """Extract a ProtocolTemplate from a stream."""
        # Determine the actual external IP (the one the camera is talking to)
        if stream.is_outbound:
            dst_ip = stream.dst_ip
        else:
            dst_ip = stream.src_ip

        template = ProtocolTemplate(
            source_pcap="",
            camera_ip=self.camera_ip,
            camera_serial=self.device_info.get("serialNumber", ""),
            camera_mac=self.device_info.get("macAddress", ""),
            camera_model=self.device_info.get("model", ""),
            protocol=stream.protocol,
            dst_ip=dst_ip,
            dst_port=stream.dst_port if stream.is_outbound else stream.src_port,
            capture_ts=datetime.now(timezone.utc).isoformat(),
        )

        prev_ts = None
        for pkt in stream.packets:
            if pkt.length == 0 and pkt.protocol == "TCP":
                # Skip pure control packets (SYN, ACK-only) in template
                # but note them if they're the first packet
                if prev_ts is None:
                    prev_ts = pkt.timestamp
                continue

            direction = "send" if pkt.src_ip == self.camera_ip else "recv"
            timing_delta = 0.0
            if prev_ts is not None:
                timing_delta = (pkt.timestamp - prev_ts) * 1000.0  # ms

            variable_ranges = self.detect_variable_ranges(pkt.payload, direction)

            step = ConversationStep(
                direction=direction,
                payload_hex=pkt.payload.hex(),
                payload_ascii=pkt.payload.decode("ascii", errors="replace"),
                variable_ranges=variable_ranges,
                timing_delta_ms=round(timing_delta, 2),
            )
            template.steps.append(step)
            prev_ts = pkt.timestamp

        return template


# ═══════════════════════════════════════════════════════════════════════════
# CHECK-IN FORGER — Replays/forges check-in packets
# ═══════════════════════════════════════════════════════════════════════════

class CheckinForger:
    """
    Replays or forges EseeCloud check-in packets using a ProtocolTemplate.
    Supports identity substitution for locked cameras.
    """

    def __init__(self, template: ProtocolTemplate):
        self.template = template

    def forge(self, substitutions: dict, dry_run: bool = False,
              timeout: float = 10.0) -> dict:
        """
        Send the forged check-in.

        Args:
            substitutions: dict mapping field_name -> new value
                e.g. {"serial": "NEWSERIAL123", "mac_ascii": "00:11:22:33:44:55"}
            dry_run: if True, don't actually send packets
            timeout: socket timeout in seconds

        Returns:
            dict with results: {"sent": [...], "received": [...], "errors": [...]}
        """
        results = {"sent": [], "received": [], "errors": []}

        if dry_run:
            print("\n*** DRY RUN — no packets will be sent ***\n")
            for step in self.template.steps:
                payload = self._apply_substitutions(step, substitutions)
                print(f"  [{step.direction.upper()}] {len(payload)}B "
                      f"(after {step.timing_delta_ms:.0f}ms delay)")
                self._print_hex_dump_small(payload)
            return results

        sock = None
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.settimeout(timeout)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

            print(f"Connecting to {self.template.dst_ip}:{self.template.dst_port}...")
            sock.connect((self.template.dst_ip, self.template.dst_port))
            print(f"Connected.")

            for i, step in enumerate(self.template.steps):
                # Apply timing
                if step.timing_delta_ms > 0 and i > 0:
                    time.sleep(step.timing_delta_ms / 1000.0)

                if step.direction == "send":
                    payload = self._apply_substitutions(step, substitutions)
                    sock.sendall(payload)
                    sent_info = f"[{i}] SENT {len(payload)}B"
                    results["sent"].append({"index": i, "length": len(payload)})
                    print(sent_info)
                    self._print_hex_dump_small(payload)

                elif step.direction == "recv":
                    expected_len = len(bytes.fromhex(step.payload_hex))
                    try:
                        data = sock.recv(max(expected_len * 2, 4096))
                        if data:
                            recv_info = f"[{i}] RECV {len(data)}B"
                            results["received"].append(
                                {"index": i, "length": len(data), "hex": data.hex()}
                            )
                            print(recv_info)
                            self._print_hex_dump_small(data)
                            # Compare with expected
                            expected = bytes.fromhex(step.payload_hex)
                            if data != expected:
                                print(f"       ⚠ Response differs from capture!")
                        else:
                            print(f"[{i}] RECV (connection closed by server)")
                    except socket.timeout:
                        print(f"[{i}] RECV (timeout — server didn't respond)")
                        results["errors"].append(
                            {"index": i, "error": "timeout waiting for response"}
                        )

        except Exception as e:
            results["errors"].append({"error": str(e)})
            print(f"ERROR: {e}")
        finally:
            if sock:
                sock.close()

        return results

    def _apply_substitutions(self, step: ConversationStep,
                              substitutions: dict) -> bytes:
        """Apply identity substitutions to a payload."""
        payload = bytearray(bytes.fromhex(step.payload_hex))

        for vr in step.variable_ranges:
            field_name_simple = vr.field_name.split("_")[0]  # "mac_ascii" → "mac"
            for sub_key, sub_val in substitutions.items():
                if sub_key == vr.field_name or sub_key == field_name_simple:
                    new_bytes = self._encode_substitution(sub_val, vr.length)
                    if new_bytes and len(new_bytes) == vr.length:
                        payload[vr.offset:vr.offset + vr.length] = new_bytes
                        print(f"       ↳ Substituted {vr.field_name} @ offset {vr.offset}: "
                              f"{vr.original_value_hex} → {new_bytes.hex()}")
                    else:
                        print(f"       ⚠ Could not encode '{sub_val}' for "
                              f"{vr.field_name} (need {vr.length} bytes)")

        return bytes(payload)

    def _encode_substitution(self, value: str, length: int) -> Optional[bytes]:
        """Encode a substitution value into bytes of the given length.

        Handles hex strings, colon/dash-separated hex (MAC addresses),
        and plain ASCII/UTF-8 values.
        """
        # Strip separators (colons, dashes) and try as hex — handles MAC addresses
        stripped = value.replace(":", "").replace("-", "").replace(" ", "")
        if all(c in "0123456789abcdefABCDEF" for c in stripped):
            try:
                result = bytes.fromhex(stripped)
                if len(result) == length:
                    return result
            except ValueError:
                pass

        # Try as ASCII
        ascii_bytes = value.encode("ascii", errors="replace")
        if len(ascii_bytes) == length:
            return ascii_bytes

        # Pad or truncate ASCII
        if len(ascii_bytes) < length:
            return ascii_bytes + b"\x00" * (length - len(ascii_bytes))
        elif len(ascii_bytes) > length:
            return ascii_bytes[:length]

        return None

    def _print_hex_dump_small(self, data: bytes):
        """Print a compact hex dump."""
        for offset in range(0, len(data), 16):
            chunk = data[offset:offset + 16]
            hex_str = " ".join(f"{b:02x}" for b in chunk)
            ascii_str = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
            print(f"       {offset:04x}  {hex_str:<48s}  |{ascii_str}|")


# ═══════════════════════════════════════════════════════════════════════════
# CLI
# ═══════════════════════════════════════════════════════════════════════════

def cmd_streams(args):
    """List all streams in a pcap."""
    print(f"Reading {args.pcap}...")
    reader = PcapReader(args.pcap, camera_ip=args.camera_ip)
    streams = reader.parse()

    if not streams:
        print("No streams found.")
        return

    analyzer = StreamAnalyzer(streams, args.camera_ip or "unknown")
    candidates = analyzer.find_checkin_candidates()

    print(f"\nFound {len(streams)} streams.\n")

    if candidates:
        print("── Check-in candidates (scored by likelihood) ──")
        for i, stream in enumerate(candidates[:20]):
            analyzer.print_stream_summary(stream, i + 1)

    # List remaining streams
    candidate_keys = {s.key for s in candidates}
    others = [s for s in streams if s.key not in candidate_keys]
    if others:
        print(f"\n── Other streams ({len(others)}) ──")
        for i, stream in enumerate(others[:10], len(candidates) + 1):
            analyzer.print_stream_summary(stream, i)


def cmd_analyze(args):
    """Analyze pcap and extract protocol template."""
    reader = PcapReader(args.pcap, camera_ip=args.camera_ip)
    streams = reader.parse()

    if not streams:
        print("ERROR: No streams found in pcap. Is the camera_ip correct?")
        sys.exit(1)

    # Load device info if provided
    device_info = {}
    if args.device_info:
        try:
            with open(args.device_info) as f:
                device_info = json.load(f)
            print(f"Loaded device info: serial={device_info.get('serialNumber', '?')} "
                  f"model={device_info.get('model', '?')}")
        except (json.JSONDecodeError, FileNotFoundError) as e:
            print(f"WARNING: Could not load device info: {e}")

    analyzer = StreamAnalyzer(streams, args.camera_ip, device_info)
    candidates = analyzer.find_checkin_candidates()

    if not candidates:
        print("ERROR: No check-in candidates found. Try providing --camera-ip.")
        sys.exit(1)

    # Show candidates
    print(f"\nFound {len(candidates)} potential check-in streams:\n")
    for i, stream in enumerate(candidates[:20]):
        analyzer.print_stream_summary(stream, i + 1)

    # Auto-select or prompt
    if args.stream_index is not None:
        idx = args.stream_index - 1
        if 0 <= idx < len(candidates):
            selected = candidates[idx]
        else:
            print(f"ERROR: stream index {args.stream_index} out of range.")
            sys.exit(1)
    elif args.auto:
        selected = candidates[0]
        print(f"\nAuto-selected stream [1] (highest score).")
    else:
        print("\nEnter stream number to analyze (or 'q' to quit): ", end="")
        choice = sys.stdin.readline().strip()
        if choice.lower() == 'q':
            return
        try:
            idx = int(choice) - 1
            if 0 <= idx < len(candidates):
                selected = candidates[idx]
            else:
                print("Invalid selection.")
                return
        except ValueError:
            print("Invalid input.")
            return

    # Show detailed view
    analyzer.print_stream_detail(selected)

    # Extract template
    template = analyzer.extract_template(selected)
    template.source_pcap = args.pcap

    # Print variable ranges found
    all_vars = []
    for step in template.steps:
        all_vars.extend(step.variable_ranges)

    if all_vars:
        print(f"\n── Detected camera identity fields ({len(all_vars)}) ──")
        for vr in all_vars:
            print(f"  offset={vr.offset:4d}  len={vr.length:2d}  "
                  f"field={vr.field_name:15s}  value={vr.original_value_ascii}")

    # Save template
    out_path = args.output or f"eseecloud-template-{template.dst_ip}-{template.dst_port}.json"
    template_dict = template.to_dict()
    with open(out_path, "w") as f:
        json.dump(template_dict, f, indent=2)
    print(f"\nTemplate saved to: {out_path}")
    print(f"  Protocol: {template.protocol}")
    print(f"  Server:   {template.dst_ip}:{template.dst_port}")
    print(f"  Steps:    {len(template.steps)} ({sum(1 for s in template.steps if s.direction == 'send')} send, "
          f"{sum(1 for s in template.steps if s.direction == 'recv')} recv)")

    if all_vars:
        print(f"\nTo forge a check-in for a locked camera:")
        print(f"  python3 scripts/eseecloud-forge.py forge {out_path} \\")
        for vr in all_vars:
            field_simple = vr.field_name.split("_")[0]
            print(f"    --{field_simple} <{field_simple}> \\")
        print(f"    [--dry-run]")


def cmd_forge(args):
    """Forge and send a check-in using a saved template."""
    with open(args.template) as f:
        template_dict = json.load(f)

    template = ProtocolTemplate.from_dict(template_dict)

    print(f"Loaded template: {template.dst_ip}:{template.dst_port}")
    print(f"  Original camera: serial={template.camera_serial} mac={template.camera_mac}")
    print(f"  Steps: {len(template.steps)}")

    # Build substitutions from CLI args
    substitutions = {}
    if args.serial:
        substitutions["serial"] = args.serial
    if args.mac:
        substitutions["mac"] = args.mac
        substitutions["mac_ascii"] = args.mac
        substitutions["mac_raw"] = args.mac
    if args.model:
        substitutions["model"] = args.model

    replay_exact = getattr(args, 'replay_exact', False)

    if not substitutions and not replay_exact:
        print("\nWARNING: No substitutions provided. Will replay the EXACT captured check-in.")
        print("This will impersonate the original camera. Use --serial and --mac to forge.")
        print("Add --dry-run to preview without sending.\n")

    forger = CheckinForger(template)

    if replay_exact:
        # Replay exact — no substitutions
        substitutions = {}

    results = forger.forge(substitutions, dry_run=args.dry_run, timeout=args.timeout)

    print(f"\n── Results ──")
    print(f"  Sent:     {len(results['sent'])} packets")
    print(f"  Received: {len(results['received'])} packets")
    print(f"  Errors:   {len(results['errors'])}")
    if results["errors"]:
        for err in results["errors"]:
            print(f"    - {err.get('error', str(err))}")


def cmd_replay(args):
    """Replay the exact captured check-in (same as forge without substitutions)."""
    args.replay_exact = True
    cmd_forge(args)


def main():
    parser = argparse.ArgumentParser(
        description="EseeCloud boot check-in protocol analyzer and forger"
    )
    sub = parser.add_subparsers(dest="command", required=True)

    # ── streams ──
    p_streams = sub.add_parser("streams", help="List all streams in a pcap")
    p_streams.add_argument("pcap", help="Path to pcap file")
    p_streams.add_argument("--camera-ip", help="Filter to this IP")

    # ── analyze ──
    p_analyze = sub.add_parser("analyze", help="Analyze pcap and extract check-in template")
    p_analyze.add_argument("pcap", help="Path to pcap file")
    p_analyze.add_argument("--camera-ip", required=True,
                           help="Camera IP address (required)")
    p_analyze.add_argument("--device-info", help="Path to deviceInfo JSON (for auto-detecting identity fields)")
    p_analyze.add_argument("--stream-index", type=int, help="Select stream by number (skip interactive)")
    p_analyze.add_argument("--auto", action="store_true", help="Auto-select highest-scored stream")
    p_analyze.add_argument("-o", "--output", help="Output template path")

    # ── forge ──
    p_forge = sub.add_parser("forge", help="Forge and send a check-in")
    p_forge.add_argument("template", help="Path to template JSON")
    p_forge.add_argument("--serial", help="Camera serial number to inject")
    p_forge.add_argument("--mac", help="Camera MAC address to inject")
    p_forge.add_argument("--model", help="Camera model to inject")
    p_forge.add_argument("--dry-run", action="store_true", help="Preview without sending")
    p_forge.add_argument("--timeout", type=float, default=10.0, help="Socket timeout (seconds)")
    p_forge.add_argument("--replay-exact", action="store_true",
                         help=argparse.SUPPRESS)  # Used by 'replay' command

    # ── replay ──
    p_replay = sub.add_parser("replay", help="Replay the exact captured check-in")
    p_replay.add_argument("template", help="Path to template JSON")
    p_replay.add_argument("--dry-run", action="store_true", help="Preview without sending")
    p_replay.add_argument("--timeout", type=float, default=10.0, help="Socket timeout (seconds)")

    args = parser.parse_args()

    if args.command == "streams":
        cmd_streams(args)
    elif args.command == "analyze":
        cmd_analyze(args)
    elif args.command == "forge":
        cmd_forge(args)
    elif args.command == "replay":
        cmd_replay(args)


if __name__ == "__main__":
    main()
