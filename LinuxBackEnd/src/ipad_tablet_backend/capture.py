from __future__ import annotations

import asyncio
import json
import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import AsyncIterator


@dataclass(slots=True)
class CaptureOptions:
    source: str = "auto"
    output: str | None = None
    width: int = 1920
    height: int = 1080
    source_width: int | None = None
    source_height: int | None = None
    offset_x: int = 0
    offset_y: int = 0
    fps: int = 60
    bitrate: int = 16_000_000
    encoder: str = "auto"
    vaapi_device: str | None = None
    rate_control: str = "cbr"
    gaming_mode: bool = False


class AnnexBAccessUnitParser:
    """Incrementally groups Annex-B NAL units using encoder-inserted AUDs."""

    def __init__(self) -> None:
        self._buffer = bytearray()
        self._access_unit = bytearray()

    def feed(self, chunk: bytes) -> list[bytes]:
        self._buffer.extend(chunk)
        starts = self._start_codes(self._buffer)
        if len(starts) < 2:
            return []
        complete = []
        for index in range(len(starts) - 1):
            start, prefix = starts[index]
            end = starts[index + 1][0]
            complete.extend(self._consume(bytes(self._buffer[start:end]), prefix))
        last = starts[-1][0]
        del self._buffer[:last]
        return complete

    def finish(self) -> list[bytes]:
        output: list[bytes] = []
        starts = self._start_codes(self._buffer)
        if starts:
            output.extend(self._consume(bytes(self._buffer[starts[0][0]:]), starts[0][1]))
        self._buffer.clear()
        if self._access_unit:
            output.append(bytes(self._access_unit))
            self._access_unit.clear()
        return output

    def _consume(self, nal: bytes, prefix_length: int) -> list[bytes]:
        if len(nal) <= prefix_length:
            return []
        nal_type = nal[prefix_length] & 0x1F
        output: list[bytes] = []
        if nal_type == 9 and self._access_unit:
            output.append(bytes(self._access_unit))
            self._access_unit.clear()
        self._access_unit.extend(nal)
        return output

    @staticmethod
    def _start_codes(data: bytes | bytearray) -> list[tuple[int, int]]:
        starts: list[tuple[int, int]] = []
        index = 0
        while index + 3 <= len(data):
            if index + 4 <= len(data) and data[index:index + 4] == b"\x00\x00\x00\x01":
                starts.append((index, 4))
                index += 4
            elif data[index:index + 3] == b"\x00\x00\x01":
                starts.append((index, 3))
                index += 3
            else:
                index += 1
        return starts


def discover_output_geometry(output: str | None) -> tuple[int, int, int, int] | None:
    if not output or not shutil.which("hyprctl"):
        return None
    try:
        result = subprocess.run(
            ["hyprctl", "monitors", "-j"], check=True, capture_output=True, text=True, timeout=3
        )
        monitors = json.loads(result.stdout)
        monitor = next(item for item in monitors if item.get("name") == output)
        return int(monitor["width"]), int(monitor["height"]), int(monitor["x"]), int(monitor["y"])
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, StopIteration, json.JSONDecodeError):
        return None


def select_vaapi_device(explicit: str | None) -> str | None:
    if explicit:
        return explicit if Path(explicit).exists() else None
    devices = sorted(Path("/dev/dri").glob("renderD*"))
    return str(devices[0]) if devices else None


def select_encoder(requested: str, vaapi_device: str | None) -> str:
    if requested != "auto":
        return requested
    return "h264_vaapi" if vaapi_device else "libx264"


