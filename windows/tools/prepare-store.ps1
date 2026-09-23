#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$HistoricalPackage,
      [Parameter(Mandatory=$true)][string]$PayloadDirectory,
      [Parameter(Mandatory=$true)][string]$SdkBinDirectory)
$ErrorActionPreference = 'Stop'
$expected = '5cd7219626334f2312fbbebe7b5540dbd57b4df9a5ba8a586589a9d131486bcd'
if ((Get-FileHash -LiteralPath $HistoricalPackage).Hash -ne $expected) { throw 'Historical package hash mismatch.' }
$root = Split-Path -Parent $PSScriptRoot
$version = [string]([xml](Get-Content "$root/src/AgentMeter/AgentMeter.csproj" -Raw)).Project.PropertyGroup.FileVersion
if ([version]$version -le [version]'2.0.1.0' -or ([version]$version).Revision -ne 0) { throw 'Invalid next Store version.' }
foreach ($name in @('makeappx.exe','makepri.exe')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $SdkBinDirectory $name)
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Invalid SDK tool signature.' }
}
if (-not (Test-Path "$PayloadDirectory/coreclr.dll") -or -not (Test-Path "$PayloadDirectory/Llumi.exe")) { throw 'Self-contained Llumi payload required.' }
$out = Join-Path $root ('artifacts/store/' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$stage = Join-Path $out 'stage'
$null = New-Item -ItemType Directory -Path $stage
Copy-Item "$PayloadDirectory/*" $stage -Recurse
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $HistoricalPackage))
try {
    $reader = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $assetNames = @($zip.Entries | Where-Object FullName -like 'Assets/*.png' | Select-Object -ExpandProperty FullName)
} finally { $zip.Dispose() }
if ($manifest.Package.Identity.Name -ne 'PraneshS.AgentMeterforWindows' -or $manifest.Package.Identity.Publisher -ne 'CN=8971912E-75C4-4D4B-8B53-3814AB80FF0F' -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Unexpected historical identity.' }
$manifest.Package.Identity.Version = $version
$manifest.Package.Properties.DisplayName = 'Llumi'
$manifest.Package.Properties.Description = 'Track your AI coding usage.'
$app = $manifest.Package.Applications.Application
$app.Executable = 'Llumi.exe'
$app.VisualElements.DisplayName = 'Llumi'
$app.VisualElements.Description = 'Track your AI coding usage.'
$app.Extensions.Extension.Executable = 'Llumi.exe'
$app.Extensions.Extension.StartupTask.DisplayName = 'Llumi'
$manifest.Save("$stage/AppxManifest.xml")
# Recreate precisely the historical resource names/scales from approved Raspberry artwork.
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Path "$stage/Assets"
$art = [Drawing.Image]::FromFile((Resolve-Path "$root/../assets/brand/llumi-dark.png"))
try {
    foreach ($name in $assetNames) {
        $base = if ($name -match 'Square44') {44} elseif ($name -match 'Square150') {150} else {50}
        $size = if ($name -match 'targetsize-(\d+)') {[int]$Matches[1]} elseif ($name -match 'scale-(\d+)') {[int][Math]::Floor($base * [int]$Matches[1] / 100)} else {$base}
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.InterpolationMode = 'HighQualityBicubic'; $graphics.DrawImage($art,0,0,$size,$size); $bitmap.Save((Join-Path $stage $name),[Drawing.Imaging.ImageFormat]::Png) }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $art.Dispose() }
& "$SdkBinDirectory/makepri.exe" createconfig /cf "$out/priconfig.xml" /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE) { throw 'PRI configuration failed.' }
[xml]$config = Get-Content "$out/priconfig.xml" -Raw
$config.SelectSingleNode('//packaging').RemoveAll(); $config.Save("$out/priconfig.xml")
& "$SdkBinDirectory/makepri.exe" new /pr $stage /cf "$out/priconfig.xml" /of "$stage/resources.pri" /o | Out-Null
if ($LASTEXITCODE) { throw 'PRI indexing failed.' }
& "$PSScriptRoot/assert-release.ps1" -Directory $stage
& "$PSScriptRoot/write-inventory.ps1" -Directory $stage
& "$PSScriptRoot/test-inventory.ps1" -PackageDirectory $stage
$package = Join-Path $out "Llumi-$version-x64.msix"
& "$SdkBinDirectory/makeappx.exe" pack /d $stage /p $package /h SHA256 | Out-File "$out/makeappx.txt"
if ($LASTEXITCODE) { throw 'MSIX packing failed.' }
if ((Get-FileHash -LiteralPath $HistoricalPackage).Hash -ne $expected) { throw 'Historical artifact changed.' }
[ordered]@{version=$version;identity=$manifest.Package.Identity.Name;storeId='9NV153Q5K5MQ';historicalSha256=$expected;sha256=(Get-FileHash $package).Hash.ToLowerInvariant();sourceCommit=(& git -C $root rev-parse HEAD).Trim();signed=$false;submitted=$false;published=$false} | ConvertTo-Json | Set-Content "$out/receipt.json"
Write-Output "STORE_CANDIDATE=$package"
