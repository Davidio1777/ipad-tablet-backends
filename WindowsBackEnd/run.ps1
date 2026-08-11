param(
  [string]$Token = $env:IPAD_TABLET_TOKEN,
  [string]$Encoder = "auto",
  [switch]$Usb,
  [switch]$UsbOnly,
  [string]$Ffmpeg = "ffmpeg.exe",
  [string]$Iproxy = "iproxy.exe"
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Backend = "$Root\dist\backend\rayshine-backend.exe"
if (!(Test-Path $Backend)) { throw "Run .\build.ps1 first." }
if (!$UsbOnly -and ([string]::IsNullOrWhiteSpace($Token) -or [Text.Encoding]::UTF8.GetByteCount($Token) -lt 16)) {
  throw "Encrypted UDP requires -Token with at least 16 UTF-8 bytes or IPAD_TABLET_TOKEN."
}

$Arguments = @(
  "serve", "--host", "0.0.0.0",
  "--encoder", $Encoder, "--ffmpeg", $Ffmpeg,
  "--source-width", "2560", "--source-height", "1440",
  "--width", "2560", "--height", "1440", "--fps", "60",
  "--bitrate", "16000000", "--input-mode", "otd"
)
if (!$UsbOnly) { $env:IPAD_TABLET_TOKEN = $Token }
if ($Usb) { $Arguments += @("--usb", "--iproxy", $Iproxy) }
if ($UsbOnly) { $Arguments += @("--usb", "--no-udp", "--iproxy", $Iproxy) }
& $Backend @Arguments
