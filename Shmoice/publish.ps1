# Builds a standalone Shmoice.exe, then (if Inno Setup is installed) an installer.
#   Exe:       publish\Shmoice.exe
#   Installer: publish\Shmoice-Setup-<version>.exe
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'Shmoice\Shmoice.csproj') -c Release -r win-x64 -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Write-Host "`nExe ready: $out\Shmoice.exe" -ForegroundColor Green

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc (Join-Path $root 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    Write-Host "Installer ready in $out" -ForegroundColor Green
} else {
    Write-Host 'Inno Setup not found - skipped the installer. Install it with: winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
}
