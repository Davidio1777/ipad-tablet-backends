# RayShine Windows 11 Backend

The Windows backend streams the desktop to the iPad and exposes Apple Pencil through an
OpenTabletDriver device hub. It offers both a console executable and a WPF GUI.

## Features

- FFmpeg Desktop Duplication capture with stable monotonic frame timing
- automatic AMD AMF, NVIDIA NVENC, Intel QSV or `libx264` selection
- AES-256-GCM encrypted UDP on `8766` (video) and `8767` (Pencil/control)
- optional direct USB with usbmuxd/iproxy, without a LAN token
- synchronized Desktop Duplication capture on Windows with automatic GDI fallback
- automatic low-latency scRGB/HDR desktop color correction for the SDR H.264 iPad stream
- named-pipe OpenTabletDriver hub with automatic absolute-mode setup
- Gaming Mode settings and a video-off tablet-only mode
- graphical launcher for connection, capture, encoder and OTD settings

LAN transport is encrypted UDP only.

## Requirements

1. Windows 11 x64
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) only when building from source;
   release executables are self-contained
3. [OpenTabletDriver](https://opentabletdriver.net/) 0.6.7 or a compatible newer release
4. USB: the current **Apple Devices** app from Microsoft Store; start it once, unlock the iPad,
   and accept **Trust This Computer**. A current checksum-pinned x64 `iproxy.exe` runtime is bundled.

## Install the release package

Download `RayShine-Windows-x64.zip` and its checksum from the
[latest GitHub release](https://github.com/Davidio1777/ipad-tablet-backends/releases/latest), verify the
SHA-256 hash, and extract the complete archive. Start `gui\RayShineBackend.exe`; keep the `gui`,
`backend` and `otd` folders together.

The GUI lets you select a monitor, enter or generate the encrypted UDP token, configure capture and
encoding, install/repair the bundled iPad OTD integration, and start or stop the backend. The backend
and GUI do not need a separately installed .NET runtime. FFmpeg and the compatible Windows x64
`iproxy` runtime are bundled in `tools`, so they remain available without changing the system `PATH`.

The launcher automatically searches the release package, `PATH`, common WinGet/Scoop locations and
the current user's folders for FFmpeg and `OpenTabletDriver.Console.exe`. If a portable OTD copy is
stored elsewhere, the launcher opens a file picker instead of repeatedly trying a missing executable.

## Build

Open PowerShell in `WindowsBackEnd`:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

The build script downloads the current BtbN static GPL FFmpeg build and a pinned, checksum-verified
Windows x64 libimobiledevice/iproxy package. Their notices and licenses are included in `dist\tools`.
Set `IPAD_TABLET_SKIP_FFMPEG=1` or `IPAD_TABLET_SKIP_USB_TOOLS=1` only for developer builds that
intentionally use external tools.

Outputs:

- `dist\backend\rayshine-backend.exe`: console backend
- `dist\gui\RayShineBackend.exe`: graphical launcher
- `dist\otd`: OTD plugin and tablet configuration

Keep the `gui` and `backend` folders next to each other. The GUI locates the console executable in
the sibling backend directory and displays its live log.

## Install OpenTabletDriver integration

For portable OTD, point the GUI at its `OpenTabletDriver.Console.exe`, then click
**Install / Repair iPad OTD integration**. The installer uses that portable copy's `userdata`
directory. For a scripted install, run:

```powershell
.\install-otd.ps1
```

At startup the backend starts the OTD daemon if needed, enables the iPad device-hub tool, retries
detection, and selects `OpenTabletDriver.Desktop.Output.AbsoluteMode`. A successful setup is logged
as `OTD ready`; a CLI exit code alone is not treated as success.

## Run with the GUI

Start `dist\gui\RayShineBackend.exe`, generate a token, select the monitor or adjust its capture
rectangle and encoder, then choose **Start backend**. For LAN use, permit UDP 8766 and 8767 in Windows
Defender Firewall for private networks only.

## Run from PowerShell

Encrypted UDP:

```powershell
.\run.ps1 -Token "replace-this-with-a-long-random-token"
```

USB only, without a token:

```powershell
.\run.ps1 -UsbOnly -Iproxy "C:\Tools\libusbmuxd\iproxy.exe"
```

USB plus encrypted UDP:

```powershell
.\run.ps1 -Usb -Token "replace-this-with-a-long-random-token" `
  -Iproxy "C:\Tools\libusbmuxd\iproxy.exe"
```

The low-level executable supports additional settings:

```powershell
dist\backend\rayshine-backend.exe serve `
  --token "replace-this-with-a-long-random-token" `
  --encoder h264_nvenc `
  --source-x 2560 --source-y 0 --source-width 2560 --source-height 1440 `
  --width 1280 --height 720 --fps 120 --bitrate 8000000 --rate-control cbr
```

Run with `--help` for every option. `--encoder auto` probes AMF, NVENC, QSV and finally `libx264`.
`--capture auto` uses Windows Desktop Duplication at the requested refresh rate and falls back to
GDI only if duplication cannot start. A previously healthy DDA session is recreated after display
mode or HDR changes instead of being permanently downgraded. GDI fallback runs at its real maximum
of 60 FPS rather than synthesizing 120 FPS. `--capture dda` or `--capture gdi` can force either path.
When Windows HDR is active, the backend probes for the 16-bit scRGB desktop format and applies a
calibrated low-cost SDR correction while tagging the H.264 stream explicitly as limited-range BT.709.

## USB details

Open Apple Devices once, unlock the iPad and accept **Trust This Computer** before starting. The backend launches `iproxy`
against the app listener. usbmuxd pairing is the trust boundary, so USB-only mode does not require,
send or validate the LAN token. No firewall rule is required for USB-only mode.

The backend first queries the bundled `idevice_id` client, which speaks the real usbmux protocol,
and distinguishes Apple service, device enumeration and app-listener failures in its log. It then
starts exactly one proxy (`127.0.0.1:18765` to iPad port `18765`) for the detected USB UDID. Apple
Mobile Device Support must still be installed and running so Windows can communicate with the paired iPad.

## Gaming Mode

The iPad can change resolution, 60/120 FPS, bitrate and CBR/VBR while connected. Tablet-only mode
stops FFmpeg completely while keeping Pencil input and OTD active. A practical low-latency starting
point is 1280x720, 120 FPS, 8 Mbit/s and CBR.

## Security

Every LAN datagram is encrypted and authenticated with AES-256-GCM. Directional keys come from the
token through HKDF-SHA256, headers are authenticated and recent nonces cannot be replayed. Tokens must
contain at least 16 UTF-8 bytes. Do not forward UDP 8766/8767 to the public internet.

## Troubleshooting

- Confirm `ffmpeg -encoders` lists the selected encoder.
- Run `otd detect` after the backend creates the device hub.
- In the GUI, choose **Repair private UDP rule** once (Windows requests administrator approval).
- For USB, confirm the iPad appears in Apple Devices and is paired. The bundled
  `tools\usbmuxd\idevice_id.exe -l` must print its UDID; `iproxy.exe` is selected automatically.
- Include the GUI/backend log, GPU, FFmpeg build and OTD version in bug reports.
