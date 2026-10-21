#Requires -Version 7.0
<#
Combines separately published applications only when every shared file is identical.
All validation happens before the destination is created; existing destinations
are rejected so a failed or older publish cannot leave stale files in a package.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$SourceDirectories,
    [Parameter(Mandatory)][string]$DestinationDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$RuntimeVersion
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$destinationRoot = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destinationRoot) { throw "Package destination already exists: $destinationRoot" }
$files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($source in $SourceDirectories) {
    $sourceRoot = (Get-Item -LiteralPath $source).FullName
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
        $destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative))
        if (!$destination.StartsWith($destinationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Invalid package destination: $relative"
        }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ($files.ContainsKey($relative) -and $files[$relative].Hash -ne $hash) {
            throw "Conflicting package file '$relative': '$($files[$relative].Source)' and '$($file.FullName)'. Align the dependencies; publishing order must not choose a winner."
        }
        if ($file.Name.EndsWith('.runtimeconfig.json', [StringComparison]::OrdinalIgnoreCase)) {
            $configuration = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
            $runtime = $configuration.runtimeOptions
            if ($runtime.ContainsKey('framework') -or $runtime.ContainsKey('frameworks') -or !$runtime.ContainsKey('includedFrameworks')) {
                throw "Application must bundle its runtime: $relative"
            }
            foreach ($framework in $runtime.includedFrameworks) {
                if ($framework.version -ne $RuntimeVersion) { throw "Unexpected runtime $($framework.version) in $relative; expected $RuntimeVersion" }
            }
        }
        $files[$relative] = [pscustomobject]@{ Source = $file.FullName; Hash = $hash; Length = $file.Length }
    }
}
if ($files.Count -eq 0) { throw 'No published files were found.' }
New-Item -ItemType Directory -Path $destinationRoot | Out-Null
$manifest = foreach ($relative in ($files.Keys | Sort-Object)) {
    $destination = Join-Path $destinationRoot $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $files[$relative].Source -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $files[$relative].Hash) { throw "Package copy verification failed: $relative" }
    [pscustomobject]@{ Path = $relative; Sha256 = $files[$relative].Hash; Length = $files[$relative].Length }
}
[ordered]@{ Version = $Version; RuntimeVersion = $RuntimeVersion; Files = @($manifest) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationRoot 'package-manifest.json')
Write-Output "Verified $($files.Count) package files in $destinationRoot"
