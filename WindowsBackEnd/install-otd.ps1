param([string]$OpenTabletDriverData = "$env:LOCALAPPDATA\OpenTabletDriver")
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Plugin = "$Root\dist\otd\IPadPencilWindowsHub.dll"
$Configuration = "$Root\dist\otd\Apple-iPad-Pro-Windows.json"

if (!(Test-Path $Plugin)) { throw "Run .\build.ps1 first." }
New-Item -ItemType Directory -Force "$OpenTabletDriverData\Plugins\IPadPencilWindowsHub" | Out-Null
New-Item -ItemType Directory -Force "$OpenTabletDriverData\Configurations" | Out-Null
Copy-Item $Plugin "$OpenTabletDriverData\Plugins\IPadPencilWindowsHub\" -Force
Copy-Item $Configuration "$OpenTabletDriverData\Configurations\" -Force
$LegacyPlugin = "$OpenTabletDriverData\Plugins\IPadPencilWindowsHub.dll"
if (Test-Path $LegacyPlugin) { Remove-Item -LiteralPath $LegacyPlugin -Force }
Write-Host "OTD module installed. Start the backend to enable the plugin and detect the iPad." -ForegroundColor Green
