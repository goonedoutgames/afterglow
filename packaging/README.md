# Packaging for Afterglow

## Windows — `publish-windows.ps1`

```powershell
./packaging/publish-windows.ps1 -AvnHubExe "C:\path\to\avn-hub.exe"
```

Output: `publish/windows/` (`net8.0-windows` + WebView2 Afterglow Browser)

### Sidecar layout

```
publish/windows/
  Afterglow.exe
  sidecar/
    avn-hub.exe
```

### CI — `.github/workflows/package.yml`

Builds and uploads `Afterglow-windows-x64.zip`. On `v*` tags, publishes a GitHub Release.

Optional repo settings:

- Variable `HUB_REPO` (default `goonedoutgames/avn-hub`)
- Secret `HUB_GITHUB_TOKEN` if the hub repo is private
- `workflow_dispatch` input `hub_tag` to pin a hub release
