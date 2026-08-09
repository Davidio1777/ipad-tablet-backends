# iPad Tablet Backend für Windows 11

Dieses Backend spricht dasselbe Protokoll wie das Linux-Backend und die iPad-App:

- WebSocket: `8765/TCP` für H.264 und Pencil
- Gaming-UDP: `8766/UDP` für Video und `8767/UDP` für Pencil/Steuerung
- Kabel: USBMux/`iproxy` auf `18765` bis `18774`, ohne Token
- OpenTabletDriver: `\\.\pipe\ipad-pencil`, 10-Byte-HID-Reports mit Druck und Tilt
- „Nur Tablet“ beendet FFmpeg vollständig; OTD und Pencil bleiben verbunden

## Voraussetzungen unter Windows

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Ein aktuelles `ffmpeg.exe` im `PATH`
3. OpenTabletDriver 0.6.7
4. Optional für USB: Windows-Build von `iproxy.exe`/libusbmuxd und ein mit dem PC gekoppeltes iPad

PowerShell im Backend-Ordner:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
.\install-otd.ps1
.\run.ps1 -Token "choose-a-long-random-token" -Udp
```

Für WebSocket/WLAN ohne UDP genügt:

```powershell
.\run.ps1 -Token "choose-a-long-random-token"
```

Für USB (Token wird absichtlich nicht geprüft, usbmuxd-Pairing ist die Vertrauensgrenze):

```powershell
.\run.ps1 -Token "choose-a-long-random-token" -Usb -Iproxy "C:\Tools\libusbmuxd\iproxy.exe"
```

Die App verwendet weiterhin `ws://WINDOWS-IP:8765`. In der Windows-Firewall müssen bei LAN-Betrieb TCP 8765 und bei UDP zusätzlich UDP 8766/8767 für das private Netzwerk freigegeben sein. USB benötigt keine LAN-Freigabe.

## Capture und Encoder

Standardmäßig ermittelt `--encoder auto` in dieser Reihenfolge:

1. `h264_amf` (AMD)
2. `h264_nvenc` (NVIDIA)
3. `h264_qsv` (Intel)
4. `libx264`

Die Desktop-Aufnahme verwendet FFmpegs `gdigrab`; die H.264-Ausgabe hat AUD-Grenzen, keine B-Frames und eine Ein-Frame-Latenz-Puffergröße. Gaming-Auflösung, FPS, Bitrate, CBR/VBR sowie „Nur Tablet“ werden live von der iPad-App übernommen.

Mehrere Monitore werden über den virtuellen Windows-Desktop gewählt. Beispiel für einen Monitor rechts vom Hauptmonitor:

```powershell
dist\backend\ipad-tablet-backend.exe serve `
  --token choose-a-long-random-token --udp --source-x 2560 --source-y 0 `
  --source-width 2560 --source-height 1440 --width 2560 --height 1440
```

## OTD-Diagnose

Backend zuerst starten, danach OpenTabletDriver neu starten und **Detect** wählen. In der Backend-Konsole muss `OTD: OpenTabletDriver verbunden` erscheinen. Der virtuelle Endpunkt wird vollständig im Benutzerkontext erzeugt; Testsigning oder ein eigener Kernel-Treiber sind nicht nötig.
