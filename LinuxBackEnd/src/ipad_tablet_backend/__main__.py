from __future__ import annotations

import argparse
import asyncio
import json
import os
import shutil
import subprocess
import sys

from .capture import CaptureOptions, discover_output_geometry, select_vaapi_device
from .server import ServerOptions, TabletServer
from .usb import USBOptions
from .udp import UDPOptions


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(prog="ipad-tablet-backend")
    sub = root.add_subparsers(dest="command", required=True)

    serve = sub.add_parser("serve", help="start screen streaming and Pencil input")
    serve.add_argument("--host", default="0.0.0.0")
    serve.add_argument("--token", default=os.environ.get("IPAD_TABLET_TOKEN", ""))
    serve.add_argument("--source", choices=("auto", "wayland", "x11"), default="auto")
    serve.add_argument("--output", help="Wayland output name, for example DP-1 or iPadTablet")
    serve.add_argument(
        "--resolution",
        help="encoded stream size; defaults to the selected output's native resolution",
    )
    serve.add_argument("--origin", default="0,0", help="X11 capture origin")
    serve.add_argument("--fps", type=int, default=60)
    serve.add_argument("--bitrate", type=int, default=16_000_000)
    serve.add_argument("--rate-control", choices=("cbr", "vbr"), default="cbr")
    serve.add_argument("--encoder", choices=("auto", "h264_vaapi", "libx264"), default="auto")
    serve.add_argument("--vaapi-device")
    serve.add_argument("--uinput", default="/dev/uinput")
    serve.add_argument("--uhid", default="/dev/uhid")
    serve.add_argument(
        "--input-mode", choices=("otd", "uinput", "none"), default="otd",
        help="Pencil output: OpenTabletDriver UHID (default), legacy uinput, or disabled",
    )
    serve.add_argument("--no-input", action="store_true", help="stream only; do not create a tablet")
    serve.add_argument("--rotation", type=int, choices=(0, 90, 180, 270), default=0)
    serve.add_argument("--pressure-curve", type=float, default=1.0)
    serve.add_argument("--usb", action="store_true", help="also serve the iPad through usbmuxd/iproxy")
    serve.add_argument("--usb-local-port", type=int, default=18_765)
    serve.add_argument("--usb-device-port", type=int, default=18_765)
    serve.add_argument(
        "--usb-port-fallbacks", type=int, default=10,
        help="number of consecutive USB ports to probe when iPadOS reports EADDRINUSE",
    )
    serve.add_argument("--udid", help="target one specific iPad when multiple devices are attached")
    serve.add_argument("--no-udp", action="store_true", help="disable encrypted LAN UDP (USB only)")
    serve.add_argument("--udp-video-port", type=int, default=8_766)
    serve.add_argument("--udp-input-port", type=int, default=8_767)
    serve.add_argument(
        "--udp-mtu", type=int, default=1_200,
        help="maximum encrypted UDP datagram size",
    )
    serve.add_argument(
        "--otd-auto-config", action=argparse.BooleanOptionalAction, default=True,
        help="detect the virtual iPad and select OTD Absolute Mode automatically",
    )
    serve.add_argument("--otd-cli", default="otd")
    serve.add_argument("--otd-tablet", default="Apple iPad Pro (Apple Pencil)")
    serve.add_argument(
        "--otd-output-mode", default="OpenTabletDriver.Desktop.Output.AbsoluteMode"
    )

    sub.add_parser("doctor", help="check capture and input prerequisites")
    outputs = sub.add_parser("virtual-display", help="manage a Hyprland headless output")
    outputs.add_argument("action", choices=("create", "remove"))
    outputs.add_argument("--name", default="iPadTablet")
    return root


def parse_pair(value: str, separator: str, label: str) -> tuple[int, int]:
    try:
        first, second = value.lower().split(separator, 1)
        return int(first), int(second)
    except ValueError as error:
        raise SystemExit(f"invalid {label}: {value}") from error


