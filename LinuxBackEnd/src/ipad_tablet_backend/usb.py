from __future__ import annotations

import asyncio
import json
import shutil
import struct
from dataclasses import dataclass
from collections.abc import Awaitable, Callable
from typing import Any, Protocol

from .protocol import configure_low_latency_writer


HELLO_FRAME = 1
VIDEO_FRAME = 2
PENCIL_FRAME = 3
PING_FRAME = 4
READY_FRAME = 5
STREAM_INFO_FRAME = 6
MAX_FRAME_SIZE = 16 * 1024 * 1024


class TabletSink(Protocol):
    def apply(self, message: dict[str, Any]) -> None: ...
    def release(self) -> None: ...


@dataclass(slots=True)
class USBOptions:
    local_port: int = 18_765
    device_port: int = 18_765
    udid: str | None = None
    port_fallbacks: int = 10


def forwarded_ports(options: USBOptions) -> list[tuple[int, int]]:
    count = max(1, min(32, options.port_fallbacks))
    return [
        (options.local_port + offset, options.device_port + offset)
        for offset in range(count)
        if options.local_port + offset <= 65_535 and options.device_port + offset <= 65_535
    ]


def encode_frame(frame_type: int, payload: bytes) -> bytes:
    if len(payload) > MAX_FRAME_SIZE:
        raise ValueError("USB frame too large")
    return bytes((frame_type,)) + struct.pack("!I", len(payload)) + payload


def usb_hello(metadata: dict[str, Any]) -> dict[str, Any]:
    return {**metadata, "transport": "usb", "protocol": 1}


async def read_frame(reader: asyncio.StreamReader) -> tuple[int, bytes]:
    header = await reader.readexactly(5)
    frame_type = header[0]
    length = struct.unpack("!I", header[1:])[0]
    if length > MAX_FRAME_SIZE:
        raise ValueError("USB frame too large")
    return frame_type, await reader.readexactly(length)


