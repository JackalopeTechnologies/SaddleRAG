# install-service.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'SaddleRAGMcp',
    [string]$DisplayName = 'SaddleRAG MCP',
    [string]$Description = 'SaddleRAG MCP server',
    [string]$PublishDirectory = (Join-Path $PSScriptRoot 'bin\x64\Release\net10.0\win-x64\publish'),
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'SaddleRAG\SaddleRAG.Mcp'),
    [switch]$InPlace,
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

function Get-ResolvedDirectory
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

    if (-not $item.PSIsContainer)
    {
        throw "$Label is not a directory: $Path"
    }

    $res = $item.FullName
    return $res
}

if (-not (Test-Administrator))
{
    throw 'Run this script from an elevated PowerShell session.'
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($null -ne $existingService)
{
    throw "Service '$ServiceName' already exists. Use update-service.ps1 or uninstall-service.ps1 first."
}

$resolvedPublishDirectory = Get-ResolvedDirectory -Path $PublishDirectory -Label 'PublishDirectory'
$sourceExePath = Join-Path $resolvedPublishDirectory 'SaddleRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $sourceExePath))
{
    throw "Published executable not found: $sourceExePath"
}

$targetDirectory = $resolvedPublishDirectory

if (-not $InPlace)
{
    if ($PSCmdlet.ShouldProcess($InstallDirectory, 'Create install directory'))
    {
        New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    }

    if ($PSCmdlet.ShouldProcess($InstallDirectory, "Copy published files from $resolvedPublishDirectory"))
    {
        Copy-Item -Path (Join-Path $resolvedPublishDirectory '*') -Destination $InstallDirectory -Recurse -Force
    }

    $targetDirectory = Get-ResolvedDirectory -Path $InstallDirectory -Label 'InstallDirectory'
}

$serviceExePath = Join-Path $targetDirectory 'SaddleRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $serviceExePath))
{
    throw "Service executable not found: $serviceExePath"
}

$serviceBinaryPath = '"' + $serviceExePath + '"'

if ($PSCmdlet.ShouldProcess($ServiceName, "Create Windows service using $serviceExePath"))
{
    New-Service -Name $ServiceName -BinaryPathName $serviceBinaryPath -DisplayName $DisplayName -Description $Description -StartupType Automatic
}

# Configure SCM failure actions so the service self-heals after a crash
# (issue #137): restart after 5s/30s/60s with a daily failure-counter reset.
# failureflag 1 extends recovery to non-crash error exits. Mirrors
# start-and-monitor.ps1 (the MSI path).
if ($PSCmdlet.ShouldProcess($ServiceName, 'Configure service recovery actions (restart 5s/30s/60s, daily reset)'))
{
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null
    Write-Output "Recovery: '$ServiceName' will auto-restart after failures (5s/30s/60s, daily reset)"
}

if (-not $SkipStart)
{
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Start Windows service'))
    {
        Start-Service -Name $ServiceName
    }
}

$service = Get-Service -Name $ServiceName

# Configure WER LocalDumps so a native crash (e.g. an ONNX access violation,
# issue #135) leaves a full memory dump for post-mortem analysis (#136).
# Mirrors SaddleRAG.Installer\ConfigureCrashCapture.ps1, which does the same
# for MSI installs. Idempotent.
$dumpFolder    = Join-Path $env:ProgramData 'SaddleRAG\CrashDumps'
$localDumpsKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\SaddleRAG.Mcp.exe'
$fullDumpType  = 2
$dumpCount     = 5

if ($PSCmdlet.ShouldProcess($localDumpsKey, "Configure WER LocalDumps (full dumps, keep $dumpCount, folder $dumpFolder)"))
{
    New-Item -ItemType Directory -Force -Path $dumpFolder | Out-Null
    New-Item -Path $localDumpsKey -Force | Out-Null
    New-ItemProperty -Path $localDumpsKey -Name DumpType -PropertyType DWord -Value $fullDumpType -Force | Out-Null
    New-ItemProperty -Path $localDumpsKey -Name DumpCount -PropertyType DWord -Value $dumpCount -Force | Out-Null
    New-ItemProperty -Path $localDumpsKey -Name DumpFolder -PropertyType ExpandString -Value $dumpFolder -Force | Out-Null
    Write-Output "CrashCapture: WER LocalDumps configured -> $dumpFolder (full dumps, keep $dumpCount)"
}

# Set OLLAMA_KEEP_ALIVE=-1 system-wide if not already configured.
# Records a registry marker so uninstall-service.ps1 can clean up only if
# we set the value (and the user did not change it afterward).
$ollamaEnvVar    = 'OLLAMA_KEEP_ALIVE'
$ollamaOurValue  = '-1'
$ollamaRegPath   = 'HKLM:\Software\Jackalope Technologies\SaddleRAG'
$ollamaMarker    = 'OllamaKeepAliveSetByUs'

$ollamaExisting = [Environment]::GetEnvironmentVariable($ollamaEnvVar, 'Machine')

if ([string]::IsNullOrEmpty($ollamaExisting))
{
    if ($PSCmdlet.ShouldProcess($ollamaEnvVar, "Set system environment variable to '$ollamaOurValue'"))
    {
        [Environment]::SetEnvironmentVariable($ollamaEnvVar, $ollamaOurValue, 'Machine')

        if (-not (Test-Path -LiteralPath $ollamaRegPath))
        {
            New-Item -Path $ollamaRegPath -Force | Out-Null
        }

        Set-ItemProperty -LiteralPath $ollamaRegPath -Name $ollamaMarker -Value 1 -Type DWord
        Write-Output "OllamaKeepAlive: set system-wide to '$ollamaOurValue' (reboot recommended)"
    }
}
else
{
    Write-Output "OllamaKeepAlive: already set to '$ollamaExisting'; left unchanged"
}

Write-Output "ServiceName: $ServiceName"
Write-Output "InstallDirectory: $targetDirectory"
Write-Output "Status: $($service.Status)"
Write-Output 'HealthUrl: http://localhost:6100/health'
Write-Output 'McpUrl: http://localhost:6100/mcp'