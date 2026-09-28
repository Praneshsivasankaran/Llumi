# Build Llumi for Windows

Requirements: Windows 10 22H2 (build 19045) or later (x64), the .NET 10 SDK and PowerShell 7. No Node runtime, provider SDK, provider account or signing certificate is required to build and run the synthetic tests.

From the repository root:

```powershell
pwsh -NoProfile -File windows/tools/build.ps1
```

The script restores dependencies, builds Release, runs the .NET tests, publishes an unsigned framework-dependent development build and checks its contents, notices and dependency inventory. Output stays under ignored `windows/artifacts/`. It does not create an installer, modify an installed package, sign, upload or publish anything.

To launch your local build:

```powershell
& ./windows/artifacts/release/Llumi.exe
```

The .NET 10 Desktop Runtime (x64) must be installed to run this framework-dependent output; the SDK includes it. Real usage also requires separately installed and authenticated Codex CLI and standalone Claude Code. Sign in using those tools. Llumi does not install providers or accept API keys.

For individual development steps:

```powershell
dotnet restore windows/AgentMeter.sln --runtime win-x64
dotnet build windows/AgentMeter.sln -c Release --no-restore
dotnet test windows/tests/AgentMeter.Tests/AgentMeter.Tests.csproj -c Release --no-build --no-restore
python scripts/check-public-tree.py
```

Tests include native Windows UI, parsing, authentication continuity, subprocess isolation, timeouts, cleanup, activity, stale states and lifecycle behavior. They use synthetic data and do not establish real-provider or clean-machine acceptance. CI runs the same build script without authentication or signing secrets and does not upload Windows executable artifacts.

Local unpackaged preferences and sanitized rotating logs use `%LOCALAPPDATA%\Llumi`. Store builds use package-local storage and Windows StartupTask; an unpackaged development run cannot verify packaged startup or installation behavior. See [privacy](../../PRIVACY.md).

The repository deliberately excludes Store submission preparation and signing material. A direct GitHub release needs a separately reviewed final artifact; see [distribution requirements](../DISTRIBUTION.md).
