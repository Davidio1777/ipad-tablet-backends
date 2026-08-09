# Linux backend for iPad Linux Tablet

This service mirrors a Linux output to the iPad with low-latency H.264 and creates a real absolute
Linux pen device from Apple Pencil events. It is intentionally dependency-light: Python's standard
library handles HTTP/WebSocket and `/dev/uhid`; `wf-recorder`/`ffmpeg` handle capture and encoding.

## What works

- Wayland/wlroots/Hyprland capture through `wf-recorder`
- X11 capture through `ffmpeg -f x11grab`
- `h264_vaapi` with CBR, no B-frames and an access-unit delimiter per frame
- automatic `libx264` low-latency fallback
- mirror an existing output or capture a Hyprland headless output as an extra display
- virtual pen tablet: absolute X/Y, 8192 pressure levels, tilt, hover, tip, stylus buttons
- input queue independent of video, so Pencil latency does not grow when a video frame drops
- authenticated two-port UDP mode: `8766/UDP` video and `8767/UDP` Pencil/control
- direct USB transport through `usbmuxd`/`iproxy`, while LAN remains available in parallel
- live gaming profile control from the iPad: resolution, 60/120 FPS, bitrate and CBR/VBR without a backend restart

## Install

Arch Linux example:

```bash
sudo pacman -S ffmpeg wf-recorder libva-utils python
python -m venv .venv
.venv/bin/pip install -e .
.venv/bin/ipad-tablet-backend doctor
```

Install the OpenTabletDriver configuration and grant the desktop user access to UHID (inspect the
included rule before copying it):

```bash
mkdir -p ~/.config/OpenTabletDriver/Configurations
cp opentabletdriver/Configurations/Apple-iPad-Pro.json ~/.config/OpenTabletDriver/Configurations/
sudo groupadd --force ipadtablet
sudo usermod -aG ipadtablet "$USER"
sudo cp udev/99-ipad-tablet-uhid.rules /etc/udev/rules.d/
sudo modprobe uhid
sudo udevadm control --reload-rules
sudo udevadm trigger --action=add /sys/class/misc/uhid
systemctl --user restart opentabletdriver.service
```

Log out and back in once after joining the dedicated `ipadtablet` group. `/dev/uhid` should then
exist and be writable by the active desktop user. Do not run either daemon as root. The older direct
kernel-tablet path remains available with `--input-mode uinput` and the existing
`99-ipad-tablet-uinput.rules` rule.

OpenTabletDriver 0.6.7's HidSharp backend does not enumerate UHID devices without a physical USB
parent. Install and enable the included device-hub plugin once so OTD consumes `/dev/ipad-pencil`
through its normal configuration, parser and output pipeline:

```bash
dotnet build opentabletdriver/Plugin/IPadPencilHub.csproj -c Release
cp opentabletdriver/Plugin/bin/Release/net8.0/IPadPencilHub.dll opentabletdriver/Plugin/
otd installplugin opentabletdriver/Plugin/IPadPencilHub.dll
otd enabletools IPadTablet.OpenTabletDriver.IPadPencilTool
otd savedefaultsettings
```

## Mirror an existing monitor

List output names with `wf-recorder -L`, then start:

```bash
.venv/bin/ipad-tablet-backend serve \
  --output DP-2 \
  --encoder h264_vaapi \
  --vaapi-device /dev/dri/renderD128 \
  --input-mode otd \
  --token 'choose-a-long-random-token'
```

`--encoder auto` chooses VAAPI when `/dev/dri/renderD*` exists, otherwise `libx264`. On a multi-GPU
machine, explicitly choose the render node reported by `vainfo --display drm --device ...`.

Open `http://LINUX-IP:8765/health` to see dimensions, encoder, frames and connected clients. The iPad
uses `ws://LINUX-IP:8765` plus the same token.

## Low-latency UDP over LAN

WebSocket remains available as the reliable TCP fallback. For a lossy low-latency path, start the
two UDP ports explicitly:

