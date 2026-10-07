#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$config = Get-Content -LiteralPath (Join-Path $root 'Llumi.runtimeconfig.json') -Raw | ConvertFrom-Json
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'Llumi.dll')).ProductVersion
$components = @()
function Component($name, $version, $origin, $bundled, $license, $notices) {
    [ordered]@{component=$name;version=$version;origin=$origin;bundled=$bundled;license=$license;requiredNotice=@($notices);noticePresent=(@($notices | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) }).Count -eq 0)}
}
$components += Component 'Llumi application and original artwork' $version 'Committed local AgentMeter source' $true 'MIT' @('LICENSE.txt')
$components += Component 'OpenAI Codex identification artwork' 'OpenAI-Logos-2025; OpenAI-white-monoblossom.svg' 'https://cdn.openai.com/brand/OpenAI-Logos-2025.zip' $true 'Vendor artwork/trademark terms; not MIT' @('THIRD-PARTY-NOTICES.txt')
$components += Component 'Claude Code identification artwork' 'Claude Code VS Code extension 2.1.289; extension/resources/clawd.svg' 'https://anthropic.gallerycdn.vsassets.io/extensions/anthropic/claude-code/2.1.289/1791068913543/Microsoft.VisualStudio.Services.VSIXPackage' $true 'Vendor artwork/trademark terms; not MIT' @('THIRD-PARTY-NOTICES.txt')
# The old sunburst remains embedded through Assets/*.svg, although the renderer
# selects the Code mascot. Inventory all bundled artwork, including that asset.
$components += Component 'Legacy Claude sunburst artwork (unused)' 'Claude favicon; ClaudeLogo.svg' 'https://claude.ai/favicon.svg' $true 'Vendor artwork/trademark terms; not MIT' @('THIRD-PARTY-NOTICES.txt')
foreach ($framework in @($config.runtimeOptions.includedFrameworks)) {
    if ($null -eq $framework) { continue }
    $notices = @("licenses/$($framework.name)-LICENSE.txt")
    if ($framework.name -eq 'Microsoft.NETCore.App' -or (Test-Path -LiteralPath (Join-Path $root "licenses/$($framework.name)-THIRD-PARTY-NOTICES.txt"))) { $notices += "licenses/$($framework.name)-THIRD-PARTY-NOTICES.txt" }
    $components += Component $framework.name $framework.version 'Microsoft resolved win-x64 NuGet runtime pack' $true 'MIT and constituent licenses listed in vendor notices' $notices
}
foreach ($projection in @('Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll')) {
    $binary = Join-Path $root $projection
    if (Test-Path -LiteralPath $binary) {
        $notices = @('licenses/Microsoft.Windows.SDK-LICENSE.rtf')
        $license = 'Microsoft Windows SDK license; SDK.NET.Ref explicitly listed in Microsoft redist list'
        if ($projection -eq 'WinRT.Runtime.dll') { $notices += 'licenses/CsWinRT-LICENSE.txt'; $license += '; C#/WinRT MIT' }
        $components += Component $projection ([Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion) 'Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 (unmodified)' $true $license $notices
    }
}
foreach ($external in @('Codex CLI','Claude Code','Claude Agent SDK','Node.js')) {
    $components += Component $external 'Not redistributed' 'User installation or excluded historical development dependency' $false 'Not applicable to this artifact' @()
}
if (@($components | Where-Object { $_.bundled -and -not $_.noticePresent }).Count) { throw 'A redistributed component is missing its required notice.' }
$inventory = [ordered]@{
    schemaVersion = 2
    product = 'Llumi Windows'
    providerExecutablesBundled = $false
    providerSdkBundled = $false
    nodeBundled = $false
    runtime = $config.runtimeOptions
    components = $components
    files = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object Name -ne 'dependency-inventory.json' | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($root.Length + 1).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'dependency-inventory.json') -Encoding UTF8
