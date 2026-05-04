[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'SaddleRAGMcp',
    [switch]$RemoveFiles
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

function Get-ServiceExecutablePath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $service = Get-CimInstance Win32_Service -Filter "Name = '$Name'"
    $res = $null

    if ($null -ne $service)
    {
        $pathName = $service.PathName.Trim()
        $res = $pathName

        if ($pathName.StartsWith('"'))
        {
            $closingQuoteIndex = $pathName.IndexOf('"', 1)

            if ($closingQuoteIndex -lt 0)
            {
                throw "Unable to parse service executable path: $pathName"
            }

            $res = $pathName.Substring(1, $closingQuoteIndex - 1)
        }
        else
        {
            $firstSpaceIndex = $pathName.IndexOf(' ')

            if ($firstSpaceIndex -ge 0)
            {
                $res = $pathName.Substring(0, $firstSpaceIndex)
            }
        }
    }

    return $res
}

if (-not (Test-Administrator))
{
    throw 'Run this script from an elevated PowerShell session.'
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($null -eq $existingService)
{
    throw "Service '$ServiceName' was not found."
}

$existingExePath = Get-ServiceExecutablePath -Name $ServiceName
$existingInstallDirectory = $null

if (-not [string]::IsNullOrWhiteSpace($existingExePath))
{
    $existingInstallDirectory = Split-Path -Path $existingExePath -Parent
}

if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped)
{
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop Windows service'))
    {
        Stop-Service -Name $ServiceName -Force
    }
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Remove Windows service'))
{
    Remove-Service -Name $ServiceName
}

if ($RemoveFiles -and -not [string]::IsNullOrWhiteSpace($existingInstallDirectory) -and (Test-Path -LiteralPath $existingInstallDirectory))
{
    if ($PSCmdlet.ShouldProcess($existingInstallDirectory, 'Remove installed files'))
    {
        Remove-Item -LiteralPath $existingInstallDirectory -Recurse -Force
    }
}

# Conditionally remove OLLAMA_KEEP_ALIVE if (and only if) install-service.ps1
# set it during install AND the value still matches what we set. Always
# clears the marker registry value so we leave nothing behind.
$ollamaEnvVar           = 'OLLAMA_KEEP_ALIVE'
$ollamaOurValue         = '-1'
$ollamaRegPath          = 'HKLM:\Software\Jackalope Technologies\SaddleRAG'
$ollamaParentRegPath    = 'HKLM:\Software\Jackalope Technologies'
$ollamaMarker           = 'OllamaKeepAliveSetByUs'

$ollamaMarkerValue = $null

if (Test-Path -LiteralPath $ollamaRegPath)
{
    $prop = Get-ItemProperty -LiteralPath $ollamaRegPath -Name $ollamaMarker -ErrorAction SilentlyContinue

    if ($null -ne $prop)
    {
        $ollamaMarkerValue = $prop.$ollamaMarker
    }
}

if ($ollamaMarkerValue -eq 1)
{
    $ollamaCurrent = [Environment]::GetEnvironmentVariable($ollamaEnvVar, 'Machine')

    if ($ollamaCurrent -eq $ollamaOurValue)
    {
        if ($PSCmdlet.ShouldProcess($ollamaEnvVar, 'Remove system environment variable'))
        {
            [Environment]::SetEnvironmentVariable($ollamaEnvVar, $null, 'Machine')
            Write-Output "OllamaKeepAlive: removed (was '$ollamaOurValue')"
        }
    }
    else
    {
        Write-Output "OllamaKeepAlive: current value '$ollamaCurrent' does not match what we set; leaving alone"
    }

    Remove-ItemProperty -LiteralPath $ollamaRegPath -Name $ollamaMarker -ErrorAction SilentlyContinue
}
else
{
    Write-Output 'OllamaKeepAlive: marker not present; leaving env var alone'
}

# Drop the SaddleRAG and Jackalope Technologies registry keys if they are
# now empty (so we leave no breadcrumbs from this install).
if (Test-Path -LiteralPath $ollamaRegPath)
{
    $remainingValues  = (Get-Item -LiteralPath $ollamaRegPath).Property
    $remainingSubkeys = Get-ChildItem -LiteralPath $ollamaRegPath -ErrorAction SilentlyContinue

    if (($null -eq $remainingValues -or $remainingValues.Count -eq 0) -and
        ($null -eq $remainingSubkeys -or $remainingSubkeys.Count -eq 0))
    {
        Remove-Item -LiteralPath $ollamaRegPath -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $ollamaParentRegPath)
{
    $parentRemainingValues  = (Get-Item -LiteralPath $ollamaParentRegPath).Property
    $parentRemainingSubkeys = Get-ChildItem -LiteralPath $ollamaParentRegPath -ErrorAction SilentlyContinue

    if (($null -eq $parentRemainingValues -or $parentRemainingValues.Count -eq 0) -and
        ($null -eq $parentRemainingSubkeys -or $parentRemainingSubkeys.Count -eq 0))
    {
        Remove-Item -LiteralPath $ollamaParentRegPath -ErrorAction SilentlyContinue
    }
}

Write-Output "ServiceName: $ServiceName"
Write-Output 'Status: Removed'

if (-not [string]::IsNullOrWhiteSpace($existingInstallDirectory))
{
    Write-Output "InstallDirectory: $existingInstallDirectory"
}