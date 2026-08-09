from __future__ import annotations

import ctypes
import fcntl
import math
import os
import struct
from dataclasses import dataclass
from typing import Any


EV_SYN, EV_KEY, EV_ABS = 0x00, 0x01, 0x03
SYN_REPORT = 0
BTN_TOOL_PEN, BTN_TOUCH, BTN_STYLUS, BTN_STYLUS2 = 0x140, 0x14A, 0x14B, 0x14C
ABS_X, ABS_Y, ABS_PRESSURE, ABS_DISTANCE, ABS_TILT_X, ABS_TILT_Y = 0x00, 0x01, 0x18, 0x19, 0x1A, 0x1B
BUS_VIRTUAL = 0x06


def _ioc(direction: int, type_: int, number: int, size: int) -> int:
    return (direction << 30) | (size << 16) | (type_ << 8) | number


def _iow(type_: int, number: int) -> int:
    return _ioc(1, type_, number, ctypes.sizeof(ctypes.c_int))


UI_SET_EVBIT = _iow(ord("U"), 100)
UI_SET_KEYBIT = _iow(ord("U"), 101)
UI_SET_ABSBIT = _iow(ord("U"), 103)
UI_DEV_CREATE = _ioc(0, ord("U"), 1, 0)
UI_DEV_DESTROY = _ioc(0, ord("U"), 2, 0)


@dataclass(slots=True)
class TabletMapping:
    rotation: int = 0
    pressure_curve: float = 1.0

    def point(self, x: float, y: float) -> tuple[float, float]:
        x, y = min(1.0, max(0.0, x)), min(1.0, max(0.0, y))
        if self.rotation == 90:
            return 1.0 - y, x
        if self.rotation == 180:
            return 1.0 - x, 1.0 - y
        if self.rotation == 270:
            return y, 1.0 - x
        return x, y

    def pressure(self, value: float) -> float:
        return min(1.0, max(0.0, value)) ** self.pressure_curve


class NullTablet:
    input_mode = "none"
    events_received = 0

    def apply(self, _message: dict[str, Any]) -> None:
        pass

    def release(self) -> None:
        pass

    def close(self) -> None:
        pass


class UInputTablet:
    input_mode = "uinput"
    AXIS_MAX = 32_767
    PRESSURE_MAX = 8_191
    TILT_MAX = 90

    def __init__(self, path: str = "/dev/uinput", mapping: TabletMapping | None = None) -> None:
        self.mapping = mapping or TabletMapping()
        self.fd = os.open(path, os.O_WRONLY | os.O_NONBLOCK)
        self._last_sequence = -1
        self._touching = False
        self._tool_present = False
        self.events_received = 0
        self._configure()

    def _configure(self) -> None:
        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_KEY)
        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_ABS)
        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_SYN)
        for code in (BTN_TOOL_PEN, BTN_TOUCH, BTN_STYLUS, BTN_STYLUS2):
            fcntl.ioctl(self.fd, UI_SET_KEYBIT, code)
        for code in (ABS_X, ABS_Y, ABS_PRESSURE, ABS_DISTANCE, ABS_TILT_X, ABS_TILT_Y):
            fcntl.ioctl(self.fd, UI_SET_ABSBIT, code)

        absmax = [0] * 64
        absmin = [0] * 64
        absfuzz = [0] * 64
        absflat = [0] * 64
        absmax[ABS_X] = absmax[ABS_Y] = self.AXIS_MAX
        absmax[ABS_PRESSURE] = self.PRESSURE_MAX
        absmax[ABS_DISTANCE] = 255
        absmin[ABS_TILT_X] = absmin[ABS_TILT_Y] = -self.TILT_MAX
        absmax[ABS_TILT_X] = absmax[ABS_TILT_Y] = self.TILT_MAX
        name = b"iPad Pro Apple Pencil (Network)".ljust(80, b"\0")
        device = struct.pack("80sHHHHI", name, BUS_VIRTUAL, 0x0001, 0x0001, 1, 0)
        device += struct.pack("64i", *absmax)
        device += struct.pack("64i", *absmin)
        device += struct.pack("64i", *absfuzz)
        device += struct.pack("64i", *absflat)
        os.write(self.fd, device)
        fcntl.ioctl(self.fd, UI_DEV_CREATE)

    def apply(self, message: dict[str, Any]) -> None:
        sequence = int(message.get("sequence", self._last_sequence + 1))
        if sequence <= self._last_sequence:
            return
        self._last_sequence = sequence
        self.events_received += 1
        if message.get("type") == "button":
            code = BTN_STYLUS2 if int(message.get("button", 1)) == 2 else BTN_STYLUS
            self._event(EV_KEY, code, bool(message.get("pressed")))
            self._sync()
            return
        if message.get("type") != "pencil":
            return

        phase = str(message.get("phase", "move"))
        if phase == "leave":
            self.release()
            return
        x, y = self.mapping.point(float(message.get("x", 0)), float(message.get("y", 0)))
        pressure = self.mapping.pressure(float(message.get("pressure", 0)))
        altitude = float(message.get("altitude", math.pi / 2))
        azimuth = float(message.get("azimuth", 0))
        tilt_magnitude = max(0.0, min(1.0, 1.0 - altitude / (math.pi / 2)))
        tilt_x = int(math.sin(azimuth) * tilt_magnitude * self.TILT_MAX)
        tilt_y = int(-math.cos(azimuth) * tilt_magnitude * self.TILT_MAX)
        touching = phase not in {"hover", "up", "cancel"}

        if not self._tool_present:
            self._event(EV_KEY, BTN_TOOL_PEN, 1)
            self._tool_present = True
        self._event(EV_ABS, ABS_X, round(x * self.AXIS_MAX))
        self._event(EV_ABS, ABS_Y, round(y * self.AXIS_MAX))
        self._event(EV_ABS, ABS_PRESSURE, round(pressure * self.PRESSURE_MAX))
        self._event(EV_ABS, ABS_DISTANCE, 0 if touching else 1)
        self._event(EV_ABS, ABS_TILT_X, tilt_x)
        self._event(EV_ABS, ABS_TILT_Y, tilt_y)
        if touching != self._touching:
            self._event(EV_KEY, BTN_TOUCH, int(touching))
            self._touching = touching
        self._sync()

    def release(self) -> None:
        if self._touching:
            self._event(EV_KEY, BTN_TOUCH, 0)
        if self._tool_present:
            self._event(EV_KEY, BTN_TOOL_PEN, 0)
        self._event(EV_ABS, ABS_PRESSURE, 0)
        self._touching = False
        self._tool_present = False
        self._sync()

    def close(self) -> None:
        if getattr(self, "fd", -1) < 0:
            return
        self.release()
        fcntl.ioctl(self.fd, UI_DEV_DESTROY)
        os.close(self.fd)
        self.fd = -1

    def _event(self, type_: int, code: int, value: int | bool) -> None:
        os.write(self.fd, struct.pack("llHHi", 0, 0, type_, code, int(value)))

    def _sync(self) -> None:
        self._event(EV_SYN, SYN_REPORT, 0)
