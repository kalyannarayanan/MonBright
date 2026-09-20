#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="MonBright"
APP_BUNDLE="$APP_NAME.app"
DMG_PATH="$APP_NAME.dmg"
VOL_NAME="MonBright"

if [ ! -d "$APP_BUNDLE" ]; then
    echo "$APP_BUNDLE not found — running ./build.sh first..."
    ./build.sh
fi

# Clear any stale quarantine attribute on the bundle before sealing it in.
xattr -dr com.apple.quarantine "$APP_BUNDLE" 2>/dev/null || true

STAGE="$(mktemp -d -t monbright-dmg)"
trap 'rm -rf "$STAGE"' EXIT

cp -R "$APP_BUNDLE" "$STAGE/"

# Drag-to-Applications shortcut. macOS resolves the symlink at drop time.
ln -s /Applications "$STAGE/Applications"

cat > "$STAGE/INSTALL.txt" <<'TXT'
MonBright — installation
========================

1. Drag MonBright.app onto the Applications shortcut in this window.

2. On first launch macOS will say one of:
       "MonBright is damaged and can't be opened."
       "MonBright cannot be opened because the developer cannot be verified."

   This is because the app is ad-hoc signed — it has no paid Apple
   Developer ID and was not notarized. To allow it to run, open Terminal
   and run:

       xattr -dr com.apple.quarantine /Applications/MonBright.app

   Then open the app normally from Launchpad or /Applications.

3. Look for a small monitor icon in the menu bar. Click it to see one
   brightness slider per connected display. F1 dims, F2 brightens the
   display under your cursor (hold fn on default keyboards).

Optional — auto-start at login:
   System Settings → General → Login Items → add MonBright.app under
   "Open at Login".

Requires Apple Silicon (M1 or later) and macOS 13+.
TXT

rm -f "$DMG_PATH"

echo "Building $DMG_PATH..."
hdiutil create \
    -volname "$VOL_NAME" \
    -srcfolder "$STAGE" \
    -ov \
    -format UDZO \
    -fs HFS+ \
    "$DMG_PATH" >/dev/null

SIZE="$(du -h "$DMG_PATH" | cut -f1)"
echo
echo "Built $DMG_PATH ($SIZE)"
echo "Share this file. Recipients drag MonBright.app to /Applications,"
echo "then run the xattr command from INSTALL.txt once to clear quarantine."
