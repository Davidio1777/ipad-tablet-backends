from __future__ import annotations

import math
import os
import struct
from typing import Any

from .tablet import TabletMapping


UHID_DESTROY = 1
UHID_CREATE2 = 11
UHID_INPUT2 = 12
# HidSharp 2.x, which OpenTabletDriver 0.6.7 uses on Linux, does not enumerate
# hidraw endpoints announced with BUS_VIRTUAL. UHID devices may describe the
# transport they emulate, so expose this endpoint through the USB HID path.
BUS_USB = 0x03
VENDOR_ID = 0x1209
PRODUCT_ID = 0xA1D0
REPORT_ID = 1
AXIS_MAX = 32_767
PRESSURE_MAX = 8_191

# One numbered, vendor-defined input report. OpenTabletDriver's built-in
# XP_PenTabletReport reads the nine payload bytes as buttons, X, Y, pressure
# and signed X/Y tilt. Keeping the kernel descriptor vendor-defined prevents libinput
# from also handling the device and applying every Pencil event twice.
REPORT_DESCRIPTOR = bytes(
    (
        0x06, 0x00, 0xFF,  # Usage Page (Vendor Defined)
        0x09, 0x01,        # Usage 1
        0xA1, 0x01,        # Collection (Application)
        0x85, REPORT_ID,   # Report ID
        0x15, 0x00,        # Logical Minimum 0
        0x26, 0xFF, 0x00,  # Logical Maximum 255
        0x75, 0x08,        # Report Size 8
        0x95, 0x09,        # Report Count 9
        0x09, 0x01,        # Usage 1
        0x81, 0x02,        # Input (Data, Variable, Absolute)
        0xC0,              # End Collection
    )
)


def create_event() -> bytes:
    descriptor = REPORT_DESCRIPTOR.ljust(4096, b"\0")
    return struct.pack(
        "<I128s64s64sHHIIII4096s",
        UHID_CREATE2,
        b"Apple iPad Pro (Apple Pencil)",
        b"ipad-linux-tablet/uhid",
        b"ipad-pencil-network",
        len(REPORT_DESCRIPTOR),
        BUS_USB,
        VENDOR_ID,
        PRODUCT_ID,
        1,
        0,
        descriptor,
    )


def input_event(report: bytes) -> bytes:
    if len(report) != 10:
        raise ValueError("OpenTabletDriver reports must contain exactly 10 bytes")
    return struct.pack("<IH", UHID_INPUT2, len(report)) + report


class UHIDTablet:
    """Virtual raw HID tablet consumed and mapped by OpenTabletDriver."""

    input_mode = "otd"

    def __init__(self, path: str = "/dev/uhid", mapping: TabletMapping | None = None) -> None:
        self.mapping = mapping or TabletMapping()
        self.fd = os.open(path, os.O_RDWR | os.O_NONBLOCK)
        self.events_received = 0
        self._last_sequence = -1
        self._x = 0
        self._y = 0
        self._pressure = 0
        self._tilt_x = 0
        self._tilt_y = 0
        self._buttons = 0
        os.write(self.fd, create_event())

    def apply(self, message: dict[str, Any]) -> None:
        sequence = int(message.get("sequence", self._last_sequence + 1))
        if sequence <= self._last_sequence:
            return
        self._last_sequence = sequence

        if message.get("type") == "button":
            button = max(1, min(3, int(message.get("button", 1))))
            mask = 1 << button
            if bool(message.get("pressed")):
                self._buttons |= mask
            else:
                self._buttons &= ~mask
            self._emit()
            return
        if message.get("type") != "pencil":
            return

        phase = str(message.get("phase", "move"))
        x, y = self.mapping.point(float(message.get("x", 0)), float(message.get("y", 0)))
        self._x = round(x * AXIS_MAX)
        self._y = round(y * AXIS_MAX)
        pressure = self.mapping.pressure(float(message.get("pressure", 0)))
        self._pressure = 0 if phase in {"hover", "leave", "up", "cancel"} else round(pressure * PRESSURE_MAX)
        altitude = float(message.get("altitude", math.pi / 2))
        azimuth = float(message.get("azimuth", 0))
        tilt_magnitude = max(0.0, min(1.0, 1.0 - altitude / (math.pi / 2)))
        self._tilt_x = round(math.sin(azimuth) * tilt_magnitude * 90)
        self._tilt_y = round(-math.cos(azimuth) * tilt_magnitude * 90)
        self._emit()

    def release(self) -> None:
        self._pressure = 0
        self._buttons = 0
        self._emit(count=False)

    def close(self) -> None:
        if getattr(self, "fd", -1) < 0:
            return
        self.release()
        os.write(self.fd, struct.pack("<I", UHID_DESTROY))
        os.close(self.fd)
        self.fd = -1

    def _emit(self, *, count: bool = True) -> None:
        report = struct.pack(
            "<BBHHHbb",
            REPORT_ID,
            self._buttons,
            self._x,
            self._y,
            self._pressure,
            self._tilt_x,
            self._tilt_y,
        )
        os.write(self.fd, input_event(report))
        if count:
            self.events_received += 1
