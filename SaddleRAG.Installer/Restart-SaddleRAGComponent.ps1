# Restart-SaddleRAGComponent.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

[CmdletBinding()]
param
(
    # One or more service names. Windows PowerShell 5.1 delivers "A,B" as ONE string when the
    # script is started with -File, so every element is also split on ',' below.
    [Parameter(Mandatory = $true)]
    [string[]]$ServiceName,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Start','Stop','Restart')]
    [string]$Action,

    # Longest wait for each service to reach its target state.
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 120,

    # Check the allow-list and installation, report through the exit code, change nothing
    # (no service call, no log line).
    [switch]$ValidateOnly
)

# Starts, stops or restarts the SaddleRAG MCP service and the local MongoDB service on behalf
# of the SaddleRAG tray. The tray runs unelevated; it launches THIS script elevated (UAC) from
# the install folder:
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<install>\Restart-SaddleRAGComponent.ps1"
#                  -ServiceName A,B -Action Restart
#
# SECURITY: THIS FILE MUST STAY WRITABLE BY ADMINISTRATORS ONLY. The tray elevates it by path,
# so anyone who can edit it can run code as administrator the next time the operator approves
# the prompt. It lives under Program Files, which is admin-writable; never relax its ACL, and
# never copy it to a user-writable folder. It is therefore also kept as small as possible:
# allow-listed service control and one log line, nothing else.
#
# Allow-list: the service named SaddleRAGMcp, or a service whose registered executable
# (ImagePath, quoted or not, arguments ignored) has the file name mongod.exe. Any other name
# is refused before a service is touched.
#
# Names are processed in the order given. Every name is validated first; if any is refused or
# not installed nothing at all is changed. After that the first failure stops processing.
# Restart on a stopped service just starts it; Stop on a stopped service and Start on a
# running service succeed without doing anything.
#
# Exit codes: 0 succeeded, 10 a name is not allow-listed, 11 a service is not installed,
# 12 timed out waiting for the target state, 13 the service control call failed.
#
# Each action appends one line to %ProgramData%\SaddleRAG\logs\service-actions.log.

# Strict mode and Stop turn typos and unexpected errors into failures instead of silently
# carrying on as administrator. Nothing here builds or evaluates script text, and no other
# script is dot-sourced or invoked.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.ServiceProcess

$ExitSucceeded     = 0
$ExitNotAllowed    = 10
$ExitNotInstalled  = 11
$ExitTimedOut      = 12
$ExitControlFailed = 13

$McpServiceName       = 'SaddleRAGMcp'
$MongoExecutableName  = 'mongod.exe'
$ServicesRegistryPath = 'SYSTEM\CurrentControlSet\Services'
$MaxLoggedNameLength  = 64
$MaxLoggedTextLength  = 200

# \z, not $: in .NET a trailing '$' also matches before a final newline, which would let a name
# ending in a line break through into the registry path and the log.
$ServiceNamePattern = '^[A-Za-z0-9_.-]+\z'

# Only a name that passed the pattern above may reach the registry or Get-Service; the
# pattern excludes wildcards, path separators, spaces and control characters.
function Test-ServiceNameFormat
{
    param
    (
        [AllowEmptyString()]
        [string]$Name
    )

    return ($Name -match $ServiceNamePattern)
}

