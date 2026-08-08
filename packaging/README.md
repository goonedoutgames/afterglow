# Packaging for Afterglow

## Windows portable — `publish-windows.ps1`

```powershell
./packaging/publish-windows.ps1 -AvnHubExe "C:\path\to\avn-hub.exe"
```

Output: `publish/windows/` (`net8.0-windows` + WebView2 Afterglow Browser)

## Windows installer — `build-installer.ps1`

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`ISCC.exe`).

```powershell
./packaging/publish-windows.ps1 -AvnHubExe "C:\path\to\avn-hub.exe"
./packaging/build-installer.ps1 -AppVersion 0.1.2
```

Output: `publish/Afterglow-Setup-x64.exe`

### Sidecar layout

```
publish/windows/
  Afterglow.exe
  sidecar/
    avn-hub.exe
```

### App icon

Multi-size `src/Afterglow/Assets/afterglow.ico` (taskbar / exe / installer).

Regenerate from the current source art:

```powershell
dotnet run --project tools/IconTool -- src/Afterglow/Assets/avalonia-logo.ico src/Afterglow/Assets/afterglow.ico
# or from a new PNG/SVG-exported PNG:
dotnet run --project tools/IconTool -- path\to\logo.png src/Afterglow/Assets/afterglow.ico
```

### CI — `.github/workflows/package.yml`

On every run: portable zip + Setup.exe artifacts.  
On `v*` tags: GitHub Release with both files.

Optional repo settings:

- Variable `HUB_REPO` (default `goonedoutgames/avn-hub`)
- Secret `HUB_GITHUB_TOKEN` if the hub repo is private
- `workflow_dispatch` input `hub_tag` to pin a hub release
