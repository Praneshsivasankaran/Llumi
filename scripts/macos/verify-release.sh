#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# == 2 ]] || fail 'Usage: verify-release.sh APP_OR_DMG --unsigned|--signed|--notarized'
TARGET="$1"; MODE="$2"
[[ -e "$TARGET" ]] || fail 'Artifact not found'
[[ "$MODE" == --unsigned || "$MODE" == --signed || "$MODE" == --notarized ]] || fail 'Unknown verification mode'
case "$TARGET" in
  *.app) app_check "$TARGET" ;;
  *.dmg) /usr/bin/hdiutil verify "$TARGET" ;;
  *) fail 'Expected .app or .dmg' ;;
esac
if [[ "$MODE" == --unsigned ]]; then
  printf 'NOT SIGNED (Developer ID) / NOT NOTARIZED: structural check only.\n'
else
  /usr/bin/codesign --verify --deep --strict --verbose=2 "$TARGET"
  INFO=$(/usr/bin/codesign -d --verbose=4 "$TARGET" 2>&1)
  printf '%s\n' "$INFO" | python3 -c 'import sys; from release_policy import validate_publisher; validate_publisher(sys.stdin.read(), "Developer ID Application: Pranesh S (K38622WCYD)", "K38622WCYD", sys.argv[1] == "app")' "$( [[ "$TARGET" == *.app ]] && printf app || printf dmg )"
  if [[ "$TARGET" == *.app ]]; then
    FRAMEWORK="$TARGET/Contents/Frameworks/Sparkle.framework/Versions/B"
    for CODE in "$FRAMEWORK/Autoupdate" "$FRAMEWORK/Updater.app" \
      "$FRAMEWORK/XPCServices/Installer.xpc" "$FRAMEWORK/XPCServices/Downloader.xpc" \
      "$TARGET/Contents/Frameworks/Sparkle.framework"; do
      /usr/bin/codesign -d --verbose=4 "$CODE" 2>&1 | python3 -c 'import sys; from release_policy import validate_publisher; validate_publisher(sys.stdin.read(), "Developer ID Application: Pranesh S (K38622WCYD)", "K38622WCYD")'
    done
    [[ "$INFO" == *'(runtime)'* ]] || fail 'Missing Hardened Runtime'
    ENT=$(mktemp)
    trap 'rm -f "$ENT"' EXIT
    /usr/bin/codesign -d --entitlements :- "$TARGET" > "$ENT" 2>/dev/null
    python3 - "$ENT" <<'CHECK'
import pathlib, plistlib, sys
raw = pathlib.Path(sys.argv[1]).read_bytes()
assert not raw or not plistlib.loads(raw), 'Unexpected entitlements; review before release'
CHECK
  fi
  if [[ "$MODE" == --notarized ]]; then
    xcrun stapler validate "$TARGET"
    if [[ "$TARGET" == *.app ]]; then
      /usr/sbin/spctl --assess --type execute --verbose=2 "$TARGET"
    else
      /usr/sbin/spctl --assess --type open --context context:primary-signature --verbose=2 "$TARGET"
    fi
  fi
fi
if [[ "$TARGET" == *.dmg ]]; then
  # A validly signed container is insufficient: inspect the contained product too.
  MOUNT=$(mktemp -d)
  trap '/usr/bin/hdiutil detach "$MOUNT" >/dev/null 2>&1 || true; rmdir "$MOUNT" 2>/dev/null || true' EXIT
  /usr/bin/hdiutil attach -readonly -nobrowse -mountpoint "$MOUNT" "$TARGET" >/dev/null
  "$ROOT/scripts/macos/verify-release.sh" "$MOUNT/Llumi.app" "$MODE"
  /usr/bin/hdiutil detach "$MOUNT" >/dev/null
  rmdir "$MOUNT"
  trap - EXIT
  (cd "$(dirname "$TARGET")" && shasum -a 256 "$(basename "$TARGET")" > "$(basename "$TARGET").sha256")
fi
