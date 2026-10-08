#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# == 2 ]] || fail 'Usage: create-dmg.sh APP --unsigned|--signed-candidate|--signed|--print-production-name'
APP="$(cd "$1" && pwd)"
MODE="$2"
[[ "$MODE" == --unsigned || "$MODE" == --signed-candidate || "$MODE" == --signed || "$MODE" == --print-production-name ]] || fail 'Choose --unsigned, --signed-candidate or --signed explicitly'
app_check "$APP"
VERSION=$(/usr/libexec/PlistBuddy -c 'Print :LlumiReleaseVersion' "$APP/Contents/Info.plist")
if [[ "$MODE" == --print-production-name ]]; then
  printf 'Llumi-%s-macos.dmg\n' "$VERSION"
  exit 0
fi
if [[ "$MODE" == --signed || "$MODE" == --signed-candidate ]]; then
  identity_check
  VERIFY=--notarized
  [[ "$MODE" != --signed-candidate ]] || VERIFY=--signed
  "$ROOT/scripts/macos/verify-release.sh" "$APP" "$VERIFY"
fi
VERSION=$(/usr/libexec/PlistBuddy -c 'Print :LlumiReleaseVersion' "$APP/Contents/Info.plist")
mkdir -p "$OUT"
SUFFIX=''; [[ "$MODE" != --unsigned ]] || SUFFIX='-unsigned'
DMG="$OUT/Llumi-$VERSION-macos$SUFFIX.dmg"
[[ ! -e "$DMG" ]] || fail 'Artifact already exists; will not overwrite.'
STAGE=$(mktemp -d "$OUT/.stage.XXXXXX")
trap 'rm -rf "$STAGE"' EXIT
ditto --norsrc --noextattr "$APP" "$STAGE/Llumi.app"
ln -s /Applications "$STAGE/Applications"
/usr/bin/hdiutil create -quiet -volname Llumi -srcfolder "$STAGE" -fs APFS -format ULFO "$DMG"
/usr/bin/hdiutil verify "$DMG"
if [[ "$MODE" == --signed || "$MODE" == --signed-candidate ]]; then
  /usr/bin/codesign --timestamp --sign "$DEVELOPER_ID_APPLICATION" "$DMG"
  printf 'Signed DMG; still requires notarization/stapling.\n'
else
  printf 'NOT SIGNED / NOT NOTARIZED: local packaging test only.\n'
fi
(cd "$OUT" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
printf '%s\n' "$DMG"
