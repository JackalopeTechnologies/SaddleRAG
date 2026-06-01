# coverage.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

<#
.SYNOPSIS
Run the test suite with code-coverage collection and open an HTML report.

.DESCRIPTION
See scripts/README.md for the full workflow, flags, gating policy, and
CI parity notes. No coverage gate is enforced.

.PARAMETER NoOpen
Skip opening the generated report in the default browser.

.PARAMETER Filter
xUnit filter expression passed through to dotnet test. Default
`Category!=Integration` matches CI.
#>
[CmdletBinding()]
param
(
    [switch]$NoOpen,
    [string]$Filter = "Category!=Integration"
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ResultsDir = Join-Path $RepoRoot 'coverage-results'
$HtmlDir = Join-Path $ResultsDir 'html'

if (Test-Path $ResultsDir)
{
    Remove-Item $ResultsDir -Recurse -Force
}

Write-Host "Restoring dotnet tools..." -ForegroundColor Cyan
dotnet tool restore --tool-manifest (Join-Path $RepoRoot '.config/dotnet-tools.json') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }

Write-Host "Running tests with coverage..." -ForegroundColor Cyan
$testProj = Join-Path $RepoRoot 'SaddleRAG.Tests/SaddleRAG.Tests.csproj'
dotnet test $testProj `
    --collect:"XPlat Code Coverage" `
    --results-directory $ResultsDir `
    --filter $Filter `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }

$coberturaFiles = Get-ChildItem -Path $ResultsDir -Filter 'coverage.cobertura.xml' -Recurse
if ($coberturaFiles.Count -eq 0) { throw "No coverage.cobertura.xml produced." }

Write-Host "Generating HTML report..." -ForegroundColor Cyan
dotnet tool run reportgenerator `
    "-reports:$($coberturaFiles.FullName -join ';')" `
    "-targetdir:$HtmlDir" `
    "-reporttypes:HtmlInline_AzurePipelines;TextSummary;MarkdownSummaryGithub"
if ($LASTEXITCODE -ne 0) { throw "reportgenerator failed." }

$summary = Get-Content (Join-Path $HtmlDir 'Summary.txt') -Raw
Write-Host "`n$summary" -ForegroundColor Green

if (-not $NoOpen)
{
    $indexHtml = Join-Path $HtmlDir 'index.html'
    if (Test-Path $indexHtml)
    {
        Start-Process $indexHtml
    }
}
