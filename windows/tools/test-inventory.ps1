#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('AgentMeter-InventoryTests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
foreach ($file in @('Llumi.dll','Llumi.runtimeconfig.json','LICENSE.txt','THIRD-PARTY-NOTICES.txt')) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination $root }
foreach ($projection in @('Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll')) { if (Test-Path (Join-Path $source $projection)) { Copy-Item -LiteralPath (Join-Path $source $projection) -Destination $root } }
if (Test-Path -LiteralPath (Join-Path $source 'licenses')) { Copy-Item -LiteralPath (Join-Path $source 'licenses') -Destination $root -Recurse }
& "$PSScriptRoot/write-inventory.ps1" -Directory $root
$inventory = Get-Content -LiteralPath (Join-Path $root 'dependency-inventory.json') -Raw | ConvertFrom-Json
if ($inventory.schemaVersion -ne 2) { throw 'Wrong schema' }
if (@($inventory.components | Where-Object { $_.bundled -and -not $_.noticePresent }).Count) { throw 'Missing notices' }
if (@($inventory.components | Where-Object { $_.component -in @('Codex CLI','Claude Code','Claude Agent SDK','Node.js') -and $_.bundled }).Count) { throw 'Provider payload claimed bundled' }
$codexArtwork = @($inventory.components | Where-Object component -eq 'OpenAI Codex identification artwork')
if ($codexArtwork.Count -ne 1 -or $codexArtwork[0].version -ne 'OpenAI-Logos-2025; OpenAI-white-monoblossom.svg' -or
    $codexArtwork[0].origin -ne 'https://cdn.openai.com/brand/OpenAI-Logos-2025.zip' -or -not $codexArtwork[0].bundled) { throw 'Wrong Codex artwork provenance' }
$claudeArtwork = @($inventory.components | Where-Object component -eq 'Claude Code identification artwork')
if ($claudeArtwork.Count -ne 1 -or $claudeArtwork[0].version -ne 'Claude Code VS Code extension 2.1.289; extension/resources/clawd.svg' -or
    $claudeArtwork[0].origin -ne 'https://anthropic.gallerycdn.vsassets.io/extensions/anthropic/claude-code/2.1.289/1791068913543/Microsoft.VisualStudio.Services.VSIXPackage' -or -not $claudeArtwork[0].bundled) { throw 'Wrong Claude Code artwork provenance' }
$legacyArtwork = @($inventory.components | Where-Object component -eq 'Legacy Claude sunburst artwork (unused)')
if ($legacyArtwork.Count -ne 1 -or $legacyArtwork[0].origin -ne 'https://claude.ai/favicon.svg' -or -not $legacyArtwork[0].bundled) { throw 'Missing retained Claude artwork provenance' }
foreach ($file in $inventory.files) { if ((Get-FileHash -LiteralPath (Join-Path $root $file.path)).Hash -ne $file.sha256) { throw 'Inventory digest mismatch' } }
# A missing MIT notice must fail inventory generation, not merely record success.
Remove-Item -LiteralPath (Join-Path $root 'LICENSE.txt')
$failed = $false
try { & "$PSScriptRoot/write-inventory.ps1" -Directory $root }
catch { if ($_.Exception.Message -ne 'A redistributed component is missing its required notice.') { throw }; $failed = $true }
if (-not $failed) { throw 'Missing MIT notice was accepted' }
Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination $root
$checks = 8
foreach ($notice in @('Microsoft.Windows.SDK-LICENSE.rtf','CsWinRT-LICENSE.txt')) {
    $file = Join-Path $root "licenses/$notice"
    if (-not (Test-Path $file)) { continue }
    Remove-Item -LiteralPath $file
    $failed = $false
    try { & "$PSScriptRoot/write-inventory.ps1" -Directory $root } catch { if ($_.Exception.Message -ne 'A redistributed component is missing its required notice.') { throw }; $failed = $true }
    if (-not $failed) { throw "Missing projection notice accepted: $notice" }
    Copy-Item -LiteralPath (Join-Path $source "licenses/$notice") -Destination $file
    $checks++
}
Write-Host "Inventory checks: $checks passed."
