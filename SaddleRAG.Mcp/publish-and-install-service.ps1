# publish-and-install-service.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,

    [string]$ProjectPath = (Join-Path $PSScriptRoot 'SaddleRAG.Mcp.csproj'),
    [string]$ServiceName = 'SaddleRAGMcp',
    [string]$DisplayName = 'SaddleRAG MCP',
    [string]$Description = 'SaddleRAG MCP server',
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [switch]$SkipStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Administrator
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $res = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    return $res
}

function Get-ResolvedFilePath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        throw "$Label does not exist: $Path"
    }

    $item = Get-Item -LiteralPath $Path

    if ($item.PSIsContainer)
    {
        throw "$Label is not a file: $Path"
    }

    $res = $item.FullName
    return $res
}

function Get-ResolvedDirectoryPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $item = Get-Item -LiteralPath $Path
    $res = $item.FullName
    return $res
}

# This helper creates a fresh development server only. Publishing one project over
# an installed server/tray package can replace libraries that its other apps need.
if ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue))
{
    throw 'This service already exists. Upgrade it with the checked Windows package and MSI installer; this development helper cannot upgrade an installation.'
}
if ((Test-Path -LiteralPath $InstallDirectory) -and
    @(Get-ChildItem -LiteralPath $InstallDirectory -Force).Count -gt 0)
{
    throw 'InstallDirectory must be empty. Use scripts/Publish-WindowsPackage.ps1 and the MSI installer for an existing installation.'
}

if (-not (Test-Administrator))
{
    throw 'Run this script from an elevated PowerShell session.'
}

$resolvedProjectPath = Get-ResolvedFilePath -Path $ProjectPath -Label 'ProjectPath'
$resolvedInstallDirectory = Get-ResolvedDirectoryPath -Path $InstallDirectory

if ($PSCmdlet.ShouldProcess($resolvedProjectPath, "Publish to $resolvedInstallDirectory"))
{
    [xml]$packages = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Packages.props')
    $runtimeVersion = [string]$packages.Project.PropertyGroup.SaddleRagBundledRuntimeVersion
    if ([string]::IsNullOrWhiteSpace($runtimeVersion)) { throw 'The bundled runtime version is not pinned.' }
    & dotnet publish $resolvedProjectPath -c $Configuration -p:Platform=$Platform -r win-x64 --self-contained true -m:2 -p:TreatWarningsAsErrors=true -p:RuntimeFrameworkVersion=$runtimeVersion -o $resolvedInstallDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Server publishing failed; the service was not created.' }
}

$serviceExePath = Join-Path $resolvedInstallDirectory 'SaddleRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $serviceExePath))
{
    throw "Published executable not found: $serviceExePath"
}

$serviceBinaryPath = '"' + $serviceExePath + '"'

if ($PSCmdlet.ShouldProcess($ServiceName, "Create Windows service using $serviceExePath"))
{
    New-Service -Name $ServiceName -BinaryPathName $serviceBinaryPath -DisplayName $DisplayName -Description $Description -StartupType Automatic
}

if (-not $SkipStart)
{
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Start Windows service'))
    {
        Start-Service -Name $ServiceName
    }
}

$service = Get-Service -Name $ServiceName

Write-Output "ProjectPath: $resolvedProjectPath"
Write-Output "ServiceName: $ServiceName"
Write-Output "InstallDirectory: $resolvedInstallDirectory"
Write-Output "Status: $($service.Status)"
Write-Output 'HealthUrl: http://localhost:6100/health'
Write-Output 'McpUrl: http://localhost:6100/mcp'