# Returns the file name of the executable a service runs, or $null when it cannot be read.
# ImagePath may be quoted with arguments ("C:\...\mongod.exe" --config "..." --service) or an
# unquoted path followed by arguments; only the executable is compared.
function Get-ServiceExecutableName
{
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $res = $null
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($ServicesRegistryPath + '\' + $Name, $false)
    if ($null -ne $key)
    {
        try
        {
            $imagePath = [string]$key.GetValue('ImagePath', '')
            $match = [regex]::Match($imagePath, '^\s*(?:"(?<path>[^"]+)"|(?<path>.+?\.exe)(?=\s|$))', 'IgnoreCase')
            if ($match.Success)
            {
                $res = [System.IO.Path]::GetFileName($match.Groups['path'].Value)
            }
        }
        catch [System.ArgumentException]
        {
            # An ImagePath holding characters illegal in a path cannot name mongod.exe. Leaving
            # the result $null makes the service not allow-listed, which is the safe answer.
            $res = $null
        }
        finally
        {
            $key.Dispose()
        }
    }
    return $res
}

function Test-ServiceAllowListed
{
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $res = $false
    if ($Name.Equals($McpServiceName, [StringComparison]::OrdinalIgnoreCase))
    {
        $res = $true
    }
    else
    {
        $executable = Get-ServiceExecutableName -Name $Name
        $res = ($null -ne $executable) -and $executable.Equals($MongoExecutableName, [StringComparison]::OrdinalIgnoreCase)
    }
    return $res
}

function Get-InstalledService
{
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

# Exit code for one name before any change: 0 usable, 10 refused, 11 allow-listed but absent.
function Test-ServiceTarget
{
    param
    (
        [AllowEmptyString()]
        [string]$Name
    )

    $res = $ExitNotAllowed
    if ((Test-ServiceNameFormat -Name $Name) -and (Test-ServiceAllowListed -Name $Name))
    {
        $res = $ExitNotInstalled
        if ($null -ne (Get-InstalledService -Name $Name))
        {
            $res = $ExitSucceeded
        }
    }
    return $res
}

# Splits each element on ',', trims, drops blanks and case-insensitive repeats, keeps order.
function Resolve-RequestedNames
{
    param
    (
        [AllowEmptyCollection()]
        [string[]]$Requested
    )

    $seen = @{}
    $names = @()
    foreach ($element in $Requested)
    {
        foreach ($part in $element.Split(','))
        {
            $trimmed = $part.Trim()
            if ($trimmed.Length -gt 0 -and -not $seen.ContainsKey($trimmed.ToLowerInvariant()))
            {
                $seen[$trimmed.ToLowerInvariant()] = $true
                $names += $trimmed
            }
        }
    }
    return $names
}

# A refused name is caller-supplied text, so it is reduced to the allowed alphabet and length
# before it is written to the log; otherwise it could forge extra log lines.
function Get-LoggableName
{
    param
    (
        [AllowEmptyString()]
        [string]$Name
    )

    $clean = ($Name -replace '[^A-Za-z0-9_.-]', '?')
    if ($clean.Length -gt $MaxLoggedNameLength)
    {
        $clean = $clean.Substring(0, $MaxLoggedNameLength)
    }
    return $clean
}

# Free text from an exception message: control characters (line breaks above all) are replaced
# so it cannot forge log lines, and the length is bounded.
function Get-LoggableText
{
    param
    (
        [AllowEmptyString()]
        [string]$Text
    )

    $clean = ($Text -replace '\p{Cc}', ' ')
    if ($clean.Length -gt $MaxLoggedTextLength)
    {
        $clean = $clean.Substring(0, $MaxLoggedTextLength)
    }
    return $clean
}

function Write-ActionLog
{
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$Service,

        [Parameter(Mandatory = $true)]
        [string]$ActionName,

        [Parameter(Mandatory = $true)]
        [string]$Result
    )

    try
    {
        # The OS-defined ProgramData folder, not the %ProgramData% variable: this process
        # inherits the unelevated caller's environment, which must not choose where an
        # administrator process creates folders and appends text.
        $logDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SaddleRAG\logs'
        if (-not (Test-Path -LiteralPath $logDirectory))
        {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }

        $timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $line = "$timestamp`t$user`t$Service`t$ActionName`t$Result"
        Add-Content -LiteralPath (Join-Path $logDirectory 'service-actions.log') -Value $line -Encoding UTF8
    }
    catch
    {
        # The log is an audit aid, not part of the action: a full disk, a locked file or a
        # permissions problem must never turn a service change that worked into a failure
        # (or hide one that did not), so the exit code is left to the action alone.
        [Console]::Error.WriteLine("Restart-SaddleRAGComponent: could not write the action log: " + $_.Exception.Message)
    }
}

# True when this exception, or anything it wraps, is the service-wait timeout. PowerShell wraps
# .NET method failures in MethodInvocationException, so the inner exceptions must be walked.
function Test-IsTimeout
{
    param
    (
        [Parameter(Mandatory = $true)]
        [System.Exception]$Failure
    )

    $res = $false
    $current = $Failure
    while ($null -ne $current -and -not $res)
    {
        $res = $current -is [System.ServiceProcess.TimeoutException]
        $current = $current.InnerException
    }
    return $res
}

function Stop-TargetService
{
    param
    (
        [Parameter(Mandatory = $true)]
        $Service,

        [Parameter(Mandatory = $true)]
        [TimeSpan]$Timeout
    )

    $Service.Refresh()
    if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped)
    {
        if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StopPending)
        {
            $Service.Stop()
        }
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $Timeout)
    }
}

