$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

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

Write-Host "Ready: $Root\dist" -ForegroundColor Green
