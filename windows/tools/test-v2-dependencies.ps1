#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('AgentMeter-DependencyTests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
# Preserve notices when testing either framework-dependent or bundled-runtime output.
Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $root
if (Test-Path -LiteralPath (Join-Path $source 'licenses')) {
    Copy-Item -LiteralPath (Join-Path $source 'licenses') -Destination $root -Recurse
}
& (Join-Path $PSScriptRoot 'assert-release.ps1') -Directory $root
$passed = 0
foreach ($name in @('sdk.mjs', 'node.exe', 'claude.exe', 'codex.exe', 'ClaudeBridge', 'node_modules', 'claude-agent-sdk')) {
    $fixture = Join-Path $root $name
    [IO.File]::WriteAllText($fixture, 'Synthetic forbidden dependency fixture')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'assert-release.ps1') -Directory $root }
    catch {
        if ($_.Exception.Message -notlike 'V2 must not redistribute*') { throw }
        $rejected = $true
    }
    finally { Remove-Item -LiteralPath $fixture -Force }
    if (-not $rejected) { throw "Dependency guard accepted $name" }
    $passed++
}
Write-Host "V2 dependency rejection cases: $passed passed. Fixture: $root"
