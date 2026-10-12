"""Pure release acceptance rules; no credentials, signing or network access."""
import base64, re

UPDATE_FEED_URL = 'https://tryllumi.com/appcast-v2.xml'
UPDATE_KEYCHAIN_ACCOUNT = 'io.github.praneshsivasankaran.llumi.sparkle.v2'
MINIMUM_UPDATE_BUILD = '5'
LEGACY_PUBLIC_ED_KEY = 'xLp/Wg9dAo3HWfBMc4LWEmz1LDbQJOUq9IuX4+h93C4='

def validate_update_configuration(candidate):
    assert candidate['feed_url'] == UPDATE_FEED_URL, 'Unexpected/non-HTTPS feed'
    assert candidate['keychain_account'] == UPDATE_KEYCHAIN_ACCOUNT, 'Unexpected updater Keychain account'
    assert re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', candidate['version']), 'Invalid version'
    build = candidate['build']
    assert re.fullmatch(r'[1-9][0-9]*', build) and int(build) >= int(MINIMUM_UPDATE_BUILD), 'Build predates new updater lineage'
    key = candidate['public_ed_key']
    try:
        decoded = base64.b64decode(key, validate=True)
    except (ValueError, TypeError):
        raise AssertionError('Invalid public key') from None
    assert len(decoded) == 32, 'Invalid public key'
    assert decoded != base64.b64decode(LEGACY_PUBLIC_ED_KEY), 'Legacy updater public key cannot be reused'
    assert base64.b64encode(decoded).decode('ascii') == key, 'Invalid public key encoding'

def merge_release_state(previous, verified):
    """Retain local provenance, without attaching another release's receipts."""
    assert isinstance(previous, dict), 'Invalid existing release state'
    for field in ['version', 'build', 'dmg', 'feed_url', 'keychain_account', 'public_ed_key']:
        if field in previous:
            assert previous[field] == verified[field], 'Existing release state belongs to another release or updater lineage'
    return {**previous, **verified}

def validate_metadata(info, candidate, frozen):
    assert info['CFBundleIdentifier'] == candidate['bundle_id'] == frozen['bundle_id'], 'Unexpected bundle identifier'
    assert 'LlumiLocalReview' not in info, 'Local review metadata must not enter a production release'
    assert info['CFBundleExecutable'] == 'Llumi', 'Unexpected executable'
    version = info['CFBundleShortVersionString']
    assert re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version), 'Invalid version'
    assert version == info['LlumiReleaseVersion'] == candidate['version'], 'Candidate version mismatch'
    assert tuple(map(int, version.split('.'))) > tuple(map(int, frozen['version'].split('.'))), 'Frozen release version cannot be reused'
    build = info['CFBundleVersion']
    assert re.fullmatch(r'[1-9][0-9]*', build) and build == candidate['build'], 'Candidate build mismatch'
    assert int(build) > int(frozen['build']), 'Build must advance past frozen baseline'
    assert info['LSMinimumSystemVersion'] == '14.0', 'Review changed deployment target'
    validate_update_configuration(candidate)
    assert info['SUFeedURL'] == candidate['feed_url'], 'Unexpected/non-HTTPS feed'
    key = info['SUPublicEDKey']
    assert key == candidate['public_ed_key'], 'Invalid public key'
    assert info.get('SUVerifyUpdateBeforeExtraction') is True and info.get('SURequireSignedFeed') is True, 'Update signature verification required'
    assert info.get('SUSignedFeedFailureExpirationInterval') == 0, 'Signed feed failures must not expire'
    assert info.get('SUEnableAutomaticChecks') is True, '1.1.3 enables automatic checks; later user preferences persist'
    assert info.get('SUAllowsAutomaticUpdates') is True, 'Automatic downloads must be available for explicit user opt-in'
    for field in ['SUAutomaticallyUpdate', 'SUSendProfileInfo', 'SUEnableSystemProfiling']:
        assert info.get(field) is False, f'{field} must be disabled'

def validate_publisher(text, identity, team, app=False):
    lines = text.splitlines()
    assert 'Authority=' + identity in lines, 'Wrong Developer ID Application publisher'
    assert 'TeamIdentifier=' + team in lines, 'Wrong Developer ID team'
    timestamps = [x.removeprefix('Timestamp=') for x in lines if x.startswith('Timestamp=')]
    assert len(timestamps) == 1 and timestamps[0] not in ['', 'none'], 'Missing secure timestamp'
    if app: assert any(x.startswith('CodeDirectory ') and '(runtime)' in x for x in lines), 'Missing Hardened Runtime'