def serve(arguments: argparse.Namespace) -> int:
    if not arguments.no_udp and len(arguments.token.encode()) < 16:
        print("Encrypted UDP requires a --token containing at least 16 UTF-8 bytes.", file=sys.stderr)
        return 2
    if not arguments.no_udp and not 576 <= arguments.udp_mtu <= 65_507:
        print("--udp-mtu must be between 576 and 65507.", file=sys.stderr)
        return 2
    requested_size = (
        parse_pair(arguments.resolution, "x", "resolution") if arguments.resolution else None
    )
    offset_x, offset_y = parse_pair(arguments.origin, ",", "origin")
    discovered = discover_output_geometry(arguments.output)
    if discovered:
        source_width, source_height, offset_x, offset_y = discovered
    else:
        source_width, source_height = requested_size or (1920, 1080)
    width, height = requested_size or (source_width, source_height)
    capture = CaptureOptions(
        source=arguments.source, output=arguments.output, width=width, height=height,
        source_width=source_width, source_height=source_height,
        offset_x=offset_x, offset_y=offset_y, fps=arguments.fps, bitrate=arguments.bitrate,
        encoder=arguments.encoder, vaapi_device=arguments.vaapi_device,
        rate_control=arguments.rate_control,
    )
    options = ServerOptions(
        host=arguments.host, token=arguments.token,
        input_mode="none" if arguments.no_input else arguments.input_mode,
        uinput_path=arguments.uinput, uhid_path=arguments.uhid, rotation=arguments.rotation,
        pressure_curve=arguments.pressure_curve, capture=capture,
        otd_auto_config=arguments.otd_auto_config,
        otd_cli=arguments.otd_cli,
        otd_tablet=arguments.otd_tablet,
        otd_output_mode=arguments.otd_output_mode,
        usb=USBOptions(
            arguments.usb_local_port, arguments.usb_device_port, arguments.udid,
            arguments.usb_port_fallbacks,
        )
        if arguments.usb else None,
        udp=None if arguments.no_udp else UDPOptions(
            arguments.udp_video_port, arguments.udp_input_port, arguments.udp_mtu
        ),
    )
    try:
        asyncio.run(TabletServer(options).run())
    except FileNotFoundError as error:
        print(f"cannot open input endpoint: {error}", file=sys.stderr)
        if arguments.input_mode == "otd" and not arguments.no_input:
            print("Install udev/99-ipad-tablet-uhid.rules and load the uhid module.", file=sys.stderr)
        return 2
    except PermissionError as error:
        endpoint = arguments.uhid if arguments.input_mode == "otd" else arguments.uinput
        print(f"cannot open {endpoint}: {error}", file=sys.stderr)
        print("Install the included udev rules or use --input-mode none.", file=sys.stderr)
        return 2
    except RuntimeError as error:
        print(str(error), file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        return 0
    return 0


def doctor() -> int:
    usb_devices: list[str] = []
    if shutil.which("idevice_id"):
        try:
            result = subprocess.run(
                ["idevice_id", "-l"], capture_output=True, text=True, timeout=5, check=False
            )
            if result.returncode == 0:
                usb_devices = [line for line in result.stdout.splitlines() if line]
        except (OSError, subprocess.SubprocessError):
            pass
    checks = {
        "ffmpeg": shutil.which("ffmpeg"),
        "wf-recorder": shutil.which("wf-recorder"),
        "hyprctl": shutil.which("hyprctl"),
        "iproxy": shutil.which("iproxy"),
        "idevice_id": shutil.which("idevice_id"),
        "usb_devices": usb_devices,
        "vaapi_device": select_vaapi_device(None),
        "uinput_exists": os.path.exists("/dev/uinput"),
        "uinput_writable": os.access("/dev/uinput", os.W_OK),
        "uhid_exists": os.path.exists("/dev/uhid"),
        "uhid_writable": os.access("/dev/uhid", os.W_OK),
        "opentabletdriver": shutil.which("otd"),
        "session_type": os.environ.get("XDG_SESSION_TYPE"),
    }
    print(json.dumps(checks, indent=2))
    return 0 if checks["ffmpeg"] and (checks["wf-recorder"] or checks["session_type"] == "x11") else 1


def virtual_display(action: str, name: str) -> int:
    if not shutil.which("hyprctl"):
        print("hyprctl was not found", file=sys.stderr)
        return 2
    verb = "create" if action == "create" else "remove"
    command = ["hyprctl", "output", verb]
    if action == "create":
        command += ["headless", name]
    else:
        command += [name]
    return subprocess.run(command, check=False).returncode


def main() -> int:
    arguments = parser().parse_args()
    if arguments.command == "serve":
        return serve(arguments)
    if arguments.command == "doctor":
        return doctor()
    return virtual_display(arguments.action, arguments.name)


if __name__ == "__main__":
    raise SystemExit(main())
