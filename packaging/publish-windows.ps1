param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Framework = "net8.0-windows",
    [string]$AvnHubExe = "",
    [string]$Output = "publish/windows"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Publishing Afterglow ($Configuration / $Runtime / $Framework)..."
dotnet publish src/Afterglow/Afterglow.csproj `
    -c $Configuration `
    -f $Framework `
    -r $Runtime `
    --self-contained false `
    -o $Output

$sidecar = Join-Path $Output "sidecar"
New-Item -ItemType Directory -Force -Path $sidecar | Out-Null

if (-not $AvnHubExe) {
    $sibling = Join-Path (Split-Path -Parent $root) "avn-hub\target\release\avn-hub.exe"
    if (Test-Path $sibling) { $AvnHubExe = $sibling }
}

if ($AvnHubExe -and (Test-Path $AvnHubExe)) {
    Copy-Item $AvnHubExe (Join-Path $sidecar "avn-hub.exe") -Force
    Write-Host "Copied sidecar: $AvnHubExe"
} else {
    @"
Place avn-hub.exe here for Local mode.
Build from the avn-hub repo, download a Windows release asset, or pass -AvnHubExe.
"@ | Set-Content (Join-Path $sidecar "README.txt")
    Write-Host "No -AvnHubExe provided; wrote sidecar/README.txt placeholder."
}

Write-Host "Done: $Output"