def build_capture_command(options: CaptureOptions) -> tuple[list[str], str]:
    source = options.source
    if source == "auto":
        source = "wayland" if os.environ.get("WAYLAND_DISPLAY") and shutil.which("wf-recorder") else "x11"

    device = select_vaapi_device(options.vaapi_device)
    encoder = select_encoder(options.encoder, device)
    if encoder == "h264_vaapi" and not device:
        raise RuntimeError("h264_vaapi requested, but no VAAPI render device was found")

    rate_control = options.rate_control.lower()
    if rate_control not in {"cbr", "vbr"}:
        raise RuntimeError(f"unsupported rate control mode: {options.rate_control}")
    gop = max(1, options.fps // 2) if options.gaming_mode else options.fps
    peak_bitrate = options.bitrate if rate_control == "cbr" else options.bitrate * 3 // 2
    buffer_size = max(options.bitrate // options.fps * (2 if options.gaming_mode else options.fps), 128_000)

    if source == "wayland":
        if not shutil.which("wf-recorder"):
            raise RuntimeError("Wayland capture requires wf-recorder")
        command = ["wf-recorder", "-D", "-r", str(options.fps), "-b", "0"]
        if options.output:
            command += ["-o", options.output]
        if encoder == "h264_vaapi":
            video_filter = (
                f"scale_vaapi=w={options.width}:h={options.height}:"
                "format=nv12:out_range=full"
            )
            command += [
                "-c", "h264_vaapi", "-d", device or "", "-F", video_filter,
                "-p", "aud=1", "-p", f"g={gop}", "-p", "bf=0",
                "-p", "async_depth=1", "-p", f"b={options.bitrate}",
                "-p", f"maxrate={peak_bitrate}", "-p", f"bufsize={buffer_size}",
                "-p", f"rc_mode={rate_control.upper()}",
            ]
            if options.gaming_mode:
                command += ["-p", "quality=7"]
        else:
            video_filter = f"scale={options.width}:{options.height}:flags=fast_bilinear"
            command += [
                "-c", "libx264", "-F", video_filter,
                "-p", "preset=ultrafast", "-p", "tune=zerolatency",
                "-p", "aud=1", "-p", "repeat-headers=1", "-p", f"g={gop}",
                "-p", "bf=0", "-p", "sc_threshold=0", "-p", f"b={options.bitrate}",
                "-p", f"maxrate={peak_bitrate}", "-p", f"bufsize={buffer_size}",
            ]
        # Unlike the ffmpeg CLI, wf-recorder/libav treats "-" as a literal
        # filename. The explicit AVIO URL is required for stdout streaming.
        command += ["-m", "h264", "-f", "pipe:1"]
        return command, encoder

    if source != "x11":
        raise RuntimeError(f"unsupported capture source: {source}")
    if not shutil.which("ffmpeg"):
        raise RuntimeError("X11 capture requires ffmpeg")
    display = os.environ.get("DISPLAY", ":0")
    source_width = options.source_width or options.width
    source_height = options.source_height or options.height
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "warning", "-f", "x11grab",
        "-framerate", str(options.fps), "-video_size", f"{source_width}x{source_height}",
        "-i", f"{display}+{options.offset_x},{options.offset_y}", "-an",
    ]
    if encoder == "h264_vaapi":
        command += [
            "-vaapi_device", device or "", "-vf",
            f"format=nv12,hwupload,scale_vaapi=w={options.width}:h={options.height}",
            "-c:v", "h264_vaapi", "-rc_mode", rate_control.upper(),
            "-b:v", str(options.bitrate), "-maxrate", str(peak_bitrate),
            "-bufsize", str(buffer_size), "-async_depth", "1", "-aud", "1",
        ]
        if options.gaming_mode:
            command += ["-quality", "7"]
    else:
        command += [
            "-vf", f"scale={options.width}:{options.height}:flags=fast_bilinear",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-b:v", str(options.bitrate), "-maxrate", str(peak_bitrate),
            "-bufsize", str(buffer_size),
            "-x264-params", f"aud=1:repeat-headers=1:keyint={gop}:scenecut=0",
        ]
        if rate_control == "cbr":
            command += ["-minrate", str(options.bitrate)]
    command += ["-bf", "0", "-g", str(gop), "-f", "h264", "pipe:1"]
    return command, encoder


async def capture_access_units(command: list[str]) -> AsyncIterator[bytes]:
    process = await asyncio.create_subprocess_exec(
        *command, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE
    )
    assert process.stdout and process.stderr

    async def relay_stderr() -> None:
        while line := await process.stderr.readline():
            print(f"[capture] {line.decode(errors='replace').rstrip()}")

    stderr_task = asyncio.create_task(relay_stderr())
    parser = AnnexBAccessUnitParser()
    try:
        while chunk := await process.stdout.read(128 * 1024):
            for access_unit in parser.feed(chunk):
                yield access_unit
        for access_unit in parser.finish():
            yield access_unit
        return_code = await process.wait()
        if return_code:
            raise RuntimeError(f"capture process exited with status {return_code}")
    finally:
        if process.returncode is None:
            process.terminate()
            try:
                await asyncio.wait_for(process.wait(), timeout=3)
            except asyncio.TimeoutError:
                process.kill()
                await process.wait()
        await stderr_task
