# iPad Tablet Linux Backend

This service streams a Linux display to the iPad as low-latency H.264 and turns Apple Pencil samples
into an absolute OpenTabletDriver device. Pencil input and video have independent queues, so a lost or
slow frame does not intentionally delay pen events.

## Features

- wlroots/Wayland capture with `wf-recorder` and X11 capture with FFmpeg
- VA-API H.264 or low-latency `libx264`
- AES-256-GCM encrypted UDP on `8766` (video) and `8767` (Pencil/control)
- optional direct USB transport with usbmuxd/iproxy; no token is used over USB
- UHID endpoint with absolute X/Y, 8192 pressure levels, tilt, hover and pen buttons
- automatic OTD detection and `OpenTabletDriver.Desktop.Output.AbsoluteMode` selection
- Gaming Mode with live resolution, FPS, bitrate, CBR/VBR and video-off controls

LAN transport is encrypted UDP only.

## Requirements

- FFmpeg
- `wf-recorder` for wlroots/Wayland
- VA-API users: a working VA driver and `vainfo`
- OpenTabletDriver 0.6.7 or a compatible newer release
- USB users: usbmuxd, `idevice_id` and `iproxy`

The AppImage bundles the Qt 6 GUI, Python backend and compiled iPad OTD integration. It deliberately
does not bundle FFmpeg, GPU drivers, OpenTabletDriver itself or usbmuxd because those components must
match the host distribution and hardware.

### Arch Linux

```bash
sudo pacman -S --needed python python-pip ffmpeg wf-recorder libva-utils \
  usbmuxd libimobiledevice libusbmuxd
```

### Debian 13 / Ubuntu-derived distributions

```bash
sudo apt update
sudo apt install python3 python3-venv python3-pip ffmpeg wf-recorder vainfo \
  usbmuxd libimobiledevice-utils libusbmuxd-tools
```

Package names above target Debian 13; older Ubuntu releases may need a newer `wf-recorder` build.

### Fedora 43 or newer

```bash
sudo dnf install python3 python3-pip ffmpeg-free wf-recorder libva-utils \
  usbmuxd libimobiledevice-utils libusbmuxd-utils
```

Fedora's `ffmpeg-free` may lack the H.264 encoder required by this project. If `ffmpeg -encoders`
does not list `h264_vaapi` or `libx264`, install the full FFmpeg build from a codec-enabled repository
approved for your system.

## Install the Qt 6 AppImage