```bash
.venv/bin/ipad-tablet-backend serve \
  --output DP-2 \
  --encoder h264_vaapi \
  --vaapi-device /dev/dri/renderD128 \
  --input-mode otd \
  --udp \
  --token 'choose-a-long-random-token'
```

UDP mode requires a non-empty token. Port `8766/UDP` sends H.264 and stream metadata to the
authenticated iPad session; `8767/UDP` receives Pencil batches and stream settings. Input packets
must contain the session token and originate from the IP that registered the video session.

Video access units are fragmented to a default maximum datagram size of 1200 bytes to avoid normal
IP fragmentation. The iPad discards incomplete frames after 250 ms instead of requesting a retry.
This trades an occasional skipped frame for bounded latency. Override the ports with
`--udp-video-port` and `--udp-input-port`, or the datagram size with `--udp-mtu`.

`/health` exposes `udpEnabled`, `udpClients`, `udpFrames`, `udpVideoPackets`, `udpInputPackets`,
`udpDroppedVideoFrames`, `udpDroppedInputPackets` and `udpError`.

## Direct USB connection

USB mode does not use IP tethering. The iPad app opens a native TCP listener on port `18765`, and
`iproxy` forwards a Linux-local connection to that listener through Apple's USB multiplexing
protocol. One framed TCP stream carries H.264 toward the iPad and Pencil messages back to Linux.
The usbmuxd device pairing is the trust boundary, so the LAN token is intentionally not sent or
checked on this cable transport.

The required packages are already present on the development host. On Arch Linux they are:

```bash
sudo pacman -S usbmuxd libimobiledevice libusbmuxd
```

Connect and unlock the iPad, confirm **Diesem Computer vertrauen**, select **USB-Kabel verwenden** in
the app, then run:

```bash
.venv/bin/ipad-tablet-backend serve \
  --output DP-2 \
  --encoder h264_vaapi \
  --vaapi-device /dev/dri/renderD128 \
  --usb
```

Useful checks:

```bash
idevice_id -l
.venv/bin/ipad-tablet-backend doctor
curl http://127.0.0.1:8765/health
```

`usbEnabled` and `usbConnected` in the health response show the bridge state. If multiple Apple
devices are attached, pass `--udid DEVICE_ID`. Advanced port overrides are
`--usb-local-port` and `--usb-device-port`; the device port must match the app's `18765`.
The app and backend map ten consecutive ports by default (`18765`–`18774`). This automatically
recovers when iPadOS reports `POSIX 48 / EADDRINUSE`; change the range with
`--usb-port-fallbacks` if necessary. `usbDevicePort` in `/health` reports the selected port.

WebSocket, UDP and USB can be served simultaneously. The iPad can switch transport without
restarting this backend, provided UDP was enabled with `--udp` at startup.

## Dedicated extra display on Hyprland

Create a temporary headless output:

```bash
.venv/bin/ipad-tablet-backend virtual-display create --name iPadTablet
hyprctl monitors all
```

Configure that output in the Hyprland version you run. On Hyprland 0.55+, the persistent Lua form is:

```lua
hl.monitor({
  output = "iPadTablet",
  mode = "2048x1536@60",
  position = "auto",
  scale = 1,
})
```

Then stream it:

```bash
.venv/bin/ipad-tablet-backend serve --output iPadTablet --resolution 2048x1536 --encoder auto
```

Remove the temporary output with:

```bash
.venv/bin/ipad-tablet-backend virtual-display remove --name iPadTablet
```

The 4:3 mode matches the 12.9-inch iPad closely. The app accounts for any remaining letterboxing when
mapping Pencil coordinates.

## OpenTabletDriver and osu!

The default `--input-mode otd` creates `Apple iPad Pro (Apple Pencil)` through Linux UHID. This is a
real raw HID endpoint (`/dev/hidraw*`, stable link `/dev/ipad-pencil`) with virtual VID `1209`, PID
`a1d0`, absolute coordinates, 8192 pressure levels and two pen buttons. The included OTD device-hub
plugin exposes that endpoint to OTD 0.6.7, which then processes the iPad through the same area,
filter, pressure and output pipeline as a physical Wacom tablet.

