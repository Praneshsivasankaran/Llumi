#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# -ge 2 && $# -le 3 ]] || fail 'Usage: generate-appcast.sh SPARKLE_BIN ARCHIVES_DIR [--stage-signed|--execute] (default: plan only)'
BIN="$1"; ARCHIVES="$2"; MODE="${3:---dry-run}"
[[ "$MODE" == --execute || "$MODE" == --stage-signed || "$MODE" == --dry-run ]] || fail 'Unknown mode'
[[ -x "$BIN/generate_appcast" ]] || fail 'Use the pinned official Sparkle 2.10.0 tools'
python3 - "$ROOT/scripts/macos/sparkle-layout.json" "$BIN" <<'CHECK'
import hashlib, json, pathlib, sys
expected = json.loads(pathlib.Path(sys.argv[1]).read_text())['tools_sha256']
for name in ['generate_appcast', 'sign_update']:
    assert hashlib.sha256((pathlib.Path(sys.argv[2])/name).read_bytes()).hexdigest() == expected[name], 'Unreviewed Sparkle tool'
CHECK
VERSION=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$ROOT/macos/release-candidate.json")
BUILD=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["build"])' "$ROOT/macos/release-candidate.json")
DMG="$ARCHIVES/Llumi-$VERSION-macos.dmg"
PREFIX="https://github.com/Praneshsivasankaran/Llumi/releases/download/llumi-macos-$VERSION/"
COMMAND=("$BIN/generate_appcast" --account io.github.praneshsivasankaran.llumi.sparkle
  --download-url-prefix "$PREFIX" --maximum-deltas 0 --minimum-update-version 2 --embed-release-notes
  --versions "$BUILD" -o "$ARCHIVES/appcast.xml" "$ARCHIVES")
if [[ "$MODE" == --dry-run ]]; then
  plan "${COMMAND[@]}"
  printf 'STAGED ONLY: no signature created and no feed published. Requires authorized final notarized DMG.\n'
  exit 0
fi
[[ -f "$DMG" ]] || fail 'Expected exact final candidate DMG'
VERIFY=--notarized
[[ "$MODE" != --stage-signed ]] || VERIFY=--signed
"$ROOT/scripts/macos/verify-release.sh" "$DMG" "$VERIFY"
[[ -f "$ARCHIVES/Llumi-$VERSION-macos.txt" ]] || fail 'Missing reviewed release notes'
"${COMMAND[@]}"
"$BIN/sign_update" --account io.github.praneshsivasankaran.llumi.sparkle --verify "$ARCHIVES/appcast.xml"
python3 - "$ARCHIVES" "$VERSION" "$BUILD" "$PREFIX" "$BIN/sign_update" "$MODE" <<'CHECK'
import hashlib, json, pathlib, subprocess, sys, xml.etree.ElementTree as ET
archives=pathlib.Path(sys.argv[1]);version,build,prefix,tool,mode=sys.argv[2:]
ns={'sparkle':'http://www.andymatuschak.org/xml-namespaces/sparkle'}
items=ET.parse(archives/'appcast.xml').findall('./channel/item')
item=next(x for x in items if x.findtext('sparkle:version',namespaces=ns)==build)
assert item.findtext('sparkle:shortVersionString',namespaces=ns)==version, 'Wrong appcast version'
assert item.findtext('sparkle:minimumSystemVersion',namespaces=ns)=='14.0', 'Wrong deployment target'
assert item.findtext('sparkle:minimumUpdateVersion',namespaces=ns)=='2', 'Wrong updater host minimum'
assert item.find('description') is not None, 'Missing embedded release notes'
enclosure=item.find('enclosure');dmg=archives/f'Llumi-{version}-macos.dmg'
assert enclosure.attrib['url']==prefix+dmg.name, 'Unexpected enclosure URL'
assert int(enclosure.attrib['length'])==dmg.stat().st_size, 'Incorrect enclosure length'
signature=enclosure.attrib['{'+ns['sparkle']+'}edSignature']
subprocess.run([tool,'--account','io.github.praneshsivasankaran.llumi.sparkle','--verify',str(dmg),signature],check=True)
state={'version':version,'build':build,'dmg':dmg.name,'sha256':hashlib.sha256(dmg.read_bytes()).hexdigest(),
    'bytes':dmg.stat().st_size,'sparkle_signature_verified':True,'feed_signature_verified':True,
    'notarization_verified':mode=='--execute','publication_authorized':False,
    'status':'PRE-NOTARIZATION STAGED; REGENERATE AFTER STAPLING' if mode=='--stage-signed' else 'VERIFIED LOCALLY; OWNER PUBLICATION AUTHORIZATION REQUIRED'}
(archives/'release-state.json').write_text(json.dumps(state,indent=2)+'\n')
CHECK
printf 'Generated locally: %s/appcast.xml. Publishing requires separate authorization.\n' "$ARCHIVES"
[[ "$MODE" != --stage-signed ]] || printf 'PRE-NOTARIZATION STAGED ONLY: regenerate signatures/checksum/feed from final stapled bytes.\n'
