#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-source.ps1"
$passed = 0
function Expect-SourceRejection {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Expected source provenance rejection.' }
    $script:passed++
}
function Invoke-FixtureGit {
    param([string[]]$Arguments)
    & git -C $temporary @Arguments 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git fixture operation failed.' }
}
$commit = 'a' * 40
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('llumi-source-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path "$temporary/windows/src/AgentMeter","$temporary/windows/src/AgentMeter.Core","$temporary/windows/tools","$temporary/docs/releases","$temporary/payload"
try {
    Copy-Item "$PSScriptRoot/prepare-store.ps1","$PSScriptRoot/release-source.ps1","$PSScriptRoot/release-version.ps1" "$temporary/windows/tools"
    Copy-Item "$PSScriptRoot/../../docs/releases/launch-baselines.json" "$temporary/docs/releases"
    foreach ($name in @('AgentMeter','AgentMeter.Core')) {
        $sourceProject = Join-Path $PSScriptRoot "../src/$name/$name.csproj"
        Copy-Item -LiteralPath $sourceProject -Destination "$temporary/windows/src/$name/$name.csproj"
        $expected = Get-ReleaseProjectIdentity -ProjectPath $sourceProject
        $valid = @{Name=$expected.Name;AssemblyVersion=$expected.AssemblyVersion;FileVersion=$expected.FileVersion;InformationalVersion="$($expected.Version)+$commit"}
        Assert-ReleaseBinaryIdentity -Identity ([pscustomobject]$valid) -Expected $expected -SourceCommit $commit; $passed++
        foreach ($case in @(@('Name','WrongAssembly'), @('AssemblyVersion','0.0.0.0'), @('FileVersion','0.0.0.0'),
            @('InformationalVersion',$expected.Version), @('InformationalVersion',"$($expected.Version)+$('b' * 40)"),
            @('InformationalVersion',"$($expected.Version)+$commit-dirty"))) {
            $invalid = $valid.Clone(); $invalid[$case[0]] = $case[1]
            Expect-SourceRejection { Assert-ReleaseBinaryIdentity ([pscustomobject]$invalid) $expected $commit } 'does not match'
        }
        Expect-SourceRejection { Assert-ReleaseBinaryIdentity ([pscustomobject]$valid) $expected 'abcdef0' } 'does not match'
        # Read genuine built assemblies without loading/executing their code.
        $binary = Join-Path $ReleaseDirectory "$($expected.Name).dll"
        $actual = Get-ReleaseBinaryIdentity -Path $binary
        if ($actual.InformationalVersion -notmatch '\+([0-9a-f]{40})$') { throw 'Built fixture lacks a full source commit.' }
        Assert-ReleaseBinaryIdentity -Identity $actual -Expected $expected -SourceCommit $Matches[1]; $passed++
        Copy-Item -LiteralPath $binary -Destination "$temporary/payload/$($expected.Name).dll"
    }
    'payload/' | Set-Content -LiteralPath "$temporary/.gitignore"
    Invoke-FixtureGit @('init','--quiet')
    Invoke-FixtureGit @('add','.gitignore','windows','docs')
    # Synthetic repository only; never change the real repository's identity.
    Invoke-FixtureGit @('-c','user.name=Llumi Synthetic Fixture','-c','user.email=fixture@example.invalid','-c','commit.gpgSign=false','commit','--quiet','-m','Synthetic source identity fixture')
    $fixtureCommit = Get-CleanReleaseSourceCommit -RepositoryDirectory $temporary
    if ($fixtureCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Missing fixture commit.' }; $passed++
    # The actual producer validator must reject a payload from another commit.
    Expect-SourceRejection { Assert-StorePayloadSource -RepositoryDirectory $temporary -PayloadDirectory "$temporary/payload" } 'does not match'
    # Exercise the package entry point: source rejection precedes any historical
    # artifact read, SDK invocation, or package output creation.
    Expect-SourceRejection { & "$temporary/windows/tools/prepare-store.ps1" -HistoricalPackage missing.msix -PayloadDirectory "$temporary/payload" -SdkBinDirectory missing } 'does not match'
    'Synthetic untracked source' | Set-Content -LiteralPath "$temporary/untracked.txt"
    Expect-SourceRejection { Get-CleanReleaseSourceCommit $temporary } 'clean source checkout'
    Remove-Item -LiteralPath "$temporary/untracked.txt"
    Add-Content -LiteralPath "$temporary/windows/src/AgentMeter/AgentMeter.csproj" -Value '<!-- Synthetic modification -->'
    Expect-SourceRejection { Get-CleanReleaseSourceCommit $temporary } 'clean source checkout'
    Invoke-FixtureGit @('add','windows')
    Expect-SourceRejection { Get-CleanReleaseSourceCommit $temporary } 'clean source checkout'
    # A core assembly renamed to Llumi.dll cannot pass the metadata reader.
    Copy-Item -LiteralPath "$temporary/payload/AgentMeter.Core.dll" -Destination "$temporary/payload/Llumi.dll" -Force
    $renamed = Get-ReleaseBinaryIdentity -Path "$temporary/payload/Llumi.dll"
    $appIdentity = Get-ReleaseProjectIdentity -ProjectPath "$temporary/windows/src/AgentMeter/AgentMeter.csproj"
    Expect-SourceRejection { Assert-ReleaseBinaryIdentity $renamed $appIdentity $commit } 'does not match'
    $projectFixture = "$temporary/conditional.csproj"
    '<Project><PropertyGroup Condition="true"><AssemblyName>Llumi</AssemblyName></PropertyGroup></Project>' | Set-Content -LiteralPath $projectFixture
    Expect-SourceRejection { Get-ReleaseProjectIdentity $projectFixture } 'Ambiguous release identity'
    '<Project><PropertyGroup><AssemblyName>Llumi</AssemblyName><InformationalVersion>forged</InformationalVersion></PropertyGroup></Project>' | Set-Content -LiteralPath $projectFixture
    Expect-SourceRejection { Get-ReleaseProjectIdentity $projectFixture } 'explicit packaging contract'
    Write-Host "PASS: Windows release source provenance guard ($passed cases)."
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'llumi-source-test-*') { throw 'Unsafe source test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
