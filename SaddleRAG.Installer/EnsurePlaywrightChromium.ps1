# EnsurePlaywrightChromium.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License in the repo root.

# Installs the Chromium revision pinned by the Microsoft.Playwright assembly
# shipped beside this script. The MSI runs this as LocalSystem, matching the
# identity of the SaddleRAG MCP Windows service.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$playwrightScript = Join-Path $PSScriptRoot 'playwright.ps1'
if (-not (Test-Path -LiteralPath $playwrightScript -PathType Leaf)) {
    throw "Playwright install script was not found: $playwrightScript"
}

& $playwrightScript install chromium
if ($LASTEXITCODE -ne 0) {
    throw "Playwright Chromium installation failed with exit code $LASTEXITCODE."
}

Write-Output 'Playwright Chromium installation completed.'