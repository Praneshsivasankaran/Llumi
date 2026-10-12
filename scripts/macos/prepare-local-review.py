#!/usr/bin/env python3
"""Prepare an already built app for local review, without release configuration edits."""
import plistlib
import subprocess
import sys
from pathlib import Path

app = Path(sys.argv[1]).resolve()
info = app / "Contents/Info.plist"
data = plistlib.loads(info.read_bytes())
if data.get("CFBundleIdentifier") != "io.github.praneshsivasankaran.llumi.review":
    raise SystemExit("Refusing to prepare a non-review bundle")
data["LlumiLocalReview"] = True
data["CFBundleName"] = "Llumi Review"
data["CFBundleDisplayName"] = "Llumi Review"
data["SUEnableAutomaticChecks"] = False
data["SUAutomaticallyUpdate"] = False
data["SUAllowsAutomaticUpdates"] = False
data.pop("SUFeedURL", None)
info.write_bytes(plistlib.dumps(data, sort_keys=False))
# Only an ad-hoc signature on this isolated output; no signing identity or credential.
subprocess.run(["/usr/bin/codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
print("Local review app prepared: updater disabled, separate preferences, ad-hoc signature.")
