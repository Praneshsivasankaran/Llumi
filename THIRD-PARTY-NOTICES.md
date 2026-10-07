# Third-party materials

The MIT license covers Llumi's original source and documentation. It does not relicense dependencies, vendor software, trademarks, logos, or other third-party assets.

The macOS application bundles no provider executable or SDK. It invokes the user's separately installed provider tools, which retain their own terms. Provider artwork is identified in [the asset provenance](macos/AgentMeter/Resources/ProviderMarks.md); displaying it does not imply endorsement or grant trademark rights.

## Windows

The Windows application also invokes separately installed Codex and Claude Code.
No provider executable, Claude Agent SDK, `sdk.mjs`, Node runtime or third-party
JavaScript bridge is included in the application or this source tree.

Windows builds target .NET 10. Framework-dependent development output requires
the separately installed Microsoft .NET Desktop Runtime. If a future distribution
bundles Microsoft.NETCore.App or Microsoft.WindowsDesktop.App, the exact resolved
runtime packs' unmodified licenses and third-party notices must accompany it.
See the upstream [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
and [third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

The Windows SDK projection reference is pinned to Microsoft.Windows.SDK.NET.Ref
10.0.19041.57. Its Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll support
StartupTask and ApplicationData. Microsoft's [Windows SDK redistribution list](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)
lists these files subject to the SDK license terms. The unmodified
[Windows SDK license](windows/packaging/licenses/Microsoft.Windows.SDK-LICENSE.rtf)
and [C#/WinRT MIT license](windows/packaging/licenses/CsWinRT-LICENSE.txt)
are included for builds that copy those projections. The latter comes from
[C#/WinRT commit 8649ee3](https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE).
SDK compilers and analyzers are build-time tools, not application payload.

Tests reference Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3 and xunit.runner.visualstudio
3.1.5. Their transitive dependencies remain test-only and are not copied into
the application output. Their packages retain their upstream licenses:
[VSTest](https://github.com/microsoft/vstest/blob/main/LICENSE),
[xUnit](https://github.com/xunit/xunit/blob/main/LICENSE) and
[Visual Studio runner](https://github.com/xunit/visualstudio.xunit/blob/main/License.txt).

Windows uses the official OpenAI blossom from
[official OpenAI artwork](https://cdn.openai.com/brand/OpenAI-Logos-2025.zip) and
the Claude Code pixel mascot from Anthropic's [Claude Code extension](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code),
version 2.1.289, `extension/resources/clawd.svg`. Geometry is unchanged; the
OpenAI blossom follows the surface appearance and the mascot retains its original
color. Exact sources and hashes are recorded in the [Windows artwork provenance](docs/windows/PROVIDER-ARTWORK.md).
These marks remain vendor property under their respective artwork and trademark
terms, including [OpenAI's brand guidance](https://openai.com/brand/).
The Llumi Raspberry gauge is original Llumi artwork. No external font files are bundled.

The local build produces a per-file dependency inventory. That inventory and
these notices do not constitute approval of a future binary: review the exact
final artifact under the [distribution requirements](docs/DISTRIBUTION.md).
