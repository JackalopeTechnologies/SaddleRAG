<#
.SYNOPSIS
Run the test suite with code-coverage collection and open an HTML report.

.DESCRIPTION
1. Restores the local dotnet tool manifest (dotnet-reportgenerator-globaltool).
2. Runs `dotnet test SaddleRAG.Tests` with --collect:"XPlat Code Coverage"
   into ./coverage-results.
3. Generates an HTML report under ./coverage-results/html and opens
   index.html.

Coverage gating is NOT enforced — the script always exits 0 on a successful
test run regardless of the coverage percentage. Add a gate by parsing
./coverage-results/html/Summary.txt if you decide to enforce one later.

.PARAMETER NoOpen
Skip opening the generated report in the default browser.

.PARAMETER Filter
xUnit filter expression passed through to dotnet test (e.g.
"Category!=Integration"). Default: exclude integration tests so the run
matches CI.
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
