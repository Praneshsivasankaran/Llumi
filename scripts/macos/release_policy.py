"""Pure release acceptance rules; no credentials, signing or network access."""
import base64, re

def validate_metadata(info, candidate, frozen):
    assert info['CFBundleIdentifier'] == candidate['bundle_id'] == frozen['bundle_id'], 'Unexpected bundle identifier'
    assert info['CFBundleExecutable'] == 'Llumi', 'Unexpected executable'
    version = info['CFBundleShortVersionString']
    assert re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version), 'Invalid version'
    assert version == info['LlumiReleaseVersion'] == candidate['version'], 'Candidate version mismatch'
    assert tuple(map(int, version.split('.'))) > tuple(map(int, frozen['version'].split('.'))), 'Frozen release version cannot be reused'
    build = info['CFBundleVersion']
    assert re.fullmatch(r'[1-9][0-9]*', build) and build == candidate['build'], 'Candidate build mismatch'
    assert int(build) > int(frozen['build']), 'Build must advance past frozen baseline'
    assert info['LSMinimumSystemVersion'] == '14.0', 'Review changed deployment target'
    assert info['SUFeedURL'] == candidate['feed_url'] == 'https://tryllumi.com/appcast.xml', 'Unexpected/non-HTTPS feed'
    key = info['SUPublicEDKey']
    assert key == candidate['public_ed_key'] and len(base64.b64decode(key, validate=True)) == 32, 'Invalid public key'
    assert info.get('SUVerifyUpdateBeforeExtraction') is True and info.get('SURequireSignedFeed') is True, 'Update signature verification required'
    assert info.get('SUSignedFeedFailureExpirationInterval') == 0, 'Signed feed failures must not expire'
    assert info.get('SUEnableAutomaticChecks') is True, '1.1.3 enables automatic checks; later user preferences persist'
    for field in ['SUAutomaticallyUpdate', 'SUAllowsAutomaticUpdates', 'SUSendProfileInfo', 'SUEnableSystemProfiling']:
        assert info.get(field) is False, f'{field} must be disabled'

def validate_publisher(text, identity, team, app=False):
    lines = text.splitlines()
    assert 'Authority=' + identity in lines, 'Wrong Developer ID Application publisher'
    assert 'TeamIdentifier=' + team in lines, 'Wrong Developer ID team'
    timestamps = [x.removeprefix('Timestamp=') for x in lines if x.startswith('Timestamp=')]
    assert len(timestamps) == 1 and timestamps[0] not in ['', 'none'], 'Missing secure timestamp'
    if app: assert any(x.startswith('CodeDirectory ') and '(runtime)' in x for x in lines), 'Missing Hardened Runtime'
