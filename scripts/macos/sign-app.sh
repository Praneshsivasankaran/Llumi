#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# -ge 1 && $# -le 2 ]] || fail 'Usage: sign-app.sh APP [--execute] (default: plan only)'
APP="$(cd "$1" && pwd)"
MODE="${2:---dry-run}"
[[ "$MODE" == --execute || "$MODE" == --dry-run ]] || fail 'Unknown mode'
app_check "$APP"
# Reviewed Sparkle 2.10.0 helpers have empty entitlements. Sign explicitly inside-out;
# do not use --deep signing or apply Llumi entitlements to an unknown helper.
FRAMEWORK="$APP/Contents/Frameworks/Sparkle.framework"
CODE=("$FRAMEWORK/Versions/B/XPCServices/Downloader.xpc"
      "$FRAMEWORK/Versions/B/XPCServices/Installer.xpc"
      "$FRAMEWORK/Versions/B/Updater.app" "$FRAMEWORK/Versions/B/Autoupdate"
      "$FRAMEWORK" "$APP")
if [[ "$MODE" == --dry-run ]]; then
  for TARGET in "${CODE[@]}"; do
    plan /usr/bin/codesign --force --timestamp --options runtime --sign '<Developer ID Application identity>' "$TARGET"
  done
  printf 'NOT SIGNED / NOT NOTARIZED: no signing command executed.\n'
  exit 0
fi
identity_check
for TARGET in "${CODE[@]}"; do
  /usr/bin/codesign --force --timestamp --options runtime --sign "$DEVELOPER_ID_APPLICATION" "$TARGET"
done
"$ROOT/scripts/macos/verify-release.sh" "$APP" --signed
