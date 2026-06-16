#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -d "MonBright.app" ]; then
    echo "MonBright.app not found. Run ./build.sh first."
    exit 1
fi

# Copy to ~/Applications (no admin needed)
mkdir -p "$HOME/Applications"
rm -rf "$HOME/Applications/MonBright.app"
cp -R "MonBright.app" "$HOME/Applications/"

PLIST="$HOME/Library/LaunchAgents/local.user.MonBright.plist"
mkdir -p "$HOME/Library/LaunchAgents"

cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>local.user.MonBright</string>
    <key>ProgramArguments</key>
    <array>
        <string>$HOME/Applications/MonBright.app/Contents/MacOS/MonBright</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <false/>
    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
EOF

# Kill every running MonBright process, including any started outside
# launchctl (e.g. double-clicked from Finder). With KeepAlive=false,
# `launchctl bootout` only unregisters the agent — it doesn't reliably
# terminate the running process, so we'd otherwise end up with two
# menu-bar icons after a re-install.
pkill -x MonBright 2>/dev/null || true
sleep 0.5

launchctl bootout "gui/$(id -u)/local.user.MonBright" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"

echo "Installed to ~/Applications/MonBright.app"
echo "LaunchAgent loaded — MonBright will start at login and is starting now."
echo "Look for the sun icon in your menu bar."