Download `iPad-Tablet-Linux-x86_64.AppImage` and its checksum from the
[latest GitHub release](https://github.com/Davidio1777/ipad-tablet-backends/releases/latest), then:

```bash
sha256sum -c iPad-Tablet-Linux-x86_64.AppImage.sha256
chmod +x iPad-Tablet-Linux-x86_64.AppImage
./iPad-Tablet-Linux-x86_64.AppImage
```

In the GUI, select **Install / Repair** once. It installs the bundled backend into `~/.local/bin`,
installs and enables the iPad OpenTabletDriver integration for the current user, and opens one Polkit
authentication dialog for the `ipadtablet` group and udev permissions. It never runs the streaming
backend as root. Log out and back in once if the group was newly added.

Select a screen, enter or generate a token, choose UDP and/or USB, then select **Start backend**.
The token is passed to the backend through its environment and is not exposed in the process command
line. UDP requires at least 16 UTF-8 bytes; USB-only mode does not require a token.

## Manual source installation

Run from this `LinuxBackEnd` directory:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install --upgrade pip
.venv/bin/pip install -e .
.venv/bin/ipad-tablet-backend doctor
```

Never combine `python -m venv` and the backend command. The first command creates `.venv`; subsequent
commands start with `.venv/bin/...`.

### Install the virtual tablet and OTD integration

```bash
mkdir -p ~/.config/OpenTabletDriver/Configurations
cp opentabletdriver/Configurations/Apple-iPad-Pro.json \
  ~/.config/OpenTabletDriver/Configurations/

sudo groupadd --force ipadtablet
sudo usermod -aG ipadtablet "$USER"
sudo install -m 0644 udev/99-ipad-tablet-uhid.rules \
  /etc/udev/rules.d/99-ipad-tablet-uhid.rules
sudo modprobe uhid
sudo udevadm control --reload-rules
sudo udevadm trigger --action=add --sysname-match=uhid
```

Log out and back in after joining `ipadtablet`. Confirm that `/dev/uhid` is writable without `sudo`.
Then install the included OTD device-hub plugin:

```bash
dotnet build opentabletdriver/Plugin/IPadPencilHub.csproj -c Release
otd installplugin opentabletdriver/Plugin/bin/Release/net8.0/IPadPencilHub.dll
otd enabletools IPadTablet.OpenTabletDriver.IPadPencilTool
otd savedefaultsettings
systemctl --user restart opentabletdriver.service
```

Building that plugin manually requires the .NET 8 SDK. Release AppImages already contain the compiled
plugin and do not require the SDK.

At backend startup, the service retries `otd detect`, runs:

```text
otd setoutputmode "Apple iPad Pro (Apple Pencil)" "OpenTabletDriver.Desktop.Output.AbsoluteMode"
```

and saves the settings. It repeats this when Gaming Mode is enabled. Disable that behavior with
`--no-otd-auto-config`, or override `--otd-cli`, `--otd-tablet` and `--otd-output-mode`.

## Build the AppImage

The release build uses Ubuntu 22.04 for broad glibc compatibility. A local build requires Python 3.11
or newer, CMake, Ninja, a C++20 compiler, Qt 6 development files, qmake6, curl and the .NET 8 SDK:

```bash
LinuxBackEnd/appimage/build-appimage.sh
```

The result and its checksum are written to `LinuxBackEnd/dist/`.

## Start over encrypted UDP

List Wayland output names with `wf-recorder -L`, then use a random token of at least 16 bytes:

```bash
.venv/bin/ipad-tablet-backend serve \
  --output DP-2 \
  --encoder h264_vaapi \
  --vaapi-device /dev/dri/renderD128 \
  --input-mode otd \
  --token 'replace-this-with-a-long-random-token'
```

Encrypted UDP is enabled by default. The final datagram size is limited to 1200 bytes unless changed
with `--udp-mtu`; incomplete video frames expire instead of being retransmitted. Override ports with
`--udp-video-port` and `--udp-input-port`. Allow both UDP ports through the host firewall only on the
trusted LAN.

`--encoder auto` selects VA-API when a render node is available and otherwise uses `libx264`. On
multi-GPU systems, use the device confirmed by:

```bash
vainfo --display drm --device /dev/dri/renderD128
```

## Start over USB

Connect and unlock the iPad, accept **Trust This Computer**, select USB in the app and verify pairing:

```bash
idevice_id -l
```

USB only, with no LAN token or ports:

```bash
.venv/bin/ipad-tablet-backend serve \
  --output DP-2 \
  --encoder h264_vaapi \
  --vaapi-device /dev/dri/renderD128 \
  --input-mode otd \
  --usb --no-udp
```

To offer USB and encrypted UDP at the same time, omit `--no-udp` and add `--token`. The backend starts
`iproxy` and probes iPad listener ports `18765` through `18774`, which avoids stale iPadOS listeners
that report POSIX 48 (`EADDRINUSE`). Use `--udid` when multiple Apple devices are attached.

## Gaming Mode and area mapping

Gaming Mode settings arrive over the encrypted control port or USB. A useful osu! starting point is
1280x720, 120 FPS, 8 Mbit/s and CBR. CBR produces steadier packet sizes; VBR allows quality peaks.
Tablet-only mode stops capture and encoding completely while Pencil/OTD remains active.

The app maps its cyan drawing rectangle to the complete 0-32767 HID range before OTD sees it. Adjust
the app area without compensating for iPad window resizing in OTD. Optional mapping flags are
`--rotation 0|90|180|270` and `--pressure-curve VALUE`.

## Hyprland extra display

```bash
.venv/bin/ipad-tablet-backend virtual-display create --name iPadTablet
hyprctl monitors all
.venv/bin/ipad-tablet-backend serve --output iPadTablet \
  --resolution 2048x1536 --encoder auto --token 'replace-with-a-long-random-token'
.venv/bin/ipad-tablet-backend virtual-display remove --name iPadTablet
```

Configure `iPadTablet` as `2048x1536@60` in Hyprland before starting the stream.

## Optional user service

Edit the output in `systemd/ipad-tablet-backend.service`, then:

```bash
mkdir -p ~/.config/systemd/user ~/.config/ipad-tablet-backend
cp systemd/ipad-tablet-backend.service ~/.config/systemd/user/
printf 'IPAD_TABLET_TOKEN=%s\n' 'replace-with-a-long-random-token' \
  > ~/.config/ipad-tablet-backend/environment
systemctl --user import-environment WAYLAND_DISPLAY DISPLAY XDG_SESSION_TYPE
systemctl --user daemon-reload
systemctl --user enable --now ipad-tablet-backend.service
```

## Diagnostics and tests

```bash
.venv/bin/ipad-tablet-backend doctor
otd detect
journalctl --user -u opentabletdriver.service -n 100 --no-pager
PYTHONPATH=src .venv/bin/python -m unittest discover -s tests -v
```

There is intentionally no HTTP health URL. Runtime counters and selected stream settings are sent to
the connected iPad as encrypted metadata and printed in the backend log.

## Security

Each UDP datagram uses AES-256-GCM with HKDF-SHA256 directional keys, a random 96-bit nonce and an
authenticated header. Recently seen nonces are rejected. This authenticates and encrypts LAN traffic,
but it does not make an internet-facing service safe: keep ports 8766/8767 on a trusted private LAN.
USB trusts the local usbmuxd pairing and does not use the LAN token.
