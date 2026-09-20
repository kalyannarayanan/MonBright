#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="MonBright"
APP_BUNDLE="$APP_NAME.app"
EXEC_DIR="$APP_BUNDLE/Contents/MacOS"
EXEC_PATH="$EXEC_DIR/$APP_NAME"

rm -rf "$APP_BUNDLE"
mkdir -p "$EXEC_DIR" "$APP_BUNDLE/Contents/Resources"

# Build the app icon (.icns) from assets/icon.png if it's missing or
# the source PNG is newer than the cached .icns.
if [ ! -f MonBright.icns ] || [ assets/icon.png -nt MonBright.icns ]; then
    echo "Building app icon..."
    ./assets/build-icon.sh
fi
cp MonBright.icns "$APP_BUNDLE/Contents/Resources/MonBright.icns"

# Info.plist — LSUIElement=true makes this a menu-bar-only app
# (no Dock icon, no app menu).
cat > "$APP_BUNDLE/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>MonBright</string>
    <key>CFBundleIdentifier</key>
    <string>local.user.MonBright</string>
    <key>CFBundleName</key>
    <string>MonBright</string>
    <key>CFBundleDisplayName</key>
    <string>MonBright</string>
    <key>CFBundleIconFile</key>
    <string>MonBright</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

echo "Compiling menu-bar app..."
swiftc \
    -O \
    -framework Cocoa \
    -framework SwiftUI \
    -framework Combine \
    -framework IOKit \
    -framework CoreDisplay \
    -o "$EXEC_PATH" \
    main.swift

echo "Compiling DDC setter helper..."
swiftc \
    -O \
    -framework Foundation \
    -framework CoreGraphics \
    -framework IOKit \
    -framework CoreDisplay \
    -F /System/Library/PrivateFrameworks \
    -framework DisplayServices \
    -o "$EXEC_DIR/setter" \
    setter.swift

echo "Ad-hoc signing..."
codesign --force --sign - --timestamp=none "$EXEC_PATH"
codesign --force --sign - --timestamp=none "$EXEC_DIR/setter"
codesign --force --sign - --timestamp=none "$APP_BUNDLE"

echo
echo "Built $APP_BUNDLE"
echo "Run:    open $APP_BUNDLE"
echo "Install at login:  ./install.sh"
