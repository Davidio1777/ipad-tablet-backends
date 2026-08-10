$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root "dist"
if (Test-Path $Dist) { Remove-Item -LiteralPath $Dist -Recurse -Force }

dotnet publish "$Root\src\IPadTablet.Backend\IPadTablet.Backend.csproj" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o "$Root\dist\backend"

dotnet publish "$Root\src\IPadTablet.Backend.Gui\IPadTablet.Backend.Gui.csproj" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o "$Root\dist\gui"

dotnet build "$Root\opentabletdriver\Plugin\IPadPencilWindowsHub.csproj" -c Release
New-Item -ItemType Directory -Force "$Root\dist\otd" | Out-Null
Copy-Item "$Root\opentabletdriver\Plugin\bin\Release\net8.0\IPadPencilWindowsHub.dll" "$Root\dist\otd\"
Copy-Item "$Root\opentabletdriver\Configurations\Apple-iPad-Pro-Windows.json" "$Root\dist\otd\"

if ($env:IPAD_TABLET_SKIP_FFMPEG -ne "1") {
  $FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
  $TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ipad-tablet-ffmpeg-" + [guid]::NewGuid())
  $Archive = Join-Path $TempRoot "ffmpeg.zip"
  $Extracted = Join-Path $TempRoot "extracted"
  New-Item -ItemType Directory -Force $Extracted | Out-Null
  Write-Host "Downloading the bundled FFmpeg build..." -ForegroundColor Cyan
  & curl.exe --fail --location --retry 3 $FfmpegUrl --output $Archive
  if ($LASTEXITCODE -ne 0) { throw "FFmpeg download failed with exit code $LASTEXITCODE." }
  Expand-Archive $Archive -DestinationPath $Extracted -Force
  $Ffmpeg = Get-ChildItem $Extracted -Recurse -File -Filter "ffmpeg.exe" | Select-Object -First 1
  if (!$Ffmpeg) { throw "The FFmpeg archive did not contain ffmpeg.exe." }
  New-Item -ItemType Directory -Force "$Root\dist\tools" | Out-Null
  Copy-Item $Ffmpeg.FullName "$Root\dist\tools\ffmpeg.exe" -Force
  $FfmpegLicense = Get-ChildItem $Extracted -Recurse -File -Filter "LICENSE.txt" | Select-Object -First 1
  if ($FfmpegLicense) {
    Copy-Item $FfmpegLicense.FullName "$Root\dist\tools\FFMPEG-LICENSE.txt" -Force
  } else {
    & curl.exe --fail --location --retry 3 `
      "https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/COPYING.GPLv3" `
      --output "$Root\dist\tools\FFMPEG-LICENSE.txt"
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg license download failed with exit code $LASTEXITCODE." }
  }
  $FfmpegReadme = Get-ChildItem $Extracted -Recurse -File -Filter "README.txt" | Select-Object -First 1
  if ($FfmpegReadme) { Copy-Item $FfmpegReadme.FullName "$Root\dist\tools\FFMPEG-BUILD-README.txt" -Force }
  @"
FFmpeg is a separate GPL-licensed program distributed alongside this project.
Binary build: $FfmpegUrl
Build scripts and corresponding source information: https://github.com/BtbN/FFmpeg-Builds
FFmpeg source and license information: https://github.com/FFmpeg/FFmpeg
"@ | Set-Content "$Root\dist\tools\FFMPEG-NOTICE.txt"
  Remove-Item $TempRoot -Recurse -Force
}

if ($env:IPAD_TABLET_SKIP_USB_TOOLS -ne "1") {
  $UsbToolsUrl = "https://github.com/jrjr/libimobiledevice-windows/releases/download/v20260809-74585f8/libimobile-suite-latest_w64.zip"
  $UsbToolsSha256 = "441d2180b6f4669668b037f57f9a3403c3315e4147a29316b358f8ea886ecec8"
  $TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ipad-tablet-usbmuxd-" + [guid]::NewGuid())
  $Archive = Join-Path $TempRoot "usbmuxd.zip"
  $Extracted = Join-Path $TempRoot "extracted"
  New-Item -ItemType Directory -Force $Extracted | Out-Null
  Write-Host "Downloading the bundled Windows USB tools..." -ForegroundColor Cyan
  & curl.exe --fail --location --retry 3 $UsbToolsUrl --output $Archive
  if ($LASTEXITCODE -ne 0) { throw "USB tools download failed with exit code $LASTEXITCODE." }
  $ActualHash = (Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($ActualHash -ne $UsbToolsSha256) {
    throw "USB tools checksum mismatch: expected $UsbToolsSha256, got $ActualHash."
  }
  Expand-Archive $Archive -DestinationPath $Extracted -Force
  $UsbToolsDestination = New-Item -ItemType Directory -Force "$Root\dist\tools\usbmuxd"
  Copy-Item "$Extracted\iproxy.exe" $UsbToolsDestination.FullName -Force
  Copy-Item "$Extracted\idevice_id.exe" $UsbToolsDestination.FullName -Force
  Copy-Item "$Extracted\idevicepair.exe" $UsbToolsDestination.FullName -Force
  Copy-Item "$Extracted\*.dll" $UsbToolsDestination.FullName -Force
  & curl.exe --fail --location --retry 3 `
    "https://raw.githubusercontent.com/jrjr/libimobiledevice-windows/v20260809-74585f8/LICENSE" `
    --output "$($UsbToolsDestination.FullName)\LIBIMOBILEDEVICE-LGPL-2.1.txt"
  if ($LASTEXITCODE -ne 0) { throw "USB tools LGPL license download failed with exit code $LASTEXITCODE." }
  & curl.exe --fail --location --retry 3 `
    "https://raw.githubusercontent.com/libimobiledevice/libusbmuxd/master/COPYING" `
    --output "$($UsbToolsDestination.FullName)\IPROXY-GPL-2.0.txt"
  if ($LASTEXITCODE -ne 0) { throw "iproxy GPL license download failed with exit code $LASTEXITCODE." }
  @"
iproxy and its runtime DLLs are separate open-source programs and libraries distributed alongside
this project. They are used only when USB transport is enabled.

Binary package: $UsbToolsUrl (automated Windows build of current upstream sources)
Pinned SHA-256: $UsbToolsSha256
Windows build project: https://github.com/jrjr/libimobiledevice-windows
Upstream libusbmuxd source: https://github.com/libimobiledevice/libusbmuxd
"@ | Set-Content "$($UsbToolsDestination.FullName)\USB-TOOLS-NOTICE.txt"
  & "$($UsbToolsDestination.FullName)\iproxy.exe" --help | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "The bundled iproxy.exe failed its startup check." }
  Remove-Item $TempRoot -Recurse -Force
}

Write-Host "Ready: $Root\dist" -ForegroundColor Green
