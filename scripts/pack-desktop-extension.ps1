# pack-desktop-extension.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

# Packs desktop-extension/ into saddlerag-<Version>.mcpb. Stages the source to a
# temp directory, stamps the build version into the manifest, then zips it. A
# .mcpb is a Claude Desktop Extension — a zip with manifest.json at the root —
# that bridges to the local SaddleRAG MCP server. See desktop-extension/README.md
# for why this exists. ConvertFrom-Json on the manifest doubles as validation.

[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputDir = (Join-Path $PSScriptRoot '..\artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExtensionDir = Resolve-Path (Join-Path $PSScriptRoot '..\desktop-extension')
$StagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "saddlerag-mcpb-$Version"
$ManifestFileName = 'manifest.json'
$JsonDepth = 20

# Fresh staging copy so the source tree's placeholder version is never mutated.
if (Test-Path $StagingDir)
{
    Remove-Item -Recurse -Force $StagingDir
}
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
Copy-Item -Recurse -Force -Path (Join-Path $ExtensionDir '*') -Destination $StagingDir

# README is repo documentation, not part of the shipped extension payload.
Get-ChildItem -Path $StagingDir -Filter '*.md' -File | Remove-Item -Force

# Stamp the real build version into the staged manifest.
$manifestPath = Join-Path $StagingDir $ManifestFileName
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $Version
$manifest | ConvertTo-Json -Depth $JsonDepth | Set-Content -LiteralPath $manifestPath -Encoding UTF8

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputPath = Join-Path $OutputDir "saddlerag-$Version.mcpb"
$ZipPath = Join-Path $OutputDir "saddlerag-$Version.zip"

# A .mcpb is a zip with manifest.json at the root. Compress the staged *contents*
# (the '*' glob) so manifest.json lands at the archive root rather than under a
# subfolder, then rename .zip -> .mcpb. Compress-Archive keeps the build free of
# any npx/network dependency.
foreach ($stale in $ZipPath, $OutputPath)
{
    if (Test-Path $stale)
    {
        Remove-Item -Force $stale
    }
}
Compress-Archive -Path (Join-Path $StagingDir '*') -DestinationPath $ZipPath -Force
Move-Item -LiteralPath $ZipPath -Destination $OutputPath -Force

Remove-Item -Recurse -Force $StagingDir
Write-Host "Packed Desktop extension: $OutputPath"
