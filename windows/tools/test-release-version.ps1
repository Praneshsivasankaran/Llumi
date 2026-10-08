#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-version.ps1"
$passed = 0
function Expect-Rejection {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Expected version rejection.' }
    $script:passed++
}
foreach ($version in @('0.0.0.0','1.9.9.0','2.0.1.0','2.0.2.0','2.0.3.1','2.0.3','invalid')) {
    Expect-Rejection { Assert-NextWindowsPackageVersion $version } 'must advance beyond frozen'
}
Assert-NextWindowsPackageVersion '2.0.3.0'; $passed++
Assert-NextWindowsPackageVersion '2.1.3.0'; $passed++
# A synthetic sibling checkout exercises the actual producers without touching
# source versions, historical artifacts, SDK tools, or producing any package.
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('llumi-version-test-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path "$temporary/windows/tools","$temporary/windows/src/AgentMeter","$temporary/docs/releases"
try {
    Copy-Item "$PSScriptRoot/release-version.ps1","$PSScriptRoot/prepare-store.ps1","$PSScriptRoot/package-direct.ps1" "$temporary/windows/tools"
    Copy-Item "$PSScriptRoot/../../docs/releases/launch-baselines.json" "$temporary/docs/releases"
    foreach ($version in @('2.0.1','2.0.2')) {
        "<Project><PropertyGroup><Version>$version</Version><FileVersion>$version.0</FileVersion></PropertyGroup></Project>" | Set-Content "$temporary/windows/src/AgentMeter/AgentMeter.csproj"
        Expect-Rejection { & "$temporary/windows/tools/prepare-store.ps1" -HistoricalPackage missing.msix -PayloadDirectory missing -SdkBinDirectory missing } 'must advance beyond frozen'
        Expect-Rejection { & "$temporary/windows/tools/package-direct.ps1" -CompilerPath missing.exe } 'must advance beyond frozen'
    }
    # The floor follows the authoritative record, including a later release.
    '{"windows":{"package_version":"2.0.3.0"}}' | Set-Content "$temporary/docs/releases/launch-baselines.json"
    Expect-Rejection { Assert-NextWindowsPackageVersion '2.0.3.0' -BaselinePath "$temporary/docs/releases/launch-baselines.json" } 'must advance beyond frozen'
    Assert-NextWindowsPackageVersion '2.0.4.0' -BaselinePath "$temporary/docs/releases/launch-baselines.json"; $passed++
    '{"windows":{}}' | Set-Content "$temporary/docs/releases/launch-baselines.json"
    Expect-Rejection { Assert-NextWindowsPackageVersion '2.0.4.0' -BaselinePath "$temporary/docs/releases/launch-baselines.json" } 'Invalid frozen'
    Expect-Rejection { Assert-NextWindowsPackageVersion '2.0.4.0' -BaselinePath "$temporary/missing.json" } 'does not exist'
    Write-Host "PASS: Windows release version guard ($passed cases)."
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'llumi-version-test-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
