#!/bin/bash
set -euo pipefail

LABEL="local.user.MonBright"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
rm -f "$PLIST"
rm -rf "$HOME/Applications/MonBright.app"

echo "MonBright removed."
