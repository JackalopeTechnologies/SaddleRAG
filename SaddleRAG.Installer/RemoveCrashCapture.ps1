# RemoveCrashCapture.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

# Removes the WER LocalDumps configuration for SaddleRAG.Mcp.exe on
# uninstall (issue #136). Captured dumps under the dump folder are
# intentionally left in place - they may still be needed for a post-mortem
# after the product is removed.

[CmdletBinding()]
param(
    [string]$ExecutableName = 'SaddleRAG.Mcp.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$localDumpsKey = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$ExecutableName"

if (Test-Path -LiteralPath $localDumpsKey)
{
    Remove-Item -LiteralPath $localDumpsKey -Recurse -Force
    Write-Output "CrashCapture: removed WER LocalDumps configuration for $ExecutableName"
}
else
{
    Write-Output "CrashCapture: no WER LocalDumps configuration present for $ExecutableName"
}
