param([string]$OpenTabletDriverData = "$env:LOCALAPPDATA\OpenTabletDriver")
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Plugin = "$Root\dist\otd\IPadPencilWindowsHub.dll"
$Configuration = "$Root\dist\otd\Apple-iPad-Pro-Windows.json"

if (!(Test-Path $Plugin)) { throw "Erst .\build.ps1 ausführen." }
New-Item -ItemType Directory -Force "$OpenTabletDriverData\Plugins" | Out-Null
New-Item -ItemType Directory -Force "$OpenTabletDriverData\Configurations" | Out-Null
Copy-Item $Plugin "$OpenTabletDriverData\Plugins\" -Force
Copy-Item $Configuration "$OpenTabletDriverData\Configurations\" -Force
Write-Host "OTD-Modul installiert. OpenTabletDriver jetzt vollständig neu starten und Detect ausführen." -ForegroundColor Green
