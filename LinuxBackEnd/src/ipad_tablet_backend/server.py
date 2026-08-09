from __future__ import annotations

import asyncio
import hmac
import json
import time
from collections import deque
from dataclasses import dataclass, replace
from typing import Any
from urllib.parse import parse_qs, urlsplit

from .capture import CaptureOptions, build_capture_command, capture_access_units
from .protocol import WebSocket, accept_websocket, read_http_request, send_http_json
from .tablet import NullTablet, TabletMapping, UInputTablet
from .uhid import UHIDTablet
from .usb import USBBridge, USBOptions
from .udp import UDPBridge, UDPOptions


@dataclass(slots=True)
class ServerOptions:
    host: str
    port: int
    token: str
    input_mode: str
    uinput_path: str
    uhid_path: str
    rotation: int
    pressure_curve: float
    capture: CaptureOptions
    usb: USBOptions | None = None
    udp: UDPOptions | None = None


class TabletServer:
    def __init__(self, options: ServerOptions) -> None:
        self.options = options
        self.clients: set[asyncio.Queue[bytes | dict[str, Any]]] = set()
        self.input_clients = 0
        self.frames = 0
        self.input_samples = 0
        self._input_sample_times: deque[float] = deque()
        self.stream_revision = 0
        self.video_enabled = True
        self.base_capture = replace(options.capture)
        self._settings_lock = asyncio.Lock()
        self.command, self.encoder = build_capture_command(options.capture)
        mapping = TabletMapping(rotation=options.rotation, pressure_curve=options.pressure_curve)
        if options.input_mode == "otd":
            self.tablet = UHIDTablet(options.uhid_path, mapping)
        elif options.input_mode == "uinput":
            self.tablet = UInputTablet(options.uinput_path, mapping)
        else:
            self.tablet = NullTablet()
        self.usb_bridge = (
            USBBridge(
                options.usb,
                self.metadata,
                options.token,
                self.tablet,
                control_handler=self.handle_input_message,
            )
            if options.usb else None
        )
        self.udp_bridge = (
            UDPBridge(
                options.udp,
                options.host,
                lambda: self.metadata,
                options.token,
                self.handle_input_message,
                self.tablet.release,
            )
            if options.udp else None
        )
        self._capture_task: asyncio.Task[None] | None = None
        self._usb_task: asyncio.Task[None] | None = None

    @property
    def metadata(self) -> dict[str, Any]:
        return {
            "type": "stream-info",
            "width": self.options.capture.width,
            "height": self.options.capture.height,
            "fps": self.options.capture.fps,
            "bitrate": self.options.capture.bitrate,
            "rateControl": self.options.capture.rate_control,
            "gamingMode": self.options.capture.gaming_mode,
            "videoEnabled": self.video_enabled,
            "streamRevision": self.stream_revision,
            "encoder": self.encoder,
        }

    async def run(self) -> None:
        server = await asyncio.start_server(self.handle_connection, self.options.host, self.options.port)
        addresses = ", ".join(str(sock.getsockname()) for sock in server.sockets or [])
        print(f"iPad Tablet backend listening on {addresses}")
        print("capture:", " ".join(self.command))
        if self.udp_bridge:
            await self.udp_bridge.start()
        if self.video_enabled:
            self._capture_task = asyncio.create_task(self.capture_loop())
        if self.usb_bridge:
            self._usb_task = asyncio.create_task(self.usb_bridge.run())
        try:
            async with server:
                await server.serve_forever()
        finally:
            tasks: list[asyncio.Task[None]] = []
            if self._capture_task:
                self._capture_task.cancel()
                tasks.append(self._capture_task)
            if self._usb_task:
                self._usb_task.cancel()
                tasks.append(self._usb_task)
            await asyncio.gather(*tasks, return_exceptions=True)
            if self.udp_bridge:
                self.udp_bridge.close()
            self.tablet.close()

    async def capture_loop(self) -> None:
        while True:
            try:
                async for access_unit in capture_access_units(self.command):
                    self.frames += 1
                    if self.usb_bridge:
                        self.usb_bridge.offer(access_unit)
                    if self.udp_bridge:
                        self.udp_bridge.offer(access_unit)
                    for queue in tuple(self.clients):
                        if queue.full():
                            try:
                                queue.get_nowait()
                            except asyncio.QueueEmpty:
                                pass
                        queue.put_nowait(access_unit)
            except asyncio.CancelledError:
                raise
            except Exception as error:
                print(f"capture failed: {error}; retrying in 2 seconds")
                await asyncio.sleep(2)

    async def handle_connection(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            request = await read_http_request(reader)
            target = urlsplit(request.target)
            if request.headers.get("upgrade", "").lower() != "websocket":
                await self.handle_http(writer, target.path)
                return
            if not self.authorized(parse_qs(target.query).get("token", [""])[0]):
                await send_http_json(writer, 401, {"error": "invalid token"})
                return
            await accept_websocket(writer, request)
            websocket = WebSocket(reader, writer)
            if target.path == "/stream":
                await self.handle_stream(websocket)
            elif target.path == "/input":
                await self.handle_input(websocket)
            else:
                await websocket.close()
        except (asyncio.IncompleteReadError, ConnectionError, ValueError):
            writer.close()
            await writer.wait_closed()

    async def handle_http(self, writer: asyncio.StreamWriter, path: str) -> None:
        if path in {"/", "/health"}:
            await send_http_json(writer, 200, {
                "status": "ok", "streamClients": len(self.clients), "frames": self.frames,
                "inputClients": self.input_clients,
                "inputMode": self.tablet.input_mode,
                "inputEvents": self.tablet.events_received,
                "inputSamples": self.input_samples,
                "inputRateHz": self.input_rate_hz,
                "usbEnabled": self.usb_bridge is not None,
                "usbConnected": self.usb_bridge.connected if self.usb_bridge else False,
                "usbFrames": self.usb_bridge.frames_sent if self.usb_bridge else 0,
                "usbDevicePort": self.usb_bridge.connected_port if self.usb_bridge else None,
                "udpEnabled": self.udp_bridge is not None,
                "udpClients": self.udp_bridge.connected_clients if self.udp_bridge else 0,
                "udpFrames": self.udp_bridge.frames_sent if self.udp_bridge else 0,
                "udpVideoPackets": self.udp_bridge.video_packets_sent if self.udp_bridge else 0,
                "udpDroppedVideoFrames": self.udp_bridge.dropped_video_frames if self.udp_bridge else 0,
                "udpInputPackets": self.udp_bridge.input_packets_received if self.udp_bridge else 0,
                "udpDroppedInputPackets": self.udp_bridge.dropped_input_packets if self.udp_bridge else 0,
                "udpError": self.udp_bridge.last_error if self.udp_bridge else None,
                **self.metadata
            })
        else:
            await send_http_json(writer, 404, {"error": "not found"})
        writer.close()
        await writer.wait_closed()

    async def handle_stream(self, websocket: WebSocket) -> None:
        queue: asyncio.Queue[bytes | dict[str, Any]] = asyncio.Queue(maxsize=1)
        self.clients.add(queue)
        try:
            await websocket.send_json(self.metadata)
            while True:
                item = await queue.get()
                if isinstance(item, bytes):
                    await websocket.send_binary(item)
                else:
                    await websocket.send_json(item)
        except (asyncio.CancelledError, ConnectionError):
            pass
        finally:
            self.clients.discard(queue)
            await websocket.close()

    async def handle_input(self, websocket: WebSocket) -> None:
        self.input_clients += 1
        try:
            while frame := await websocket.receive():
                opcode, payload = frame
                if opcode != 0x1:
                    continue
                try:
                    message = json.loads(payload)
                    if isinstance(message, dict):
                        await self.handle_input_message(message)
                except (ValueError, TypeError, json.JSONDecodeError):
                    continue
        except (asyncio.IncompleteReadError, ConnectionError, ValueError):
            pass
        finally:
            self.input_clients -= 1
            self.tablet.release()
            await websocket.close()

    async def handle_input_message(self, message: dict[str, Any]) -> None:
        message_type = message.get("type")
        if message_type == "stream-settings":
            await self.apply_stream_settings(message)
        elif message_type == "pencil-batch":
            samples = message.get("samples")
            if not isinstance(samples, list):
                return
            # A UIKit callback normally contains only a handful of coalesced
            # samples. Keep a defensive cap for untrusted network clients.
            for sample in samples[:512]:
                if isinstance(sample, dict) and sample.get("type") == "pencil":
                    self._apply_tablet_sample(sample)
        else:
            self._apply_tablet_sample(message)

    def _apply_tablet_sample(self, message: dict[str, Any]) -> None:
        if message.get("type") in {"pencil", "button"}:
            now = time.monotonic()
            self.input_samples += 1
            self._input_sample_times.append(now)
            self._expire_input_samples(now)
        self.tablet.apply(message)

    def _expire_input_samples(self, now: float) -> None:
        cutoff = now - 1.0
        while self._input_sample_times and self._input_sample_times[0] < cutoff:
            self._input_sample_times.popleft()

    @property
    def input_rate_hz(self) -> int:
        self._expire_input_samples(time.monotonic())
        return len(self._input_sample_times)

    async def apply_stream_settings(self, message: dict[str, Any]) -> None:
        async with self._settings_lock:
            gaming_mode = bool(message.get("enabled", False))
            video_enabled = bool(message.get("videoEnabled", True))
            if gaming_mode:
                width = max(640, min(3840, int(message.get("width", 1280)))) & ~1
                height = max(360, min(2160, int(message.get("height", 720)))) & ~1
                fps = max(30, min(120, int(message.get("fps", 120))))
                bitrate = max(1_000_000, min(50_000_000, int(message.get("bitrate", 8_000_000))))
                rate_control = str(message.get("rateControl", "cbr")).lower()
                if rate_control not in {"cbr", "vbr"}:
                    rate_control = "cbr"
                capture = replace(
                    self.base_capture,
                    width=width,
                    height=height,
                    fps=fps,
                    bitrate=bitrate,
                    rate_control=rate_control,
                    gaming_mode=True,
                )
            else:
                capture = replace(self.base_capture, gaming_mode=False)

            if capture == self.options.capture and video_enabled == self.video_enabled:
                return

            command, encoder = build_capture_command(capture)
            old_capture_task = self._capture_task
            self.options.capture = capture
            self.video_enabled = video_enabled
            self.command = command
            self.encoder = encoder
            self.stream_revision += 1
            print(
                "stream profile:",
                "tablet-only" if not video_enabled else "gaming" if gaming_mode else "quality",
                f"{capture.width}x{capture.height}",
                f"@ {capture.fps} FPS",
                f"{capture.bitrate // 1_000_000} Mbit/s",
                capture.rate_control.upper(),
            )

            if old_capture_task:
                old_capture_task.cancel()
                await asyncio.gather(old_capture_task, return_exceptions=True)
            self._capture_task = None
            if self.usb_bridge:
                self.usb_bridge.clear_video()
            await self.publish_metadata()
            if video_enabled:
                self._capture_task = asyncio.create_task(self.capture_loop())

    async def publish_metadata(self) -> None:
        metadata = self.metadata
        for queue in tuple(self.clients):
            while not queue.empty():
                try:
                    queue.get_nowait()
                except asyncio.QueueEmpty:
                    break
            queue.put_nowait(metadata)
        if self.usb_bridge:
            await self.usb_bridge.update_metadata(metadata)
        if self.udp_bridge:
            self.udp_bridge.publish_metadata()

    def authorized(self, supplied: str) -> bool:
        return not self.options.token or hmac.compare_digest(self.options.token, supplied)
