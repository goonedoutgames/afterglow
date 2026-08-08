param(
    [string]$SourceDir = "publish/windows",
    [string]$OutputDir = "publish",
    [string]$AppVersion = "0.0.0",
    [string]$IconFile = "src/Afterglow/Assets/afterglow.ico",
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$source = Join-Path $root $SourceDir
if (-not (Test-Path (Join-Path $source "Afterglow.exe"))) {
    throw "Afterglow.exe not found in $source — run publish-windows.ps1 first."
}

$icon = Join-Path $root $IconFile
if (-not (Test-Path $icon)) {
    throw "Icon not found: $icon"
}

$out = Join-Path $root $OutputDir
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Find-ISCC {
    param([string]$Hint)
    if ($Hint -and (Test-Path $Hint)) { return (Resolve-Path $Hint).Path }
    $candidates = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$iscc = Find-ISCC -Hint $InnoCompiler
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isinfo.php or via chocolatey: choco install innosetup -y"
}

$iss = Join-Path $root "packaging\afterglow.iss"
Write-Host "Building installer with $iscc ..."
Write-Host "  Source : $source"
Write-Host "  Version: $AppVersion"
Write-Host "  Icon   : $icon"

& $iscc `
    "/DAppVersion=$AppVersion" `
    "/DSourceDir=$source" `
    "/DIconFile=$icon" `
    "/DOutputDir=$out" `
    $iss

$setup = Join-Path $out "Afterglow-Setup-x64.exe"
if (-not (Test-Path $setup)) {
    throw "Installer was not produced at $setup"
}

Get-Item $setup | Format-List FullName, Length, LastWriteTime
Write-Host "Done: $setup"
