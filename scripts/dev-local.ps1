# Build sibling avn-hub (release), copy into Afterglow sidecar/, then run the app.
# Usage (from avn-hub-desktop repo root):
#   ./scripts/dev-local.ps1
#   ./scripts/dev-local.ps1 -SkipBuild
#   ./scripts/dev-local.ps1 -AvnHubRepo "C:\path\to\avn-hub"

param(
    [string]$AvnHubRepo = "",
    [switch]$SkipBuild,
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"
$desktopRoot = Split-Path -Parent $PSScriptRoot
Set-Location $desktopRoot

if (-not $AvnHubRepo) {
    $sibling = Join-Path (Split-Path -Parent $desktopRoot) "avn-hub"
    if (Test-Path (Join-Path $sibling "Cargo.toml")) {
        $AvnHubRepo = $sibling
    } else {
        throw "Could not find sibling avn-hub repo. Pass -AvnHubRepo <path>."
    }
}

$profile = if ($DebugBuild) { "debug" } else { "release" }
$exeName = "avn-hub.exe"
$built = Join-Path $AvnHubRepo "target\$profile\$exeName"
$sidecarDir = Join-Path $desktopRoot "src\Afterglow\sidecar"
$dest = Join-Path $sidecarDir $exeName

if (-not $SkipBuild) {
    Write-Host "Building avn-hub ($profile) in $AvnHubRepo ..."
    Push-Location $AvnHubRepo
    try {
        if ($DebugBuild) {
            cargo build -p avn-hub-server --bin avn-hub
        } else {
            cargo build --release -p avn-hub-server --bin avn-hub
        }
    } finally {
        Pop-Location
    }
}

if (-not (Test-Path $built)) {
    throw "Missing $built — build failed or wrong repo path."
}

New-Item -ItemType Directory -Force -Path $sidecarDir | Out-Null
Copy-Item $built $dest -Force
Write-Host "Sidecar installed: $dest"
Write-Host "Launching Afterglow (Local mode will use sidecar on first Use Local / cold start)..."
dotnet run --project src/Afterglow
