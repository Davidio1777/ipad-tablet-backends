# iPad Tablet Windows 11 Backend

The Windows backend streams the desktop to the iPad and exposes Apple Pencil through an
OpenTabletDriver device hub. It offers both a console executable and a WPF GUI.

## Features

- FFmpeg `gdigrab` capture
- automatic AMD AMF, NVIDIA NVENC, Intel QSV or `libx264` selection
- AES-256-GCM encrypted UDP on `8766` (video) and `8767` (Pencil/control)
- optional direct USB with usbmuxd/iproxy, without a LAN token
- named-pipe OpenTabletDriver hub with automatic absolute-mode setup
- Gaming Mode settings and a video-off tablet-only mode
- graphical launcher for connection, capture, encoder and OTD settings

LAN transport is encrypted UDP only.

## Requirements

1. Windows 11 x64
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) only when building from source;
   release executables are self-contained
3. A full Windows FFmpeg build with `gdigrab` and at least one H.264 encoder in `PATH`
4. [OpenTabletDriver](https://opentabletdriver.net/) 0.6.7 or a compatible newer release
5. Optional USB: a Windows `iproxy.exe`/libusbmuxd build and Apple Mobile Device support

## Install the release package

Download `iPad-Tablet-Windows-x64.zip` and its checksum from the
[latest GitHub release](https://github.com/Davidio1777/ipad-tablet-backends/releases/latest), verify the
SHA-256 hash, and extract the complete archive. Start `gui\iPadTabletBackend.exe`; keep the `gui`,
`backend` and `otd` folders together.

The GUI lets you select a monitor, enter or generate the encrypted UDP token, configure capture and
encoding, install/repair the bundled iPad OTD integration, and start or stop the backend. The backend
and GUI do not need a separately installed .NET runtime.

## Build

Open PowerShell in `WindowsBackEnd`:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

Outputs:

- `dist\backend\ipad-tablet-backend.exe`: console backend
- `dist\gui\iPadTabletBackend.exe`: graphical launcher
- `dist\otd`: OTD plugin and tablet configuration

Keep the `gui` and `backend` folders next to each other. The GUI locates the console executable in
the sibling backend directory and displays its live log.

## Install OpenTabletDriver integration

Close OpenTabletDriver completely, then run:

```powershell
.\install-otd.ps1
```

Restart OTD and run **Detect**. The backend retries detection and selects
`OpenTabletDriver.Desktop.Output.AbsoluteMode` at startup and whenever Gaming Mode is enabled.

## Run with the GUI

Start `dist\gui\iPadTabletBackend.exe`, generate a token, select the monitor or adjust its capture
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
dist\backend\ipad-tablet-backend.exe serve `
  --token "replace-this-with-a-long-random-token" `
  --encoder h264_nvenc `
  --source-x 2560 --source-y 0 --source-width 2560 --source-height 1440 `
  --width 1280 --height 720 --fps 120 --bitrate 8000000 --rate-control cbr
```

Run with `--help` for every option. `--encoder auto` probes AMF, NVENC, QSV and finally `libx264`.

## USB details

Unlock the iPad and accept **Trust This Computer** before starting. The backend launches `iproxy`
against the app listener. usbmuxd pairing is the trust boundary, so USB-only mode does not require,
send or validate the LAN token. No firewall rule is required for USB-only mode.

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
- Use a private Windows Firewall rule for UDP 8766/8767.
- For USB, confirm the iPad is paired and `iproxy.exe` is the selected path.
- Include the GUI/backend log, GPU, FFmpeg build and OTD version in bug reports.
