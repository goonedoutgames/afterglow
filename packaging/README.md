# Packaging for Afterglow

## Windows — `publish-windows.ps1`

```powershell
./packaging/publish-windows.ps1 -AvnHubExe "C:\path\to\avn-hub.exe"
```

Output: `publish/windows/` (`net8.0-windows` + WebView2 Afterglow Browser)

## Linux — `publish-linux.sh`

```bash
chmod +x packaging/publish-linux.sh
AvnHubBin=/path/to/avn-hub ./packaging/publish-linux.sh
```

Output: `publish/linux/` (`net8.0`). Interactive hoster browser is **Windows-only**.

### Sidecar layout

```
publish/windows/          publish/linux/
  Afterglow.exe             Afterglow
  sidecar/                  sidecar/
    avn-hub.exe               avn-hub
```

### CI — `.github/workflows/package.yml`

| Job | Behavior |
|-----|----------|
| `build-windows` | Required. Builds, bundles latest hub Windows sidecar, uploads zip. |
| `build-linux` | Best-effort (`continue-on-error`). Builds + optional Linux hub sidecar. |
| `release` | On `v*` tags: always publishes Windows; attaches Linux only when `packaged=true` (real success). |

Optional repo settings:

- Variable `HUB_REPO` (default `goonedoutgames/avn-hub`)
- Secret `HUB_GITHUB_TOKEN` if the hub repo is private
- `workflow_dispatch` input `hub_tag` to pin a hub release
