#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CompilerPath,
      [string]$DotNetPath = 'dotnet.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/AgentMeter/AgentMeter.csproj'
$version = [string]([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must have three numeric components.' }
. "$PSScriptRoot/release-version.ps1"
Assert-NextWindowsPackageVersion -PackageVersion "$version.0"
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Use the signature-verified established Inno compiler.' }
$out = Join-Path $root ('artifacts/direct/' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$null = New-Item -ItemType Directory -Path $out
. "$PSScriptRoot/runtime-notices.ps1"
$artifacts = @()
foreach ($flavor in @('self-contained','framework-dependent')) {
    $destination = Join-Path $out $flavor
    $selfContained = if ($flavor -eq 'self-contained') { 'true' } else { 'false' }
    $build = Join-Path $out "build-$flavor"
    & $DotNetPath publish $project -c Release -r win-x64 --self-contained $selfContained --artifacts-path $build -o $destination --verbosity minimal '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishSingleFile=false'
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    $null = New-Item -ItemType Directory -Path "$destination/licenses" -Force
    Copy-Item "$root/packaging/licenses/*" "$destination/licenses"
    Copy-Item "$root/../LICENSE" "$destination/LICENSE.txt"
    Copy-Item "$root/../THIRD-PARTY-NOTICES.md" "$destination/THIRD-PARTY-NOTICES.txt"
    if ($selfContained -eq 'true') {
        $metadata = & $DotNetPath msbuild $project -nologo -target:ResolveFrameworkReferences -property:Configuration=Release -property:RuntimeIdentifier=win-x64 -property:SelfContained=true "-property:ArtifactsPath=$build" -getItem:ResolvedRuntimePack
        if ($LASTEXITCODE) { throw 'Runtime license resolution failed.' }
        $packs = (($metadata -join "`n") | ConvertFrom-Json).Items.ResolvedRuntimePack
        $frameworks = (Get-Content "$destination/Llumi.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
        Copy-RuntimeNotices -RuntimePacks $packs -Frameworks $frameworks -Destination $destination
    }
    & "$PSScriptRoot/assert-release.ps1" -Directory $destination
    & "$PSScriptRoot/test-v2-dependencies.ps1" -ReleaseDirectory $destination
    & "$PSScriptRoot/write-inventory.ps1" -Directory $destination
    & "$PSScriptRoot/test-inventory.ps1" -PackageDirectory $destination
    $archive = Join-Path $out "Llumi-$version-win-x64-$flavor.zip"
    Compress-Archive -Path "$destination/*" -DestinationPath $archive -CompressionLevel Optimal
    $artifacts += [ordered]@{flavor=$flavor;file=[IO.Path]::GetFileName($archive);bytes=(Get-Item $archive).Length;expandedBytes=(Get-ChildItem $destination -File -Recurse | Measure-Object Length -Sum).Sum;sha256=(Get-FileHash $archive).Hash.ToLowerInvariant()}
}
& $compiler '/Qp' "/DPayloadDir=$out/self-contained" "/DOutputDir=$out" "/DAppVersion=$version" "$root/installer/Llumi.iss"
if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
$installer = Join-Path $out "Llumi-$version-win-x64-Setup.exe"
$artifacts += [ordered]@{flavor='installer';file=[IO.Path]::GetFileName($installer);bytes=(Get-Item $installer).Length;sha256=(Get-FileHash $installer).Hash.ToLowerInvariant()}
[ordered]@{product='Llumi';version=$version;sourceCommit=(& git -C $root rev-parse HEAD).Trim();sourceWorkingTreeDirty=(@(& git -C $root status --porcelain).Count -ne 0);architecture='x64';signed=$false;published=$false;releaseReady=$false;artifacts=$artifacts} | ConvertTo-Json -Depth 6 | Set-Content "$out/receipt.json" -Encoding utf8
$artifacts | ForEach-Object { "$($_.sha256)  $($_.file)" } | Set-Content "$out/SHA256SUMS.txt" -Encoding ascii
Write-Output "DIRECT_CANDIDATE=$out"
