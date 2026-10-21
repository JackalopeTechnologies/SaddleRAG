#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version,
    [string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Publish output already exists: $output. Use a new directory." }
[xml]$packages = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props')
$runtimeVersion = [string]$packages.Project.PropertyGroup.SaddleRagBundledRuntimeVersion
if ([string]::IsNullOrWhiteSpace($runtimeVersion)) { throw 'The bundled runtime version is not pinned.' }
New-Item -ItemType Directory -Path $output | Out-Null
$parts = foreach ($project in @('SaddleRAG.Mcp','SaddleRAG.Cli','SaddleRAG.Tray','SaddleRAG.Installer.Helper')) {
    $part = Join-Path $output "parts\$project"
    $projectPath = Join-Path $repository "$project\$project.csproj"
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true -m:2 -p:TreatWarningsAsErrors=true -p:Version=$Version -p:RuntimeFrameworkVersion=$runtimeVersion -o $part | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    $part
}
& (Join-Path $PSScriptRoot 'Merge-PublishOutputs.ps1') -SourceDirectories $parts -DestinationDirectory (Join-Path $output 'publish') -Version $Version -RuntimeVersion $runtimeVersion
