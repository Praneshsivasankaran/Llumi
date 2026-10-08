#requires -Version 7.0
# Shared by all Windows package producers. The published baseline is immutable.
function Assert-NextWindowsPackageVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$PackageVersion,
          [string]$BaselinePath = (Join-Path $PSScriptRoot '../../docs/releases/launch-baselines.json'))
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $floor = [string]$baseline.windows.package_version
    if ($floor -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Invalid frozen Windows package baseline.' }
    if ($PackageVersion -notmatch '^\d+\.\d+\.\d+\.0$' -or
        [version]$PackageVersion -le [version]$floor) {
        throw "Windows package version must advance beyond frozen $floor with revision 0."
    }
}
