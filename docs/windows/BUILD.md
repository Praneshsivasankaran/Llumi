# Build Llumi for Windows

Requirements: Windows 10 22H2 (build 19045) or later (x64), the .NET 10 SDK, PowerShell 7 and Python 3 for the standard-library privacy check. No Node runtime, provider SDK, provider account or signing certificate is required to build and run the synthetic tests.

From the repository root:

```powershell
pwsh -NoProfile -File windows/tools/build.ps1
```

The script runs the Windows production privacy check, restores dependencies, builds Release, runs the .NET tests, publishes an unsigned framework-dependent development build and checks its contents, notices and dependency inventory. Use `-PythonPath <python.exe>` when Python is outside PATH. Output stays under ignored `windows/artifacts/`. It does not create an installer, modify an installed package, sign, upload or publish anything.

The test runner aborts the test process if a single test hangs for sixty seconds; it does not collect memory dumps. The multi-page setup appearance fixture checks its deadline between UI stages and awaits its worker through cleanup, so a timed-out worker cannot change shared appearance state during the next test.

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

The production executable accepts normal launch, `--startup` and `--quit`. The former developer `--probe` export is removed; unsupported arguments exit before application or provider activity. Use the separate development ReviewHost for isolated local review. It does not add a production usage-export command.

The repository deliberately excludes Store submission preparation and signing material. A direct GitHub release needs a separately reviewed final artifact; see [distribution requirements](../DISTRIBUTION.md).
