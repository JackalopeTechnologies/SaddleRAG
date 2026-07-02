# ConfigureCrashCapture.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

# Configures Windows Error Reporting LocalDumps for SaddleRAG.Mcp.exe so a
# native crash (e.g. an access violation inside the ONNX runtime, issue #135)
# leaves a full memory dump for post-mortem analysis instead of only an event
# log stack (issue #136). Idempotent: safe to run on every install/upgrade.
# Must run elevated (the MSI runs it as a deferred, non-impersonated CA).

[CmdletBinding()]
param(
    [string]$ExecutableName = 'SaddleRAG.Mcp.exe',
    [string]$DumpFolder = (Join-Path $env:ProgramData 'SaddleRAG\CrashDumps'),
    [int]$DumpCount = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# DumpType 2 = full dump. Full dumps are required to inspect native ONNX
# state after an access violation; disk cost is bounded by DumpCount.
$fullDumpType = 2

$localDumpsKey = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$ExecutableName"

New-Item -ItemType Directory -Force -Path $DumpFolder | Out-Null
New-Item -Path $localDumpsKey -Force | Out-Null
New-ItemProperty -Path $localDumpsKey -Name DumpType -PropertyType DWord -Value $fullDumpType -Force | Out-Null
New-ItemProperty -Path $localDumpsKey -Name DumpCount -PropertyType DWord -Value $DumpCount -Force | Out-Null
New-ItemProperty -Path $localDumpsKey -Name DumpFolder -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null

Write-Output "CrashCapture: WER LocalDumps configured for $ExecutableName -> $DumpFolder (full dumps, keep $DumpCount)"
