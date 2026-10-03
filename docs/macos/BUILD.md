# Build Llumi for macOS

Use Xcode 27 with Swift 6.4 (the currently verified toolchain). The deployment target is macOS 14. Release builds contain arm64 and x86_64; Intel runtime behavior has not been physically tested. Earlier Xcode versions are not yet verified.

```sh
git clone https://github.com/Praneshsivasankaran/Llumi.git
cd Llumi
xcodebuild -project macos/AgentMeter.xcodeproj -scheme AgentMeter -configuration Release -derivedDataPath "$HOME/Library/Developer/Xcode/DerivedData/AgentMeter" -destination 'platform=macOS' clean build
xcodebuild -project macos/AgentMeter.xcodeproj -scheme AgentMeter -configuration Debug -derivedDataPath "$HOME/Library/Developer/Xcode/DerivedData/AgentMeter" -destination 'platform=macOS' test
python3 macos/Scripts/privacy-check.py "$HOME/Library/Developer/Xcode/DerivedData/AgentMeter/Build/Products/Release/Llumi.app"
open "$HOME/Library/Developer/Xcode/DerivedData/AgentMeter/Build/Products/Release/Llumi.app"
```

Release packaging removes debugging symbols from the app executable. Keep generated dSYM bundles local; do not add them to the beta ZIP. The privacy check inspects all binary bytes for development-home paths.

Build output belongs under Library, outside protected document folders. No provider login, provider installation, signing certificate, or third-party package installation is needed to compile or run synthetic tests. Real usage appears only when supported tools are installed and signed in.

The frozen release is 1.1.1 (build 1). A local source build does not reproduce the signing/notarization receipt or authorize replacement of published bytes. Use a new version for future release work. For a local unsigned source-build archive after validation:

```sh
mkdir -p "$HOME/Library/Caches/LlumiCandidate"
ditto -c -k --norsrc --noextattr --keepParent "$HOME/Library/Developer/Xcode/DerivedData/AgentMeter/Build/Products/Release/Llumi.app" "$HOME/Library/Caches/LlumiCandidate/Llumi-1.1.2-unsigned-source-build.zip"
shasum -a 256 "$HOME/Library/Caches/LlumiCandidate/Llumi-1.1.2-unsigned-source-build.zip"
```

This creates a local ad-hoc build, not Developer ID signing or notarization. Builds are source-reproducible; byte-identical archives are not promised. The macOS workflow uses GitHub's `xcode-27` preview runner with synthetic tests and no provider authentication or signing secrets. Preview runner availability may vary; no passing badge is advertised until a real run completes.

For an unsigned DMG rehearsal and the future signing/notarization steps, see [Distribution](DISTRIBUTION.md).

The active candidate is 1.1.2/build 2 with pinned Sparkle 2.10.0. Xcode resolves the official binary dependency and embeds its framework; no signing occurs in unsigned source builds. See [updater candidate and remaining gates](UPDATES.md).