class USBBridge:
    """Pushes the existing stream through iproxy and receives Pencil frames."""

    def __init__(
        self,
        options: USBOptions,
        metadata: dict[str, Any],
        token: str,
        tablet: TabletSink,
        control_handler: Callable[[dict[str, Any]], Awaitable[None]] | None = None,
    ) -> None:
        self.options = options
        self.metadata = metadata
        self.token = token
        self.tablet = tablet
        self.control_handler = control_handler
        self.queue: asyncio.Queue[bytes] = asyncio.Queue(maxsize=1)
        self.connected = False
        self.frames_sent = 0
        self.connected_port: int | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._write_lock = asyncio.Lock()
        self._iproxy: asyncio.subprocess.Process | None = None
        self._iproxy_log_task: asyncio.Task[None] | None = None

    def offer(self, access_unit: bytes) -> None:
        if self.queue.full():
            try:
                self.queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
        self.queue.put_nowait(access_unit)

    def clear_video(self) -> None:
        while not self.queue.empty():
            try:
                self.queue.get_nowait()
            except asyncio.QueueEmpty:
                break

    async def update_metadata(self, metadata: dict[str, Any]) -> None:
        self.metadata = metadata
        writer = self._writer
        if not writer:
            return
        payload = json.dumps(metadata, separators=(",", ":")).encode()
        await self._send_frame(writer, STREAM_INFO_FRAME, payload)

    async def run(self) -> None:
        if not shutil.which("iproxy"):
            raise RuntimeError("USB mode requires iproxy from libusbmuxd")
        while True:
            self._iproxy = await self._start_iproxy()
            failures = 0
            try:
                while self._iproxy.returncode is None:
                    try:
                        await self._connect_available_port()
                        failures = 0
                    except asyncio.CancelledError:
                        raise
                    except (ConnectionError, OSError, asyncio.IncompleteReadError, ValueError) as error:
                        self.connected = False
                        failures += 1
                        if failures == 1 or failures % 10 == 0:
                            print(f"USB waiting for iPad listener: {error}")
                        await asyncio.sleep(1)
            finally:
                await self._stop_iproxy()
            await asyncio.sleep(1)

    async def _connect_available_port(self) -> None:
        last_error: Exception | None = None
        for local_port, device_port in forwarded_ports(self.options):
            writer: asyncio.StreamWriter | None = None
            try:
                reader, writer = await asyncio.open_connection("127.0.0.1", local_port)
                configure_low_latency_writer(writer)
                await self._session(reader, writer, device_port=device_port)
                return
            except asyncio.CancelledError:
                raise
            except (ConnectionError, OSError, asyncio.IncompleteReadError, ValueError) as error:
                last_error = error
                if writer is not None:
                    writer.close()
                    try:
                        await writer.wait_closed()
                    except (ConnectionError, OSError):
                        pass
        if last_error is not None:
            raise last_error
        raise ConnectionError("no valid USB forwarding ports configured")

    async def _start_iproxy(self) -> asyncio.subprocess.Process:
        command = ["iproxy", "-l"]
        if self.options.udid:
            command += ["-u", self.options.udid]
        command += [f"{local}:{device}" for local, device in forwarded_ports(self.options)]
        print("USB proxy:", " ".join(command))
        process = await asyncio.create_subprocess_exec(
            *command, stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.PIPE
        )
        assert process.stderr
        self._iproxy_log_task = asyncio.create_task(self._relay_iproxy_errors(process.stderr))
        return process

    async def _relay_iproxy_errors(self, stream: asyncio.StreamReader) -> None:
        while line := await stream.readline():
            print(f"[iproxy] {line.decode(errors='replace').rstrip()}")

    async def _stop_iproxy(self) -> None:
        process = self._iproxy
        self._iproxy = None
        if process and process.returncode is None:
            process.terminate()
            try:
                await asyncio.wait_for(process.wait(), timeout=3)
            except asyncio.TimeoutError:
                process.kill()
                await process.wait()
        if self._iproxy_log_task:
            await self._iproxy_log_task
            self._iproxy_log_task = None

    async def _session(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter, *, device_port: int
    ) -> None:
        # usbmuxd pairing is the trust boundary for the cable transport. The
        # LAN token is intentionally neither required nor sent over USB.
        hello = usb_hello(self.metadata)
        writer.write(encode_frame(HELLO_FRAME, json.dumps(hello, separators=(",", ":")).encode()))
        await writer.drain()
        frame_type, _ = await asyncio.wait_for(read_frame(reader), timeout=5)
        if frame_type != READY_FRAME:
            raise ConnectionError("iPad returned an invalid USB handshake")
        self.connected = True
        self.connected_port = device_port
        self._writer = writer
        print(f"USB iPad connected on device port {device_port}")
        stream_task = asyncio.create_task(self._stream(writer))
        input_task = asyncio.create_task(self._input(reader))
        try:
            done, pending = await asyncio.wait(
                (stream_task, input_task), return_when=asyncio.FIRST_COMPLETED
            )
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
            for task in done:
                task.result()
        finally:
            stream_task.cancel()
            input_task.cancel()
            await asyncio.gather(stream_task, input_task, return_exceptions=True)
            self.connected = False
            self.connected_port = None
            self._writer = None
            self.tablet.release()
            writer.close()
            try:
                await writer.wait_closed()
            except (ConnectionError, OSError):
                pass

    async def _stream(self, writer: asyncio.StreamWriter) -> None:
        while True:
            await self._send_frame(writer, VIDEO_FRAME, await self.queue.get())
            self.frames_sent += 1

    async def _send_frame(self, writer: asyncio.StreamWriter, frame_type: int, payload: bytes) -> None:
        async with self._write_lock:
            writer.write(encode_frame(frame_type, payload))
            await writer.drain()

    async def _input(self, reader: asyncio.StreamReader) -> None:
        while True:
            frame_type, payload = await read_frame(reader)
            if frame_type == PENCIL_FRAME:
                try:
                    message = json.loads(payload)
                    if isinstance(message, dict):
                        if self.control_handler:
                            await self.control_handler(message)
                        else:
                            self.tablet.apply(message)
                except (TypeError, ValueError, json.JSONDecodeError):
                    continue
            elif frame_type == PING_FRAME:
                continue
