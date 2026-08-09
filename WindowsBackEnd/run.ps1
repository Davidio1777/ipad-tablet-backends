param(
  [string]$Token = $env:IPAD_TABLET_TOKEN,
  [string]$Encoder = "auto",
  [switch]$Udp,
  [switch]$Usb,
  [string]$Ffmpeg = "ffmpeg.exe",
  [string]$Iproxy = "iproxy.exe"
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Backend = "$Root\dist\backend\ipad-tablet-backend.exe"
if (!(Test-Path $Backend)) { throw "Erst .\build.ps1 ausführen." }
if ([string]::IsNullOrWhiteSpace($Token)) {
  throw "Token fehlt. -Token angeben oder IPAD_TABLET_TOKEN setzen."
}

$Arguments = @(
  "serve", "--host", "0.0.0.0", "--port", "8765",
  "--token", $Token, "--encoder", $Encoder, "--ffmpeg", $Ffmpeg,
  "--source-width", "2560", "--source-height", "1440",
  "--width", "2560", "--height", "1440", "--fps", "60",
  "--bitrate", "16000000", "--input-mode", "otd"
)
if ($Udp) { $Arguments += "--udp" }
if ($Usb) { $Arguments += @("--usb", "--iproxy", $Iproxy) }
& $Backend @Arguments
