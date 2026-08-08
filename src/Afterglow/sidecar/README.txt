Local avn-hub sidecar
=====================

Afterglow starts avn-hub.exe only in Local mode (never for Remote).

Lookup order:
1. This folder: sidecar/avn-hub.exe
2. AFTERGLOW_AVN_HUB_PATH environment variable
3. PATH
4. Sibling repo ../avn-hub/target/release|debug/avn-hub.exe
5. Auto `cargo build --release -p avn-hub-server --bin avn-hub` from that sibling repo, then copy here

Or download a CI release asset from avn-hub (avn-hub-windows-x64.exe / avn-hub.exe) into this folder.

Settings → "Prepare / rebuild sidecar" runs steps 1–5 (rebuild when a sibling repo exists).
"Factory reset" returns to the welcome screen so you can choose Local again.

Dev one-liner from avn-hub-desktop:
  ./scripts/dev-local.ps1
