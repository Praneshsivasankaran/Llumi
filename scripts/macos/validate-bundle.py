#!/usr/bin/env python3
"""Reject unexpected candidate identity, versions and nested code before distribution."""
import json, pathlib, plistlib, subprocess, sys
from release_policy import validate_metadata
ROOT = pathlib.Path(__file__).resolve().parents[2]

def validate(app):
    app = pathlib.Path(app).resolve()
    info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
    candidate = json.loads((ROOT / 'macos/release-candidate.json').read_text())
    frozen = json.loads((ROOT / 'docs/releases/launch-baselines.json').read_text())['macos']
    validate_metadata(info, candidate, frozen)
    framework = app / 'Contents/Frameworks/Sparkle.framework'
    layout = json.loads((ROOT / 'scripts/macos/sparkle-layout.json').read_text())
    assert framework.is_dir() and not framework.is_symlink(), 'Missing/unexpected Sparkle framework'
    links = {str(p.relative_to(framework)):str(p.readlink()) for p in framework.rglob('*') if p.is_symlink()}
    assert links == layout['symlinks'], 'Unreviewed Sparkle symlink layout'
    for path in framework.rglob('*'):
        assert path.resolve().is_relative_to(framework), 'Sparkle symlink escapes framework'
    files = {str(p.relative_to(framework)) for p in framework.rglob('*') if p.is_file() and not p.is_symlink() and '_CodeSignature' not in p.parts}
    assert files == set(layout['files']), 'Unreviewed Sparkle files'
    for path in framework.rglob('Info.plist'):
        if path.is_symlink(): continue
        metadata = plistlib.loads(path.read_bytes())
        assert metadata['CFBundleShortVersionString'] == candidate['sparkle_version'], 'Sparkle version mismatch'
    allowed_code = {'Contents/MacOS/Llumi'} | {'Contents/Frameworks/Sparkle.framework/' + x for x in layout['executables']}
    allowed_dirs = {'Contents', 'Contents/MacOS', 'Contents/Resources', 'Contents/Frameworks', 'Contents/Frameworks/Sparkle.framework'}
    for path in app.rglob('*'):
        rel = path.relative_to(app).as_posix()
        if rel.startswith('Contents/Frameworks/Sparkle.framework/'):
            continue  # Exact vendor file and symlink inventory checked above.
        assert not path.is_symlink(), 'Unexpected bundle symlink'
        if path.is_dir():
            assert rel in allowed_dirs or rel.startswith(('Contents/Resources/', 'Contents/_CodeSignature')), 'Unreviewed nested directory'
        else:
            assert rel in {'Contents/Info.plist', 'Contents/PkgInfo', 'Contents/MacOS/Llumi'} or rel.startswith(('Contents/Resources/', 'Contents/_CodeSignature/')), 'Unexpected bundle file'
            assert path.suffix not in {'.p8', '.p12', '.key', '.log'}, 'Forbidden artifact'
            if rel.startswith('Contents/Resources/'):
                assert 'Mach-O' not in subprocess.check_output(['/usr/bin/file', '-b', str(path)], text=True), 'Unreviewed executable resource'
    for rel in files:
        path = framework / rel
        if 'Mach-O' in subprocess.check_output(['/usr/bin/file', '-b', str(path)], text=True):
            assert 'Contents/Frameworks/Sparkle.framework/' + rel in allowed_code, 'Unreviewed Sparkle executable'
    for rel in allowed_code:
        arches = set(subprocess.check_output(['/usr/bin/lipo', '-archs', str(app/rel)], text=True).split())
        assert arches == {'arm64', 'x86_64'}, f'Unexpected architectures: {rel}'
    print('Bundle:', info['LlumiReleaseVersion'], 'build', info['CFBundleVersion'], 'arm64 + x86_64; Intel runtime not physically validated')

if __name__ == '__main__': validate(sys.argv[1])
