#requires -Version 7.0
# Metadata-only provenance checks. Never execute an assembly supplied as a payload.
function Get-CleanReleaseSourceCommit {
    param([Parameter(Mandatory=$true)][string]$RepositoryDirectory)
    $commit = @(& git -C $RepositoryDirectory rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $commit.Count -ne 1 -or $commit[0] -cnotmatch '^[0-9a-f]{40}$') {
        throw 'A committed Git source checkout is required for Store packaging.'
    }
    $status = @(& git -C $RepositoryDirectory status --porcelain --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) {
        throw 'Store packaging requires a clean source checkout, including untracked files.'
    }
    return [string]$commit[0]
}

function Get-ReleaseProjectIdentity {
    param([Parameter(Mandatory=$true)][string]$ProjectPath)
    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw -ErrorAction Stop
    # These two projects declare their identity locally; omitted fields use the
    # Microsoft.NET.Sdk defaults. Do not guess at conditional/custom versions.
    $properties = @{}
    foreach ($group in @($project.Project.PropertyGroup)) {
        foreach ($name in @('AssemblyName','Version','VersionPrefix','VersionSuffix','AssemblyVersion','FileVersion','InformationalVersion')) {
            foreach ($property in @($group.SelectNodes($name))) {
                if ($group.HasAttribute('Condition') -or $property.HasAttribute('Condition') -or $properties.ContainsKey($name)) {
                    throw 'Ambiguous release identity in project source.'
                }
                $properties[$name] = $property.InnerText
            }
        }
    }
    if ($properties.ContainsKey('VersionPrefix') -or $properties.ContainsKey('VersionSuffix') -or $properties.ContainsKey('InformationalVersion')) {
        throw 'Custom release identity requires an explicit packaging contract.'
    }
    $name = if ($properties.ContainsKey('AssemblyName')) { $properties.AssemblyName } else { [IO.Path]::GetFileNameWithoutExtension($ProjectPath) }
    $version = if ($properties.ContainsKey('Version')) { $properties.Version } else { '1.0.0' }
    $assembly = if ($properties.ContainsKey('AssemblyVersion')) { $properties.AssemblyVersion } else { "$version.0" }
    $file = if ($properties.ContainsKey('FileVersion')) { $properties.FileVersion } else { "$version.0" }
    if ($name -notin @('Llumi','AgentMeter.Core') -or $version -notmatch '^\d+\.\d+\.\d+$' -or
        $assembly -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $file -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'Invalid release identity in project source.'
    }
    return [pscustomobject]@{Name=$name;Version=$version;AssemblyVersion=$assembly;FileVersion=$file}
}

function Get-ReleaseBinaryIdentity {
    param([Parameter(Mandatory=$true)][string]$Path)
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($resolved)
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolved)
    return [pscustomobject]@{Name=$assembly.Name;AssemblyVersion=$assembly.Version.ToString();FileVersion=$version.FileVersion;InformationalVersion=$version.ProductVersion}
}

function Assert-ReleaseBinaryIdentity {
    param([Parameter(Mandatory=$true)][object]$Identity,
          [Parameter(Mandatory=$true)][object]$Expected,
          [Parameter(Mandatory=$true)][string]$SourceCommit)
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $Identity.Name -cne $Expected.Name -or
        $Identity.AssemblyVersion -cne $Expected.AssemblyVersion -or $Identity.FileVersion -cne $Expected.FileVersion -or
        $Identity.InformationalVersion -cne "$($Expected.Version)+$SourceCommit") {
        throw "Payload identity or source revision does not match the committed source: $($Expected.Name)."
    }
}

function Assert-StorePayloadSource {
    param([Parameter(Mandatory=$true)][string]$RepositoryDirectory,
          [Parameter(Mandatory=$true)][string]$PayloadDirectory)
    $commit = Get-CleanReleaseSourceCommit -RepositoryDirectory $RepositoryDirectory
    $assemblies = @()
    foreach ($name in @('AgentMeter','AgentMeter.Core')) {
        $project = Join-Path $RepositoryDirectory "windows/src/$name/$name.csproj"
        $expected = Get-ReleaseProjectIdentity -ProjectPath $project
        $requiredName = if ($name -ceq 'AgentMeter') { 'Llumi' } else { 'AgentMeter.Core' }
        if ($expected.Name -cne $requiredName) { throw 'Store payload assembly names must retain the application and core identities.' }
        $file = "$($expected.Name).dll"
        $path = Join-Path $PayloadDirectory $file
        $identity = Get-ReleaseBinaryIdentity -Path $path
        Assert-ReleaseBinaryIdentity -Identity $identity -Expected $expected -SourceCommit $commit
        $assemblies += [ordered]@{file=$file;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();assemblyName=$identity.Name;assemblyVersion=$identity.AssemblyVersion;fileVersion=$identity.FileVersion;informationalVersion=$identity.InformationalVersion}
    }
    return [pscustomobject]@{sourceCommit=$commit;assemblies=$assemblies}
}
