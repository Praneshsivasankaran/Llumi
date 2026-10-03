#!/usr/bin/env python3
"""Local/CI distribution guard tests. Never sign or submit; requires a built app."""
import json, os, pathlib, plistlib, shutil, subprocess, sys, tempfile, unittest
ROOT = pathlib.Path(__file__).resolve().parents[2]
APP = str(pathlib.Path(sys.argv.pop(1)).resolve())
SCRIPTS = ROOT / 'scripts/macos'
CANDIDATE = json.loads((ROOT/'macos/release-candidate.json').read_text())
class DistributionGuards(unittest.TestCase):
    def test_llumi_identity_and_current_visible_copy(self):
        info = plistlib.loads((pathlib.Path(APP)/'Contents/Info.plist').read_bytes())
        self.assertEqual(info['CFBundleName'], 'Llumi')
        self.assertEqual(info['CFBundleIdentifier'], 'io.github.praneshsivasankaran.llumi')
        self.assertEqual(info['CFBundleExecutable'], 'Llumi')
        self.assertEqual(info['CFBundleShortVersionString'], CANDIDATE['version'])
        for folder in ('Views', 'App', 'Notch'):
            for source in (ROOT/'macos/AgentMeter'/folder).glob('*.swift'):
                self.assertNotIn('AgentMeter', source.read_text(), str(source))
        self.assertIn('Track your AI coding usage.', (ROOT/'macos/AgentMeter/Views/MainView.swift').read_text())
    def test_windows_display_product_preserves_package_task(self):
        project = (ROOT/'windows/src/AgentMeter/AgentMeter.csproj').read_text()
        self.assertIn('<Product>Llumi</Product>', project)
        self.assertIn('<AssemblyName>Llumi</AssemblyName>', project)
        task = (ROOT/'windows/src/AgentMeter/PackagedStartupRegistration.cs').read_text()
        self.assertIn('TaskId = "AgentMeterStartup"', task)
        for name in ('UsageForm.cs', 'MonitorForm.cs', 'SetupForm.cs', 'TrayContext.cs'):
            for line in (ROOT/'windows/src/AgentMeter'/name).read_text().splitlines():
                if '"' in line: self.assertNotIn('AgentMeter', line)
    def test_setup_is_copy_only_and_notch_has_no_forced_dark_scheme(self):
        setup = (ROOT/'macos/AgentMeter/Views/SetupView.swift').read_text()
        flow = (ROOT/'macos/AgentMeter/Presentation/SetupFlow.swift').read_text()
        for forbidden in ('Process(', 'Subprocess.', 'NSAppleScript', 'OSAScript', 'URLSession', 'readLine('):
            self.assertNotIn(forbidden, setup + flow)
        self.assertIn('model.refreshAction()', setup)
        self.assertIn('NSPasteboard.general.setString(command', setup)
        notch = (ROOT/'macos/AgentMeter/Views/NotchView.swift').read_text()
        self.assertNotIn('environment(\\.colorScheme, .dark)', notch)
        self.assertNotIn('Native. Local. No AgentMeter account.',
                         (ROOT/'macos/AgentMeter/Views/MainView.swift').read_text())
    def test_production_name_without_packaging(self):
        r = self.run_script('create-dmg.sh', APP, '--print-production-name')
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.splitlines()[-1], f"Llumi-{CANDIDATE['version']}-macos.dmg")
    def test_rejects_placeholder_identity_and_incoherent_versions(self):
        for key, value in [('CFBundleIdentifier', 'local.agentmeter.mac'),
                           ('CFBundleShortVersionString', '0.1.0'),
                           ('CFBundleVersion', '1'),
                           ('LlumiReleaseVersion', '0.1.0-beta.3')]:
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                app = pathlib.Path(directory) / 'Llumi.app'
                shutil.copytree(APP, app)
                path = app / 'Contents/Info.plist'
                info = plistlib.loads(path.read_bytes()); info[key] = value
                path.write_bytes(plistlib.dumps(info))
                result = subprocess.run([sys.executable, str(SCRIPTS/'validate-bundle.py'), str(app)],
                                        capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
    def run_script(self, name, *args):
        env = os.environ.copy()
        env.pop('DEVELOPER_ID_APPLICATION', None)
        env.pop('NOTARY_PROFILE', None)
        return subprocess.run([str(SCRIPTS/name), *args], env=env, text=True, capture_output=True)
    def test_shell_syntax(self):
        for path in SCRIPTS.glob('*.sh'):
            self.assertEqual(subprocess.run(['/bin/bash', '-n', str(path)]).returncode, 0)
    def test_sign_defaults_to_plan_without_identity(self):
        r = self.run_script('sign-app.sh', APP)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn('PLAN:', r.stdout)
        self.assertIn('--options runtime', r.stdout)
        self.assertIn('no signing command executed', r.stdout)
    def test_notary_defaults_to_plan_without_credentials(self):
        r = self.run_script('notarize.sh', APP)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn('notarytool submit', r.stdout)
        self.assertIn('no Apple submission', r.stdout)
    def test_sign_requires_real_identity(self):
        r = self.run_script('sign-app.sh', APP, '--execute')
        self.assertNotEqual(r.returncode, 0)
        self.assertIn('Set DEVELOPER_ID_APPLICATION', r.stderr)
    def test_signed_candidate_packaging_requires_approved_identity(self):
        r = self.run_script('create-dmg.sh', APP, '--signed-candidate')
        self.assertNotEqual(r.returncode, 0)
        self.assertIn('Set DEVELOPER_ID_APPLICATION', r.stderr)
    def test_notary_requires_existing_profile(self):
        r = self.run_script('notarize.sh', APP, '--execute')
        self.assertNotEqual(r.returncode, 0)
        self.assertIn('Set NOTARY_PROFILE', r.stderr)
    def test_unsigned_cannot_pass_signed_verification(self):
        self.assertNotEqual(self.run_script('verify-release.sh', APP, '--signed').returncode, 0)
    def test_unknown_modes_rejected(self):
        for name in ('sign-app.sh', 'notarize.sh', 'create-dmg.sh', 'verify-release.sh'):
            self.assertNotEqual(self.run_script(name, APP, '--typo').returncode, 0)
    def test_unsigned_bundle_validation(self):
        r = self.run_script('verify-release.sh', APP, '--unsigned')
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn('NOT NOTARIZED', r.stdout)
    def test_unreviewed_sparkle_helper_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            app = pathlib.Path(directory) / 'Llumi.app'
            shutil.copytree(APP, app, symlinks=True)
            helper = app / 'Contents/Frameworks/Sparkle.framework/Versions/B/XPCServices/Unexpected.xpc/Contents/MacOS/Unexpected'
            helper.parent.mkdir(parents=True)
            shutil.copyfile(app/'Contents/MacOS/Llumi', helper)
            result = self.run_script('verify-release.sh', str(app), '--unsigned')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Unreviewed Sparkle files', result.stderr)
    def test_sparkle_symlink_escape_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            app = pathlib.Path(directory) / 'Llumi.app'
            shutil.copytree(APP, app, symlinks=True)
            link = app/'Contents/Frameworks/Sparkle.framework/Versions/Current'
            link.unlink()
            link.symlink_to('/tmp')
            result = self.run_script('verify-release.sh', str(app), '--unsigned')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Unreviewed Sparkle symlink layout', result.stderr)
unittest.main()
