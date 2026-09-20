#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")/.."

SRC="assets/icon.png"
OUT="MonBright.icns"
ICONSET="$(mktemp -d -t monbright-iconset)"
trap 'rm -rf "$ICONSET"' EXIT

if [ ! -f "$SRC" ]; then
    echo "Source icon not found at $SRC"
    exit 1
fi

ICONSET="$ICONSET/Icon.iconset"
mkdir -p "$ICONSET"

# Each row: target filename inside the .iconset, pixel size to render.
# macOS picks from this exact ladder — iconutil rejects iconsets with
# missing or oddly-named entries.
SIZES=(
    "icon_16x16.png 16"
    "icon_16x16@2x.png 32"
    "icon_32x32.png 32"
    "icon_32x32@2x.png 64"
    "icon_128x128.png 128"
    "icon_128x128@2x.png 256"
    "icon_256x256.png 256"
    "icon_256x256@2x.png 512"
    "icon_512x512.png 512"
    "icon_512x512@2x.png 1024"
)

for entry in "${SIZES[@]}"; do
    name="${entry%% *}"
    size="${entry##* }"
    sips -z "$size" "$size" "$SRC" --out "$ICONSET/$name" >/dev/null
done

iconutil -c icns "$ICONSET" -o "$OUT"

echo "Built $OUT ($(du -h "$OUT" | cut -f1))"