After the backend starts, trigger detection or open the GUI:

```bash
otd detect
otd-gui
```

Select `Apple iPad Pro (Apple Pencil)` as the OTD profile and configure its area/output like the
Wacom CTL-472. In osu!lazer, either use OTD's system cursor output or select the resulting OTD virtual
tablet according to the osu! input mode you use. The UHID report descriptor is vendor-defined so
libinput does not also consume the raw iPad endpoint.

The health endpoint reports `inputMode`, `inputClients`, `inputEvents`, `inputSamples` and
`inputRateHz`. While moving the Pencil, `inputSamples` must increase and `inputRateHz` shows how many
real samples arrived during the last second. This separates iPad capture problems from OTD mapping
or video-refresh problems.

The app's cyan drawing rectangle is mapped to the full 0–32767 HID coordinate range before OTD
sees the report. Its 4:3 size and position can therefore be adjusted on the iPad without changing
the OTD output-area profile or reacting to floating-window resizes.

## Gaming and low-latency streaming

Enable **Gaming Mode** in the iPad settings panel, choose a resolution, bitrate and CBR/VBR, then
tap **Profil jetzt anwenden**. The existing input channel sends a `stream-settings` control message;
the backend terminates only `wf-recorder`, clears stale frames, rebuilds the VAAPI command and sends
updated stream metadata to WebSocket, UDP and USB clients.

**Nur Tablet** sends `videoEnabled: false`: the backend cancels `wf-recorder` instead of merely
covering the image on the iPad. Pencil/OTD, WebSocket/UDP/USB control channels and the cyan area
outline remain active. Switching it off again starts a clean capture pipeline.

The recommended osu! starting point is 1280×720, 120 FPS, 8 Mbit/s and CBR. CBR gives steadier frame sizes;
VBR allows peaks up to 150% of the selected target and may preserve more detail in complex scenes.
Gaming mode additionally uses a half-second GOP, `bf=0`, `async_depth=1`, a two-frame rate-control
buffer and a one-frame application queue. Coalesced UIKit Pencil samples arrive as compact batches
and are expanded back into individual UHID/OTD reports, so input sampling is independent of video
FPS. CBR and VBR profiles support VA-API and DMA-BUF capture when the installed driver exposes them.

The command-line quality profile is restored when Gaming Mode is disabled. Its initial rate control
can also be selected explicitly:

```bash
.venv/bin/ipad-tablet-backend serve \
  --output DP-2 \
  --encoder h264_vaapi \
  --vaapi-device /dev/dri/renderD128 \
  --rate-control cbr
```

`/health` reports `gamingMode`, `videoEnabled`, `width`, `height`, `fps`, `bitrate`, `rateControl`, `streamRevision`
and the live Pencil rate as `inputRateHz`.

Useful mapping flags are `--rotation 0|90|180|270` and `--pressure-curve 1.0`. Values above `1.0`
soften the lower end of the pressure response; values below `1.0` make it more sensitive.

## Optional user service

Edit the output and other arguments in `systemd/ipad-tablet-backend.service`, then:

```bash
mkdir -p ~/.config/systemd/user ~/.config/ipad-tablet-backend
cp systemd/ipad-tablet-backend.service ~/.config/systemd/user/
printf 'IPAD_TABLET_TOKEN=%s\n' 'choose-a-long-random-token' > ~/.config/ipad-tablet-backend/environment
systemctl --user import-environment WAYLAND_DISPLAY DISPLAY XDG_SESSION_TYPE
systemctl --user daemon-reload
systemctl --user enable --now ipad-tablet-backend.service
```

## Security and latency notes

The transport is unencrypted WebSocket intended for a trusted LAN or USB network. Always use a token
on shared networks. Do not expose port 8765 to the internet. For the lowest latency, use wired Ethernet
or a USB network link, 5/6 GHz Wi-Fi, a 60 Hz output, VAAPI, and a bitrate between 12–25 Mbit/s.

## Test

```bash
PYTHONPATH=src python -m unittest discover -s tests -v
```
