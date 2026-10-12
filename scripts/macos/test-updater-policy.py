#!/usr/bin/env python3
"""Security regression tests; never sign, read a private key, or contact a feed."""
import copy, json, pathlib, plistlib, unittest
from release_policy import validate_metadata, validate_publisher, merge_release_state, LEGACY_PUBLIC_ED_KEY
ROOT = pathlib.Path(__file__).resolve().parents[2]

class UpdaterPolicyTests(unittest.TestCase):
    def setUp(self):
        self.candidate = json.loads((ROOT/'macos/release-candidate.json').read_text())
        self.frozen = json.loads((ROOT/'docs/releases/launch-baselines.json').read_text())['macos']
        self.info = plistlib.loads((ROOT/'macos/AgentMeter/Info.plist').read_bytes())
        self.info.update(CFBundleIdentifier=self.candidate['bundle_id'], CFBundleExecutable='Llumi',
            CFBundleShortVersionString=self.candidate['version'], LlumiReleaseVersion=self.candidate['version'],
            CFBundleVersion=self.candidate['build'], LSMinimumSystemVersion='14.0')
        self.signed = '\n'.join(['Authority='+self.candidate['developer_id_identity'],
            'TeamIdentifier='+self.candidate['developer_id_team'], 'Timestamp=Oct 3, 2026',
            'CodeDirectory v=20500 size=123 flags=0x10000(runtime)'])
    def check(self, info=None, candidate=None):
        validate_metadata(info or self.info, candidate or self.candidate, self.frozen)
    def publisher(self, text):
        validate_publisher(text, self.candidate['developer_id_identity'], self.candidate['developer_id_team'], app=True)
    def test_candidate_passes_and_frozen_baseline_is_distinct(self):
        self.check()
        self.assertEqual((self.frozen['version'], self.frozen['build']), ('1.1.1','1'))
        self.assertEqual(self.frozen['source_commit'], 'af2199b572a8b2bb4273664ed38d87476a21eca1')
    def test_frozen_version_cannot_be_reused_even_with_matching_candidate_configuration(self):
        info=copy.deepcopy(self.info); candidate=copy.deepcopy(self.candidate)
        info['LlumiReleaseVersion']=info['CFBundleShortVersionString']=candidate['version']='1.1.1'
        with self.assertRaises(AssertionError): self.check(info,candidate)
    def test_build_must_advance_past_baseline(self):
        info=copy.deepcopy(self.info);candidate=copy.deepcopy(self.candidate)
        info['CFBundleVersion']=candidate['build']='1'
        with self.assertRaises(AssertionError): self.check(info,candidate)
    def test_wrong_bundle_or_incoherent_version_rejected(self):
        for key,value in [('CFBundleIdentifier','example.other'),('LlumiReleaseVersion','1.1.1'),('CFBundleVersion',str(int(self.candidate['build'])+1))]:
            with self.subTest(key=key):
                info=copy.deepcopy(self.info);info[key]=value
                with self.assertRaises(AssertionError):self.check(info)
    def test_review_marker_rejected_even_with_production_identity_and_feed(self):
        # Review output disables the native updater. Reject the marker's
        # presence, including malformed/false values, before distribution.
        for value in [True, False, None, 0, 1, 'false', 'true']:
            with self.subTest(value=value):
                info=copy.deepcopy(self.info);info['LlumiLocalReview']=value
                with self.assertRaisesRegex(AssertionError, 'Local review metadata'):
                    self.check(info)
    def test_non_https_or_different_feed_rejected(self):
        for url in ['https://tryllumi.com/appcast.xml','http://tryllumi.com/appcast-v2.xml',
                    'https://example.invalid/appcast-v2.xml','file:///tmp/appcast-v2.xml']:
            info=copy.deepcopy(self.info);info['SUFeedURL']=url
            with self.assertRaises(AssertionError):self.check(info)
    def test_feed_allowlist_cannot_be_bypassed_with_matching_candidate(self):
        for url in ['https://tryllumi.com/appcast.xml','http://tryllumi.com/appcast-v2.xml',
                    'https://example.invalid/appcast-v2.xml','https://tryllumi.com/appcast-v2.xml?preview=1',
                    'https://tryllumi.com/appcast-v2.xml/','https://user@tryllumi.com/appcast-v2.xml']:
            with self.subTest(url=url):
                info=copy.deepcopy(self.info);candidate=copy.deepcopy(self.candidate)
                info['SUFeedURL']=candidate['feed_url']=url
                with self.assertRaisesRegex(AssertionError, 'Unexpected/non-HTTPS feed'):
                    self.check(info,candidate)
    def test_legacy_or_unexpected_signing_account_rejected(self):
        for account in ['io.github.praneshsivasankaran.llumi.sparkle','ed25519',
                        'io.github.praneshsivasankaran.llumi.sparkle.v2.extra','']:
            with self.subTest(account=account):
                candidate=copy.deepcopy(self.candidate);candidate['keychain_account']=account
                with self.assertRaisesRegex(AssertionError, 'Unexpected updater Keychain account'):
                    self.check(candidate=candidate)
    def test_legacy_key_rejected_even_with_matching_candidate(self):
        info=copy.deepcopy(self.info);candidate=copy.deepcopy(self.candidate)
        info['SUPublicEDKey']=candidate['public_ed_key']=LEGACY_PUBLIC_ED_KEY
        with self.assertRaisesRegex(AssertionError, 'Legacy updater public key'):
            self.check(info,candidate)
    def test_invalid_public_key_rejected_even_with_matching_candidate(self):
        for key in ['not-base64', '', 'YQ==', None]:
            with self.subTest(key=key):
                info=copy.deepcopy(self.info);candidate=copy.deepcopy(self.candidate)
                info['SUPublicEDKey']=candidate['public_ed_key']=key
                with self.assertRaisesRegex(AssertionError, 'Invalid public key'):
                    self.check(info,candidate)
    def test_new_lineage_cannot_reuse_previous_local_build(self):
        info=copy.deepcopy(self.info);candidate=copy.deepcopy(self.candidate)
        info['CFBundleVersion']=candidate['build']='4'
        with self.assertRaisesRegex(AssertionError, 'Build predates new updater lineage'):
            self.check(info,candidate)
    def test_unknown_key_or_disabled_verification_rejected(self):
        for key,value in [('SUPublicEDKey','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='),('SUVerifyUpdateBeforeExtraction',False),('SURequireSignedFeed',False),('SUSignedFeedFailureExpirationInterval',1728000)]:
            info=copy.deepcopy(self.info);info[key]=value
            with self.assertRaises(AssertionError):self.check(info)
    def test_telemetry_and_default_automatic_downloads_rejected(self):
        for key in ['SUSendProfileInfo','SUEnableSystemProfiling','SUAutomaticallyUpdate']:
            for value in [True, None, 0, 'false']:
                with self.subTest(key=key,value=value):
                    info=copy.deepcopy(self.info);info[key]=value
                    with self.assertRaises(AssertionError):self.check(info)
    def test_automatic_downloads_available_only_as_explicit_opt_in(self):
        self.assertIs(self.info['SUAllowsAutomaticUpdates'], True)
        self.assertIs(self.info['SUAutomaticallyUpdate'], False)
        self.check()
        for value in [False, None, 1, 'true']:
            with self.subTest(value=value):
                info=copy.deepcopy(self.info);info['SUAllowsAutomaticUpdates']=value
                with self.assertRaises(AssertionError):self.check(info)
    def test_auto_checks_default_enabled_and_missing_default_rejected(self):
        for value in [False, None]:
            info=copy.deepcopy(self.info);info['SUEnableAutomaticChecks']=value
            with self.assertRaises(AssertionError):self.check(info)
    def test_expected_publisher_and_team_pass(self):self.publisher(self.signed)
    def test_other_valid_developer_id_or_prefix_match_rejected(self):
        for name in ['Developer ID Application: Other Publisher (K38622WCYD)',self.candidate['developer_id_identity']+' extra']:
            with self.assertRaises(AssertionError):self.publisher(self.signed.replace(self.candidate['developer_id_identity'],name))
    def test_wrong_team_or_missing_secure_timestamp_rejected(self):
        for text in [self.signed.replace('K38622WCYD\n','OTHERTEAM\n'), self.signed.replace('Timestamp=Oct 3, 2026','Timestamp=none')]:
            with self.assertRaises(AssertionError):self.publisher(text)
    def test_app_without_hardened_runtime_rejected(self):
        with self.assertRaises(AssertionError):self.publisher(self.signed.replace('(runtime)',''))
    def test_dependency_is_exact_and_matches_resolved_revision(self):
        project=plistlib.loads((ROOT/'macos/AgentMeter.xcodeproj/project.pbxproj').read_bytes())
        packages=[x for x in project['objects'].values() if x['isa']=='XCRemoteSwiftPackageReference']
        self.assertEqual(len(packages),1)
        self.assertEqual(packages[0]['requirement'],{'kind':'exactVersion','version':self.candidate['sparkle_version']})
        pins=json.loads((ROOT/'macos/AgentMeter.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved').read_text())['pins']
        self.assertEqual(pins[0]['state']['revision'], self.candidate['sparkle_revision'])

