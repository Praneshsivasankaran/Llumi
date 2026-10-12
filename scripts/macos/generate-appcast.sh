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
CONFIG=$(python3 - "$ROOT/macos/release-candidate.json" <<'CHECK'
import json, pathlib, sys
from urllib.parse import urlsplit
from release_policy import validate_update_configuration, MINIMUM_UPDATE_BUILD
candidate = json.loads(pathlib.Path(sys.argv[1]).read_text())
validate_update_configuration(candidate)
feed_name = pathlib.PurePosixPath(urlsplit(candidate['feed_url']).path).name
print('\t'.join([candidate['version'], candidate['build'], candidate['keychain_account'],
    candidate['feed_url'], feed_name, MINIMUM_UPDATE_BUILD, candidate['public_ed_key']]))
CHECK
)
IFS=$'\t' read -r VERSION BUILD ACCOUNT FEED_URL FEED_NAME MINIMUM_BUILD PUBLIC_ED_KEY <<< "$CONFIG"
DMG="$ARCHIVES/Llumi-$VERSION-macos.dmg"
PREFIX="https://github.com/Praneshsivasankaran/Llumi/releases/download/llumi-macos-$VERSION/"
COMMAND=("$BIN/generate_appcast" --account "$ACCOUNT"
  --download-url-prefix "$PREFIX" --maximum-deltas 0 --minimum-update-version "$MINIMUM_BUILD" --embed-release-notes
  --versions "$BUILD" -o "$ARCHIVES/$FEED_NAME" "$ARCHIVES")
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
"$BIN/sign_update" --account "$ACCOUNT" --verify "$ARCHIVES/$FEED_NAME"
python3 - "$ARCHIVES" "$VERSION" "$BUILD" "$PREFIX" "$BIN/sign_update" "$MODE" \
  "$ACCOUNT" "$FEED_URL" "$FEED_NAME" "$MINIMUM_BUILD" "$PUBLIC_ED_KEY" <<'CHECK'
import hashlib, json, pathlib, subprocess, sys, xml.etree.ElementTree as ET
from release_policy import merge_release_state
archives=pathlib.Path(sys.argv[1])
version,build,prefix,tool,mode,account,feed_url,feed_name,minimum_build,public_ed_key=sys.argv[2:]
feed=archives/feed_name
ns={'sparkle':'http://www.andymatuschak.org/xml-namespaces/sparkle'}
items=ET.parse(feed).findall('./channel/item')
item=next(x for x in items if x.findtext('sparkle:version',namespaces=ns)==build)
assert item.findtext('sparkle:shortVersionString',namespaces=ns)==version, 'Wrong appcast version'
assert item.findtext('sparkle:minimumSystemVersion',namespaces=ns)=='14.0', 'Wrong deployment target'
assert item.findtext('sparkle:minimumUpdateVersion',namespaces=ns)==minimum_build, 'Wrong updater host minimum'
assert item.find('description') is not None, 'Missing embedded release notes'
enclosure=item.find('enclosure');dmg=archives/f'Llumi-{version}-macos.dmg'
assert enclosure.attrib['url']==prefix+dmg.name, 'Unexpected enclosure URL'
assert int(enclosure.attrib['length'])==dmg.stat().st_size, 'Incorrect enclosure length'
signature=enclosure.attrib['{'+ns['sparkle']+'}edSignature']
subprocess.run([tool,'--account',account,'--verify',str(dmg),signature],check=True)
state_path=archives/'release-state.json'
previous=json.loads(state_path.read_text()) if state_path.exists() else {}
state=merge_release_state(previous, {'version':version,'build':build,'dmg':dmg.name,'sha256':hashlib.sha256(dmg.read_bytes()).hexdigest(),
    'feed':feed_name,'feed_url':feed_url,'feed_sha256':hashlib.sha256(feed.read_bytes()).hexdigest(),
    'keychain_account':account,'public_ed_key':public_ed_key,'minimum_update_version':minimum_build,
    'bytes':dmg.stat().st_size,'sparkle_signature_verified':True,'feed_signature_verified':True,
    'notarization_verified':mode=='--execute','publication_authorized':False,
    'status':'PRE-NOTARIZATION STAGED; REGENERATE AFTER STAPLING' if mode=='--stage-signed' else 'VERIFIED LOCALLY; OWNER PUBLICATION AUTHORIZATION REQUIRED'})
state_path.write_text(json.dumps(state,indent=2)+'\n')
CHECK
printf 'Generated locally: %s/%s. Publishing requires separate authorization.\n' "$ARCHIVES" "$FEED_NAME"
[[ "$MODE" != --stage-signed ]] || printf 'PRE-NOTARIZATION STAGED ONLY: regenerate signatures/checksum/feed from final stapled bytes.\n'