function Start-TargetService
{
    param
    (
        [Parameter(Mandatory = $true)]
        $Service,

        [Parameter(Mandatory = $true)]
        [TimeSpan]$Timeout
    )

    $Service.Refresh()
    if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running)
    {
        if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StartPending)
        {
            $Service.Start()
        }
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, $Timeout)
    }
}

# Performs the action on one validated service and returns its exit code. The result is logged.
function Invoke-ServiceAction
{
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$ActionName,

        [Parameter(Mandatory = $true)]
        [TimeSpan]$Timeout
    )

    $service = Get-InstalledService -Name $Name
    $code = $ExitSucceeded
    $result = 'Succeeded'
    try
    {
        if ($ActionName -in @('Stop', 'Restart'))
        {
            Stop-TargetService -Service $service -Timeout $Timeout
        }
        if ($ActionName -in @('Start', 'Restart'))
        {
            Start-TargetService -Service $service -Timeout $Timeout
        }
    }
    catch
    {
        if (Test-IsTimeout -Failure $_.Exception)
        {
            $code = $ExitTimedOut
            $result = 'TimedOut'
        }
        else
        {
            $code = $ExitControlFailed
            $result = 'Failed: ' + (Get-LoggableText -Text $_.Exception.Message)
        }
    }
    Write-ActionLog -Service $service.Name -ActionName $ActionName -Result $result

    # Console, not Write-Output: anything written to the output stream would become part of
    # this function's return value and corrupt the exit code.
    [Console]::Out.WriteLine("Restart-SaddleRAGComponent: $($service.Name) $ActionName $result")
    return $code
}

$exitCode = $ExitSucceeded
try
{
    $names = @(Resolve-RequestedNames -Requested $ServiceName)
    if ($names.Count -eq 0)
    {
        $exitCode = $ExitNotAllowed
    }

    # Validate every name before changing anything so a refused name can never leave the
    # others half-processed.
    foreach ($name in $names)
    {
        if ($exitCode -eq $ExitSucceeded)
        {
            $check = Test-ServiceTarget -Name $name
            if ($check -ne $ExitSucceeded)
            {
                $exitCode = $check
                $reason = if ($check -eq $ExitNotInstalled) { 'NotInstalled' } else { 'NotAllowListed' }
                Write-Output "Restart-SaddleRAGComponent: $(Get-LoggableName -Name $name) $reason"
                if (-not $ValidateOnly)
                {
                    Write-ActionLog -Service (Get-LoggableName -Name $name) -ActionName $Action -Result $reason
                }
            }
        }
    }

    if ($exitCode -eq $ExitSucceeded -and -not $ValidateOnly)
    {
        $timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        foreach ($name in $names)
        {
            if ($exitCode -eq $ExitSucceeded)
            {
                $exitCode = Invoke-ServiceAction -Name $name -ActionName $Action -Timeout $timeout
            }
        }
    }
}
catch
{
    # Anything unexpected (for example the SCM refusing a query) is reported as a failed
    # control call; stderr carries the reason, as in the other installer scripts.
    [Console]::Error.WriteLine("Restart-SaddleRAGComponent: FAILURE: " + $_.Exception.Message)
    $exitCode = $ExitControlFailed
}

exit $exitCode
