from __future__ import annotations

import asyncio
import hmac
import json
import struct
import time
from collections.abc import Awaitable, Callable, Iterable
from dataclasses import dataclass
from typing import Any, cast


MAGIC = b"IPUD"
VERSION = 1
VIDEO_PACKET = 1
METADATA_PACKET = 2
HEADER = struct.Struct("!4sBBIHH")
DEFAULT_MTU = 1200
MAX_CONTROL_SIZE = 64 * 1024

Address = tuple[str, int] | tuple[str, int, int, int]


@dataclass(slots=True)
class UDPOptions:
    video_port: int = 8_766
    input_port: int = 8_767
    mtu: int = DEFAULT_MTU
    client_timeout: float = 5.0


@dataclass(slots=True)
class UDPClient:
    address: Address
    last_seen: float


def encode_packets(
    packet_type: int, frame_id: int, payload: bytes, *, mtu: int = DEFAULT_MTU
) -> Iterable[bytes]:
    payload_size = mtu - HEADER.size
    if payload_size <= 0:
        raise ValueError("UDP MTU is smaller than the protocol header")
    chunk_count = max(1, (len(payload) + payload_size - 1) // payload_size)
    if chunk_count > 0xFFFF:
        raise ValueError("UDP frame is too large")
    for chunk_index in range(chunk_count):
        start = chunk_index * payload_size
        chunk = payload[start:start + payload_size]
        yield HEADER.pack(
            MAGIC,
            VERSION,
            packet_type,
            frame_id & 0xFFFFFFFF,
            chunk_index,
            chunk_count,
        ) + chunk


def decode_packet(data: bytes) -> tuple[int, int, int, int, bytes]:
    if len(data) < HEADER.size:
        raise ValueError("short UDP packet")
    magic, version, packet_type, frame_id, chunk_index, chunk_count = HEADER.unpack_from(data)
    if magic != MAGIC or version != VERSION:
        raise ValueError("invalid UDP packet")
    if chunk_count == 0 or chunk_index >= chunk_count:
        raise ValueError("invalid UDP chunk index")
    return packet_type, frame_id, chunk_index, chunk_count, data[HEADER.size:]


class _VideoProtocol(asyncio.DatagramProtocol):
    def __init__(self, bridge: UDPBridge) -> None:
        self.bridge = bridge

    def connection_made(self, transport: asyncio.BaseTransport) -> None:
        self.bridge.video_transport = cast(asyncio.DatagramTransport, transport)

    def datagram_received(self, data: bytes, address: Address) -> None:
        self.bridge.handle_video_datagram(data, address)

    def error_received(self, error: Exception) -> None:
        self.bridge.last_error = str(error)


class _InputProtocol(asyncio.DatagramProtocol):
    def __init__(self, bridge: UDPBridge) -> None:
        self.bridge = bridge

    def connection_made(self, transport: asyncio.BaseTransport) -> None:
        self.bridge.input_transport = cast(asyncio.DatagramTransport, transport)

    def datagram_received(self, data: bytes, address: Address) -> None:
        self.bridge.handle_input_datagram(data, address)

    def error_received(self, error: Exception) -> None:
        self.bridge.last_error = str(error)


class UDPBridge:
    """Authenticated, lossy LAN transport with separate video and input ports."""

    def __init__(
        self,
        options: UDPOptions,
        host: str,
        metadata: Callable[[], dict[str, Any]],
        token: str,
        control_handler: Callable[[dict[str, Any]], Awaitable[None]],
        release_handler: Callable[[], None],
    ) -> None:
        if not token:
            raise ValueError("UDP transport requires a non-empty authentication token")
        self.options = options
        self.host = host
        self.metadata = metadata
        self.token = token
        self.control_handler = control_handler
        self.release_handler = release_handler
        self.video_transport: asyncio.DatagramTransport | None = None
        self.input_transport: asyncio.DatagramTransport | None = None
        self.clients: dict[str, UDPClient] = {}
        self.frame_id = 0
        self.metadata_id = 0
        self.frames_sent = 0
        self.video_packets_sent = 0
        self.dropped_video_frames = 0
        self.input_packets_received = 0
        self.dropped_input_packets = 0
        self.last_error: str | None = None

    async def start(self) -> None:
        loop = asyncio.get_running_loop()
        await loop.create_datagram_endpoint(
            lambda: _VideoProtocol(self),
            local_addr=(self.host, self.options.video_port),
        )
        try:
            await loop.create_datagram_endpoint(
                lambda: _InputProtocol(self),
                local_addr=(self.host, self.options.input_port),
            )
        except BaseException:
            if self.video_transport:
                self.video_transport.close()
                self.video_transport = None
            raise
        print(
            f"UDP transport listening: video {self.host}:{self.options.video_port}, "
            f"input {self.host}:{self.options.input_port}"
        )

    def close(self) -> None:
        if self.video_transport:
            self.video_transport.close()
        if self.input_transport:
            self.input_transport.close()
        self.video_transport = None
        self.input_transport = None
        self.clients.clear()

    @property
    def connected_clients(self) -> int:
        self._expire_clients(time.monotonic())
        return len(self.clients)

    def handle_video_datagram(self, data: bytes, address: Address) -> None:
        if len(data) > MAX_CONTROL_SIZE:
            return
        try:
            message = json.loads(data)
        except (TypeError, ValueError, json.JSONDecodeError):
            return
        if not isinstance(message, dict) or message.get("type") != "hello":
            return
        supplied = message.get("token", "")
        session = message.get("session", "")
        if not isinstance(supplied, str) or not isinstance(session, str) or not session:
            return
        if len(session) > 128 or not hmac.compare_digest(self.token, supplied):
            return
        self.clients[session] = UDPClient(address=address, last_seen=time.monotonic())
        self._send_metadata(address)

    def handle_input_datagram(self, data: bytes, address: Address) -> None:
        if len(data) > MAX_CONTROL_SIZE:
            self.dropped_input_packets += 1
            return
        try:
            envelope = json.loads(data)
        except (TypeError, ValueError, json.JSONDecodeError):
            self.dropped_input_packets += 1
            return
        if not isinstance(envelope, dict) or envelope.get("type") != "input":
            self.dropped_input_packets += 1
            return
        supplied = envelope.get("token", "")
        session = envelope.get("session", "")
        payload = envelope.get("payload")
        client = self.clients.get(session) if isinstance(session, str) else None
        if (
            not isinstance(supplied, str)
            or not isinstance(session, str)
            or not isinstance(payload, dict)
            or client is None
            or address[0] != client.address[0]
            or not hmac.compare_digest(self.token, supplied)
        ):
            self.dropped_input_packets += 1
            return
        client.last_seen = time.monotonic()
        self.input_packets_received += 1
        task = asyncio.create_task(self.control_handler(payload))
        task.add_done_callback(self._control_done)

    def offer(self, access_unit: bytes) -> None:
        transport = self.video_transport
        if not transport:
            return
        self._expire_clients(time.monotonic())
        if not self.clients:
            return
        get_buffer_size = getattr(transport, "get_write_buffer_size", None)
        if callable(get_buffer_size) and get_buffer_size() > max(64 * 1024, self.options.mtu * 16):
            # Datagram transports can still buffer locally when the socket is
            # temporarily blocked. Drop a whole access unit instead of
            # allowing that buffer to turn into latency.
            self.dropped_video_frames += 1
            return
        self.frame_id = (self.frame_id + 1) & 0xFFFFFFFF
        packets = tuple(
            encode_packets(VIDEO_PACKET, self.frame_id, access_unit, mtu=self.options.mtu)
        )
        for client in tuple(self.clients.values()):
            for packet in packets:
                transport.sendto(packet, client.address)
                self.video_packets_sent += 1
            self.frames_sent += 1

    def publish_metadata(self) -> None:
        for client in tuple(self.clients.values()):
            self._send_metadata(client.address)

    def _send_metadata(self, address: Address) -> None:
        transport = self.video_transport
        if not transport:
            return
        self.metadata_id = (self.metadata_id + 1) & 0xFFFFFFFF
        payload = json.dumps(self.metadata(), separators=(",", ":")).encode()
        for packet in encode_packets(
            METADATA_PACKET, self.metadata_id, payload, mtu=self.options.mtu
        ):
            transport.sendto(packet, address)
            self.video_packets_sent += 1

    def _expire_clients(self, now: float) -> None:
        expired = [
            session
            for session, client in self.clients.items()
            if now - client.last_seen > self.options.client_timeout
        ]
        for session in expired:
            del self.clients[session]
        if expired and not self.clients:
            self.release_handler()

    def _control_done(self, task: asyncio.Task[None]) -> None:
        if task.cancelled():
            return
        try:
            task.result()
        except Exception as error:  # pragma: no cover - diagnostic path
            self.last_error = str(error)
