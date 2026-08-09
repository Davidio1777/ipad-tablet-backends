param([string]$OpenTabletDriverData = "$env:LOCALAPPDATA\OpenTabletDriver")
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Plugin = "$Root\dist\otd\IPadPencilWindowsHub.dll"
$Configuration = "$Root\dist\otd\Apple-iPad-Pro-Windows.json"

if (!(Test-Path $Plugin)) { throw "Run .\build.ps1 first." }
New-Item -ItemType Directory -Force "$OpenTabletDriverData\Plugins" | Out-Null
New-Item -ItemType Directory -Force "$OpenTabletDriverData\Configurations" | Out-Null
Copy-Item $Plugin "$OpenTabletDriverData\Plugins\" -Force
Copy-Item $Configuration "$OpenTabletDriverData\Configurations\" -Force
Write-Host "OTD module installed. Restart OpenTabletDriver completely and run Detect." -ForegroundColor Green
