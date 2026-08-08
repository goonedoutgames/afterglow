#!/usr/bin/env bash
set -euo pipefail

Configuration="${Configuration:-Release}"
Runtime="${Runtime:-linux-x64}"
AvnHubBin="${AvnHubBin:-}"
Output="${Output:-publish/linux}"

Root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$Root"

echo "Publishing Afterglow ($Configuration / $Runtime / net8.0)..."
dotnet publish src/Afterglow/Afterglow.csproj \
  -c "$Configuration" \
  -f net8.0 \
  -r "$Runtime" \
  --self-contained false \
  -o "$Output"

sidecar="$Output/sidecar"
mkdir -p "$sidecar"

if [[ -z "$AvnHubBin" ]]; then
  sibling="$(cd "$Root/.." && pwd)/avn-hub/target/release/avn-hub"
  if [[ -f "$sibling" ]]; then
    AvnHubBin="$sibling"
  fi
fi

if [[ -n "$AvnHubBin" && -f "$AvnHubBin" ]]; then
  cp -f "$AvnHubBin" "$sidecar/avn-hub"
  chmod +x "$sidecar/avn-hub"
  echo "Copied sidecar: $AvnHubBin"
else
  cat > "$sidecar/README.txt" <<'EOF'
Place a Linux avn-hub binary here for Local mode (name it avn-hub).
Build from the avn-hub repo, download a Linux release asset, or pass AvnHubBin=.
Interactive hoster downloads (Afterglow Browser / WebView2) are Windows-only.
EOF
  echo "No AvnHubBin provided; wrote sidecar/README.txt placeholder."
fi

echo "Done: $Output"
