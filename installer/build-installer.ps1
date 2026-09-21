#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the one-click DBLabs-Setup-<version>.exe installer.

.DESCRIPTION
    1. dotnet publish's the app as a self-contained win-x64 build (no .NET install
       needed on the target machine).
    2. Compiles installer\DBLabs.iss with Inno Setup's ISCC.exe into
       a single installer exe under dist\.

    Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php) — install via
    `winget install JRSoftware.InnoSetup` if ISCC.exe isn't already on PATH or in
    its default install location.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'publish'
$projectPath = Join-Path $repoRoot 'DBLabs'
$issPath = Join-Path $PSScriptRoot 'DBLabs.iss'

Write-Host "==> Publishing self-contained win-x64 build..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Locating Inno Setup (ISCC.exe)..." -ForegroundColor Cyan
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) {
        throw "ISCC.exe not found. Install Inno Setup 6 first: winget install JRSoftware.InnoSetup"
    }
    $iscc = $found
} else {
    $iscc = $iscc.Source
}

Write-Host "==> Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

$distDir = Join-Path $repoRoot 'dist'
Write-Host "==> Done. Installer(s) in $distDir :" -ForegroundColor Green
Get-ChildItem $distDir -Filter '*.exe' | ForEach-Object { Write-Host "    $($_.FullName)" }
