#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $ScratchDirectory) { throw 'Use a new scratch directory for package tests.' }
$root = [IO.Path]::GetFullPath($ScratchDirectory)
$first = Join-Path $root 'first'
$second = Join-Path $root 'second'
New-Item -ItemType Directory -Path $first,$second | Out-Null
$merge = Join-Path $PSScriptRoot 'Merge-PublishOutputs.ps1'
$runtimeVersion = '10.0.13'
$version = '0.0.0-test'
function Assert-Rejected {
    param([scriptblock]$Action, [string]$ExpectedText, [string]$Destination)
    $rejected = $false
    try { & $Action }
    catch {
        if (!$_.ToString().Contains($ExpectedText, [StringComparison]::Ordinal)) { throw }
        $rejected = $true
    }
    if (!$rejected) { throw "Expected rejection: $ExpectedText" }
    if (Test-Path -LiteralPath $Destination) { throw "Rejected package created output: $Destination" }
    Write-Output "PASS rejection: $ExpectedText"
}
[IO.File]::WriteAllText((Join-Path $first 'shared.dll'), 'first version')
[IO.File]::WriteAllText((Join-Path $second 'shared.dll'), 'second version')
$conflict = Join-Path $root 'conflict'
Assert-Rejected -ExpectedText 'Conflicting package file' -Destination $conflict -Action {
    & $merge -SourceDirectories $first,$second -DestinationDirectory $conflict -Version $version -RuntimeVersion $runtimeVersion
}
[IO.File]::WriteAllText((Join-Path $second 'shared.dll'), 'first version')
$runtimeConfig = Join-Path $first 'test.runtimeconfig.json'
[IO.File]::WriteAllText($runtimeConfig, '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.13"}}}')
$dependent = Join-Path $root 'dependent'
Assert-Rejected -ExpectedText 'Application must bundle its runtime' -Destination $dependent -Action {
    & $merge -SourceDirectories $first,$second -DestinationDirectory $dependent -Version $version -RuntimeVersion $runtimeVersion
}
[IO.File]::WriteAllText($runtimeConfig, '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}')
$wrongRuntime = Join-Path $root 'wrong-runtime'
Assert-Rejected -ExpectedText 'Unexpected runtime' -Destination $wrongRuntime -Action {
    & $merge -SourceDirectories $first,$second -DestinationDirectory $wrongRuntime -Version $version -RuntimeVersion $runtimeVersion
}
[IO.File]::WriteAllText($runtimeConfig, '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.13"}]}}')
$valid = Join-Path $root 'valid'
& $merge -SourceDirectories $first,$second -DestinationDirectory $valid -Version $version -RuntimeVersion $runtimeVersion
$manifest = Get-Content -LiteralPath (Join-Path $valid 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Files.Count -ne 2) { throw 'Identical shared files were not deduplicated.' }
if ((Get-FileHash -LiteralPath (Join-Path $valid 'shared.dll')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $first 'shared.dll')).Hash) { throw 'Shared file changed during assembly.' }
Write-Output 'PASS identical shared files, bundled runtime and manifest'
