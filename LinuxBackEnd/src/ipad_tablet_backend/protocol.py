from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import socket
import struct
from dataclasses import dataclass
from typing import Any


WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


def configure_low_latency_writer(writer: asyncio.StreamWriter) -> None:
    transport = getattr(writer, "transport", None)
    if transport is not None:
        transport.set_write_buffer_limits(high=256 * 1024, low=64 * 1024)
    raw_socket = writer.get_extra_info("socket") if hasattr(writer, "get_extra_info") else None
    if raw_socket is not None:
        try:
            raw_socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            raw_socket.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 256 * 1024)
        except OSError:
            pass


@dataclass(slots=True)
class HttpRequest:
    method: str
    target: str
    headers: dict[str, str]


async def read_http_request(reader: asyncio.StreamReader) -> HttpRequest:
    raw = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), timeout=8)
    if len(raw) > 16_384:
        raise ValueError("HTTP headers too large")
    lines = raw.decode("latin-1").split("\r\n")
    method, target, _version = lines[0].split(" ", 2)
    headers: dict[str, str] = {}
    for line in lines[1:]:
        if not line:
            continue
        key, value = line.split(":", 1)
        headers[key.strip().lower()] = value.strip()
    return HttpRequest(method=method, target=target, headers=headers)


async def accept_websocket(writer: asyncio.StreamWriter, request: HttpRequest) -> None:
    key = request.headers.get("sec-websocket-key")
    if not key or request.headers.get("upgrade", "").lower() != "websocket":
        raise ValueError("not a WebSocket upgrade")
    accept = base64.b64encode(hashlib.sha1((key + WEBSOCKET_GUID).encode("ascii")).digest()).decode("ascii")
    writer.write(
        (
            "HTTP/1.1 101 Switching Protocols\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Accept: {accept}\r\n\r\n"
        ).encode("ascii")
    )
    await writer.drain()


async def send_http_json(writer: asyncio.StreamWriter, status: int, payload: dict[str, Any]) -> None:
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    reason = {200: "OK", 400: "Bad Request", 401: "Unauthorized", 404: "Not Found"}.get(status, "Error")
    writer.write(
        f"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\n"
        f"Content-Length: {len(body)}\r\nConnection: close\r\n\r\n".encode("ascii") + body
    )
    await writer.drain()


class WebSocket:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self.reader = reader
        self.writer = writer
        self._send_lock = asyncio.Lock()
        configure_low_latency_writer(writer)

    async def send_binary(self, payload: bytes) -> None:
        await self._send(0x2, payload)

    async def send_json(self, payload: dict[str, Any]) -> None:
        await self._send(0x1, json.dumps(payload, separators=(",", ":")).encode("utf-8"))

    async def receive(self) -> tuple[int, bytes] | None:
        while True:
            first = await self.reader.readexactly(2)
            opcode = first[0] & 0x0F
            final = bool(first[0] & 0x80)
            masked = bool(first[1] & 0x80)
            length = first[1] & 0x7F
            if not final:
                raise ValueError("fragmented WebSocket frames are not supported")
            if length == 126:
                length = struct.unpack("!H", await self.reader.readexactly(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", await self.reader.readexactly(8))[0]
            if length > 1_048_576:
                raise ValueError("WebSocket message too large")
            mask = await self.reader.readexactly(4) if masked else b""
            payload = await self.reader.readexactly(length)
            if masked:
                payload = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
            if opcode == 0x8:
                return None
            if opcode == 0x9:
                await self._send(0xA, payload)
                continue
            if opcode == 0xA:
                continue
            return opcode, payload

    async def close(self) -> None:
        try:
            await self._send(0x8, b"")
        except (ConnectionError, asyncio.CancelledError):
            pass
        self.writer.close()
        try:
            await self.writer.wait_closed()
        except ConnectionError:
            pass

    async def _send(self, opcode: int, payload: bytes) -> None:
        size = len(payload)
        if size < 126:
            header = bytes((0x80 | opcode, size))
        elif size <= 0xFFFF:
            header = bytes((0x80 | opcode, 126)) + struct.pack("!H", size)
        else:
            header = bytes((0x80 | opcode, 127)) + struct.pack("!Q", size)
        async with self._send_lock:
            self.writer.write(header + payload)
            await self.writer.drain()
