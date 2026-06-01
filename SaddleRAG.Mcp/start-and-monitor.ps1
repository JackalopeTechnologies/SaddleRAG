# start-and-monitor.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ServiceName,
    [Parameter(Mandatory)] [string]$HealthUrl,
    [Parameter(Mandatory)] [string]$BinaryPath,
    [string]$DisplayName = 'SaddleRAG MCP Server',
    [string]$Description = 'Documentation RAG system - MCP server for AI-assisted code documentation lookup.',
    [int]$TotalTimeoutSec = 300,
    [int]$PollIntervalSec = 2,
    [int]$HealthRequestTimeoutSec = 5,
    [int]$MaxStartAttempts = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

function Write-Stamp
{
    param(
        [string]$Message,
        [System.Diagnostics.Stopwatch]$Sw
    )

    $elapsed = $Sw.Elapsed.ToString('mm\:ss')
    Write-Host "[$elapsed] $Message"
}

function Test-HealthEndpoint
{
    param(
        [string]$Url,
        [int]$TimeoutSec
    )

    $res = $false
    try
    {
        $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec
        $res = ($resp.StatusCode -eq 200)
    }
    catch
    {
    }

    return $res
}

function Register-SaddleRagService
{
    param(
        [string]$Name,
        [string]$BinPath,
        [string]$Display,
        [string]$Desc,
        [System.Diagnostics.Stopwatch]$Sw
    )

    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if ($null -ne $existing)
    {
        Write-Stamp "Service '$Name' already registered; reusing existing registration." $Sw
    }
    else
    {
        if (-not (Test-Path -LiteralPath $BinPath))
        {
            Write-Stamp "Binary path does not exist: $BinPath" $Sw
            throw "Binary not found at $BinPath"
        }

        Write-Stamp "Registering '$Name' via New-Service (Auto, LocalSystem) -> $BinPath" $Sw
        New-Service -Name $Name -BinaryPathName ('"' + $BinPath + '"') -DisplayName $Display -Description $Desc -StartupType Automatic | Out-Null
        Write-Stamp "Service '$Name' registered." $Sw
    }
}

function Add-UsersServiceControlAce
{
    param(
        [string]$Name,
        [System.Diagnostics.Stopwatch]$Sw
    )

    # Extend the service DACL with an ACE letting the built-in Users group (SDDL
    # alias 'BU', S-1-5-32-545) start, stop, and query the service WITHOUT UAC, so
    # the non-elevated SaddleRAG tray can manage it. This replaces a former
    # util:PermissionEx in Package.wxs: that compiles to Wix4ExecSecureObjects,
    # which MSI runs ~94 sequence slots BEFORE this script creates the service, so
    # it failed fatally with 0x80070424 ERROR_SERVICE_DOES_NOT_EXIST and rolled the
    # install back. Granting here keeps the ACE in lockstep with creation. Mirrors
    # scripts/grant-service-control.ps1 (ACE A;;RPWPLORC;;;BU). Best-effort: a grant
    # failure degrades tray control to requiring UAC but never fails the install.
    $ourAce = '(A;;RPWPLORC;;;BU)'

    $existingSddl = (& sc.exe sdshow $Name | Out-String).Trim()
    if (-not $existingSddl -or $existingSddl -notmatch '^D:')
    {
        Write-Stamp "Could not read SDDL for '$Name' to grant Users control; skipping (tray will need UAC). sc.exe: $existingSddl" $Sw
        return
    }

    if ($existingSddl -match [regex]::Escape($ourAce))
    {
        Write-Stamp "Users-group control ACE already present on '$Name'; no change." $Sw
        return
    }

    # Insert our ACE at the end of the DACL, before any SACL. The DACL is a run of
    # (...) ACEs; the SACL (if present) begins at the ')S:' boundary. A naive
    # ^D:[^S]* split fails because DACL access flags contain 'S' (e.g. SW).
    $saclMarkerIndex = $existingSddl.IndexOf(')S:')
    if ($saclMarkerIndex -lt 0)
    {
        $newSddl = "$existingSddl$ourAce"
    }
    else
    {
        $daclPart = $existingSddl.Substring(0, $saclMarkerIndex + 1)
        $saclPart = $existingSddl.Substring($saclMarkerIndex + 1)
        $newSddl = "$daclPart$ourAce$saclPart"
    }

    $output = & sc.exe sdset $Name $newSddl 2>&1
    if ($LASTEXITCODE -eq 0)
    {
        Write-Stamp "Granted Users-group start/stop/query control on '$Name' (no-UAC tray control)." $Sw
    }
    else
    {
        Write-Stamp "sc.exe sdset failed ($LASTEXITCODE) granting Users control on '$Name'; tray will need UAC. $output" $Sw
    }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$startAttempts = 0
$lastStatus = ''
$healthy = $false
$exitCode = 1

Write-Stamp "Starting '$ServiceName' and polling '$HealthUrl' (overall timeout ${TotalTimeoutSec}s, max ${MaxStartAttempts} start attempts)" $sw

try
{
    Register-SaddleRagService -Name $ServiceName -BinPath $BinaryPath -Display $DisplayName -Desc $Description -Sw $sw
}
catch
{
    Write-Stamp "Service registration failed: $($_.Exception.Message)" $sw
    exit 1
}

# Best-effort, non-fatal: grant the Users group no-UAC start/stop/query control.
Add-UsersServiceControlAce -Name $ServiceName -Sw $sw

while (-not $healthy -and $sw.Elapsed.TotalSeconds -lt $TotalTimeoutSec)
{
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($null -eq $svc)
    {
        Write-Stamp "Service '$ServiceName' is not registered in SCM. Aborting." $sw
        break
    }

    $status = $svc.Status.ToString()

    if ($status -ne $lastStatus)
    {
        Write-Stamp "Service status: $status (attempts so far: $startAttempts)" $sw
        $lastStatus = $status
    }

    if ($status -eq 'Stopped' -and $startAttempts -lt $MaxStartAttempts)
    {
        $startAttempts++
        Write-Stamp "Issuing Start-Service (attempt ${startAttempts} of ${MaxStartAttempts})" $sw
        try
        {
            Start-Service -Name $ServiceName -ErrorAction Stop
        }
        catch
        {
            Write-Stamp "Start-Service raised: $($_.Exception.Message)" $sw
        }
    }

    if ($status -eq 'Running')
    {
        $healthy = Test-HealthEndpoint -Url $HealthUrl -TimeoutSec $HealthRequestTimeoutSec
    }

    if ($status -eq 'Stopped' -and $startAttempts -ge $MaxStartAttempts)
    {
        Write-Stamp "Exceeded max start attempts (${MaxStartAttempts}); service kept transitioning to Stopped." $sw
        break
    }

    if (-not $healthy)
    {
        Start-Sleep -Seconds $PollIntervalSec
    }
}

if ($healthy)
{
    Write-Stamp "Service is healthy after $($sw.Elapsed.TotalSeconds.ToString('F1'))s ($startAttempts start attempt(s))" $sw
    $exitCode = 0
}
else
{
    Write-Stamp "Service did not become healthy within ${TotalTimeoutSec}s. Last status: '$lastStatus', start attempts: $startAttempts. Install will roll back." $sw
}

exit $exitCode
