#requires -Version 7.0
[CmdletBinding()]
param([string]$PythonPath = 'python.exe')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $env:LOCALAPPDATA 'AgentMeter\dotnet\dotnet.exe'
$candidates = @((Get-Command dotnet.exe -CommandType Application -All -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)) + @($localSdk)
$sdk = $null
foreach ($candidate in ($candidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $versions = & $candidate --list-sdks
    if ($LASTEXITCODE -eq 0 -and @($versions | Where-Object { $_ -match '^10\.\d+\.\d+' }).Count -gt 0) { $sdk = $candidate; break }
}
if (-not $sdk) { throw 'Install the .NET 10 SDK on PATH.' }
function Invoke-DotNet {
    param([string[]]$Arguments)
    & $sdk @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}
Push-Location -LiteralPath $projectRoot
try {
    & $PythonPath (Join-Path $projectRoot '../scripts/check-windows-privacy.py')
    if ($LASTEXITCODE -ne 0) { throw 'Windows production privacy check failed.' }
    Invoke-DotNet @('restore', 'AgentMeter.sln', '--runtime', 'win-x64', '--verbosity', 'minimal')
    Invoke-DotNet @('build', 'AgentMeter.sln', '-c', 'Release', '--no-restore', '--verbosity', 'minimal')
    Invoke-DotNet @('test', 'tests/AgentMeter.Tests/AgentMeter.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--verbosity', 'minimal', '--results-directory', 'artifacts/test-results', '--blame-hang-timeout', '60s', '--blame-hang-dump-type', 'none')
    Invoke-DotNet @('publish', 'src/AgentMeter/AgentMeter.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '--artifacts-path', 'artifacts/publish-build', '-o', 'artifacts/release', '--verbosity', 'minimal', '-p:DebugType=None', '-p:DebugSymbols=false')
    $release = Join-Path $projectRoot 'artifacts/release'
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $release 'licenses')
    Copy-Item -Path (Join-Path $projectRoot 'packaging/licenses/*') -Destination (Join-Path $release 'licenses')
    Copy-Item -LiteralPath (Join-Path $projectRoot '../LICENSE') -Destination (Join-Path $release 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot '../THIRD-PARTY-NOTICES.md') -Destination (Join-Path $release 'THIRD-PARTY-NOTICES.txt')
    & "$PSScriptRoot/assert-release.ps1" -Directory $release
    & "$PSScriptRoot/test-package-notices.ps1"
    & "$PSScriptRoot/test-release-version.ps1"
    & "$PSScriptRoot/test-release-source.ps1" -ReleaseDirectory $release
    & "$PSScriptRoot/test-v2-dependencies.ps1" -ReleaseDirectory $release
    & "$PSScriptRoot/write-inventory.ps1" -Directory $release
    & "$PSScriptRoot/test-inventory.ps1" -PackageDirectory $release
    Write-Host 'PASS: local Windows development build and validation. No installer or public release produced.'
}
finally { Pop-Location }
