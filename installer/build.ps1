<#
.SYNOPSIS
    Builds the portable exe and the installer:
      artifacts\publish\win-x64\KoalaDeck.exe          (self-contained, single file)
      artifacts\installer\KoalaDeck-Setup-<version>.exe
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
    dotnet publish $project -p:PublishProfile=win-x64 -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
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

Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter "KoalaDeck-Setup-$version.exe" |
    ForEach-Object { Write-Host ("Installer: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
