$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet publish "$Root\src\IPadTablet.Backend\IPadTablet.Backend.csproj" `
  -c Release -r win-x64 --self-contained false `
  -o "$Root\dist\backend"

dotnet build "$Root\opentabletdriver\Plugin\IPadPencilWindowsHub.csproj" -c Release
New-Item -ItemType Directory -Force "$Root\dist\otd" | Out-Null
Copy-Item "$Root\opentabletdriver\Plugin\bin\Release\net8.0\IPadPencilWindowsHub.dll" "$Root\dist\otd\"
Copy-Item "$Root\opentabletdriver\Configurations\Apple-iPad-Pro-Windows.json" "$Root\dist\otd\"

Write-Host "Fertig: $Root\dist" -ForegroundColor Green
