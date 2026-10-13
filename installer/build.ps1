<#
.SYNOPSIS
    Builds both distributables:
      artifacts\publish\portable\KoalaDeck.exe          portable: one self-contained exe, runs from anywhere
      artifacts\installer\KoalaDeck-Setup-<version>.exe  installer: installs the app folder per-user
.DESCRIPTION
    The version comes from <Version> in src\KoalaDeck\KoalaDeck.csproj.
    Requires the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>
param(
    # Reuse the existing publish output and only rebuild the installer.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\KoalaDeck\KoalaDeck.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Koala Deck $version" -ForegroundColor Cyan

if (-not $SkipPublish) {
    foreach ($profile in 'portable', 'installer') {
        # Start from an empty folder so files removed from the app never linger in the package.
        Remove-Item (Join-Path $root "artifacts\publish\$profile") -Recurse -Force -ErrorAction SilentlyContinue
        dotnet publish $project -p:PublishProfile=$profile -nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Error 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup'
}

& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'KoalaDeck.iss')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-Item (Join-Path $root 'artifacts\publish\portable\KoalaDeck.exe'), (Join-Path $root "artifacts\installer\KoalaDeck-Setup-$version.exe") |
    ForEach-Object { Write-Host ("{0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
