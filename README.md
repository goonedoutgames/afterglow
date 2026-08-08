# Afterglow

Desktop client for [AVN Hub](https://github.com/goonedoutgames/avn-hub) — browse F95Zone, manage your library, download games, track playtime, and sync Ren'Py saves.

Built with **.NET 8 + Avalonia (Skia)** for **Windows**. Interactive hoster downloads open **Afterglow Browser** (WebView2) so captchas/timers can be completed and files are intercepted in-app.

## Dual hub mode (exclusive)

| Mode | What happens |
|------|----------------|
| **Remote** | Talks only to your hosted AVN Hub API. **Never** starts the embedded hub. |
| **Local** | Spawns bundled `avn-hub.exe` on `127.0.0.1:18080`. Data lives under `%AppData%/Afterglow/hub-data`. |

Local mode is **not a backup**. If you care about saves/playtime across machines, use Remote.

Install paths, library folders, and UI prefs stay on this PC (Steam-like). Playtime, saves, patches, and library metadata sync through AVN Hub.

## Requirements

- .NET 8 SDK (**Windows** — target `net8.0-windows`)
- Microsoft Edge **WebView2 Runtime** for Afterglow Browser download capture
- For **Local** mode: `avn-hub.exe` in `sidecar/`, on `PATH`, `AFTERGLOW_AVN_HUB_PATH`, or a sibling `avn-hub` repo (auto cargo build)
- For **Remote** mode: a reachable AVN Hub API
- Optional: Rust/`cargo` next to this repo for Local sidecar builds

## Run

```bash
cd avn-hub-desktop
dotnet run --project src/Afterglow
```

Use `net8.0-windows` (the project default). Do not retarget to plain `net8.0` — that drops WebView2 / Afterglow Browser.

### Local mode (dev): build hub + attach sidecar + launch

With `avn-hub` cloned as a sibling of `avn-hub-desktop`:

```powershell
cd ../avn-hub
cargo build --release -p avn-hub-server --bin avn-hub

cd ../avn-hub-desktop
New-Item -ItemType Directory -Force -Path src\Afterglow\sidecar | Out-Null
Copy-Item ..\avn-hub\target\release\avn-hub.exe src\Afterglow\sidecar\avn-hub.exe -Force
dotnet run --project src/Afterglow
```

Or one shot:

```powershell
./scripts/dev-local.ps1
```

In the app: **Use Local**, or Settings → **Prepare / rebuild sidecar** (auto-finds/builds if needed).

## Solution layout

```
src/Afterglow            # Avalonia UI
src/Afterglow.Core       # Models, paths, backend mode
src/Afterglow.HubClient  # HTTP client for /api/v1
src/Afterglow.LocalStore # Machine-local SQLite
src/Afterglow.Downloads  # GoFile / Mega / Pixeldrain / HTTP + extract
src/Afterglow.Launcher   # Launch, playtime queue, Ren'Py save upload
src/Afterglow.HubSidecar # Embedded avn-hub process (Local only)
src/Afterglow.BrowserHost # WebView2 Afterglow Browser
```

## Packaging

See [packaging/README.md](packaging/README.md).

```powershell
./packaging/publish-windows.ps1
# optional installer (needs Inno Setup 6):
./packaging/build-installer.ps1 -AppVersion 0.1.2
```

CI (`.github/workflows/package.yml`) publishes a portable zip and a Setup.exe; tag `v*` creates a GitHub Release with both.

## MVP download hosts

GoFile, Mega (limited — may need external handling), Pixeldrain, direct HTTP(S). Unknown hosts can still be opened/queued when a direct URL is available.

## Overlay

In-game overlay is **v2** — not in this MVP.