class ReleaseStateTests(unittest.TestCase):
    def setUp(self):
        self.verified={'version':'2.2.0','build':'5','dmg':'Llumi-2.2.0-macos.dmg',
            'feed_url':'https://tryllumi.com/appcast-v2.xml',
            'keychain_account':'io.github.praneshsivasankaran.llumi.sparkle.v2',
            'public_ed_key':'synthetic-public-key', 'sha256':'final-hash',
            'sparkle_signature_verified':True,'feed_signature_verified':True,'publication_authorized':False}
    def test_verification_preserves_provenance_and_updates_current_result(self):
        previous={'version':'2.2.0','build':'5','sha256':'pre-stapling-hash',
            'source_commit':'synthetic-commit','notarization':{'app':'accepted','dmg':'accepted'},
            'native_checks':{'background_launch':True},'sparkle_signature_verified':False}
        result=merge_release_state(previous,self.verified)
        for field in ['source_commit','notarization','native_checks']:
            self.assertEqual(result[field],previous[field])
        self.assertEqual(result['sha256'],'final-hash')
        self.assertIs(result['sparkle_signature_verified'],True)
        self.assertIs(result['publication_authorized'],False)
        self.assertEqual(previous['sha256'],'pre-stapling-hash')
    def test_another_release_or_lineage_cannot_supply_provenance(self):
        for field,value in [('version','1.1.3'),('build','4'),('dmg','other.dmg'),
                            ('feed_url','https://tryllumi.com/appcast.xml'),
                            ('keychain_account','io.github.praneshsivasankaran.llumi.sparkle'),
                            ('public_ed_key',LEGACY_PUBLIC_ED_KEY)]:
            with self.subTest(field=field):
                with self.assertRaisesRegex(AssertionError, 'another release or updater lineage'):
                    merge_release_state({field:value},self.verified)
    def test_missing_state_can_be_initialized_but_invalid_state_is_rejected(self):
        self.assertEqual(merge_release_state({},self.verified),self.verified)
        for previous in [None, [], 'invalid']:
            with self.subTest(previous=previous), self.assertRaisesRegex(AssertionError, 'Invalid existing release state'):
                merge_release_state(previous,self.verified)

if __name__=='__main__':unittest.main()
