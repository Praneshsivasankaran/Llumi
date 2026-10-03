#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# -ge 2 && $# -le 3 ]] || fail 'Usage: generate-appcast.sh SPARKLE_BIN ARCHIVES_DIR [--execute] (default: plan only)'
BIN="$1"; ARCHIVES="$2"; MODE="${3:---dry-run}"
[[ "$MODE" == --execute || "$MODE" == --dry-run ]] || fail 'Unknown mode'
[[ -x "$BIN/generate_appcast" ]] || fail 'Use the pinned official Sparkle 2.10.0 tools'
python3 - "$ROOT/scripts/macos/sparkle-layout.json" "$BIN/generate_appcast" <<'CHECK'
import hashlib, json, pathlib, sys
expected = json.loads(pathlib.Path(sys.argv[1]).read_text())['tools_sha256']['generate_appcast']
assert hashlib.sha256(pathlib.Path(sys.argv[2]).read_bytes()).hexdigest() == expected, 'Unreviewed Sparkle generator'
CHECK
VERSION=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$ROOT/macos/release-candidate.json")
BUILD=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["build"])' "$ROOT/macos/release-candidate.json")
DMG="$ARCHIVES/Llumi-$VERSION-macos.dmg"
PREFIX="https://github.com/Praneshsivasankaran/Llumi/releases/download/llumi-macos-$VERSION/"
COMMAND=("$BIN/generate_appcast" --account io.github.praneshsivasankaran.llumi.sparkle
  --download-url-prefix "$PREFIX" --maximum-deltas 0 --minimum-update-version 2
  --versions "$BUILD" -o "$ARCHIVES/appcast.xml" "$ARCHIVES")
if [[ "$MODE" == --dry-run ]]; then
  plan "${COMMAND[@]}"
  printf 'STAGED ONLY: no signature created and no feed published. Requires authorized final notarized DMG.\n'
  exit 0
fi
[[ -f "$DMG" ]] || fail 'Expected exact final candidate DMG'
"$ROOT/scripts/macos/verify-release.sh" "$DMG" --notarized
MOUNT=$(mktemp -d)
trap '/usr/bin/hdiutil detach "$MOUNT" >/dev/null 2>&1 || true; rmdir "$MOUNT" 2>/dev/null || true' EXIT
/usr/bin/hdiutil attach -readonly -nobrowse -mountpoint "$MOUNT" "$DMG" >/dev/null
"$ROOT/scripts/macos/verify-release.sh" "$MOUNT/Llumi.app" --notarized
"${COMMAND[@]}"
printf 'Generated locally: %s/appcast.xml. Publishing requires separate authorization.\n' "$ARCHIVES"
