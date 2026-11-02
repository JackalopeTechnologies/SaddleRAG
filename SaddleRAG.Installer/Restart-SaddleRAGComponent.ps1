# Restart-SaddleRAGComponent.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

[CmdletBinding(DefaultParameterSetName = 'Service')]
param
(
    # One or more service names. Windows PowerShell 5.1 delivers "A,B" as ONE string when the
    # script is started with -File, so every element is also split on ',' below.
    [Parameter(Mandatory = $true, ParameterSetName = 'Service')]
    [string[]]$ServiceName,

    [Parameter(Mandatory = $true, ParameterSetName = 'Service')]
    [ValidateSet('Start','Stop','Restart')]
    [string]$Action,

    # Longest wait for each service to reach its target state (applied to every wait).
    [Parameter(ParameterSetName = 'Service')]
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 120,

    # Check the allow-list and installation, report through the exit code, change nothing
    # (no service call, no event-log entry).
    [Parameter(ParameterSetName = 'Service')]
    [switch]$ValidateOnly,

    # Diagnostic for the tests: parse the given ImagePath text, print the file name of the
    # executable it launches (empty when it cannot be determined) and exit. It reads no
    # registry value and touches no service.
    [Parameter(Mandatory = $true, ParameterSetName = 'ImagePathProbe')]
    [string]$TestImagePath
)

# No module may be auto-loaded. This process is elevated, and PSModulePath includes the
# user's own Documents\WindowsPowerShell\Modules, which the user (or anything running as
# the user, including a cloud-synced folder) can write: a look-alike of a cmdlet placed
# there would otherwise run with administrator rights the moment the script called it.
# Everything below therefore uses language keywords and .NET types only -- no cmdlet that
# lives in a module. Keep it that way: RestartComponentScriptTests fails on any such command.
$PSModuleAutoloadingPreference = 'None'

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
# allow-listed service control and one event-log entry, nothing else.
#
# Allow-list: the service named SaddleRAGMcp, or a service whose registered executable
# (ImagePath: the quoted path, or the first whitespace-free token when unquoted) has the file
# name mongod.exe. Any other name is refused before a service is touched.
#
# Names are processed in the order given. Every name is validated first; if any is refused or
# not installed nothing at all is changed. After that the first failure stops processing.
# Restart on a stopped service just starts it; Stop on a stopped service and Start on a
# running service succeed without doing anything. A service that is mid-transition is waited
# on first (a starting service is awaited before it is stopped, a stopping one before it is
# started), each wait bounded by TimeoutSeconds.
#
# Exit codes: 0 succeeded, 10 a name is not allow-listed, 11 a service is not installed,
# 12 timed out waiting for the target state, 13 the service control call failed.
#
# Each action is recorded in the Windows Application event log under the source named below
# (registered by the installer). The event log, unlike a file in ProgramData, cannot be edited
# or replaced by an unprivileged user.

# Strict mode and Stop turn typos and unexpected errors into failures instead of silently
# carrying on as administrator. Nothing here builds or evaluates script text, and no other
# script is dot-sourced or invoked.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# By full name, so no PSModulePath or current-directory lookup can supply the assembly.
$null = [System.Reflection.Assembly]::Load('System.ServiceProcess, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a')

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

$EventSourceName    = 'SaddleRAG Service Actions'
$EventIdSucceeded   = 1000
$EventIdNotSucceeded = 1001

# \z, not $: in .NET a trailing '$' also matches before a final newline, which would let a name
# ending in a line break through into the registry path and the log.
$ServiceNamePattern = '^[A-Za-z0-9_.-]+\z'

# The executable an ImagePath launches: the contents of the leading quoted string, or, when it
# is not quoted, ONLY the first whitespace-free token. Looking for ".exe" anywhere in the text
# would accept "C:\Windows\tool -log C:\data\mongod.exe". An unquoted path with spaces therefore
# yields its first token and is refused, which is also what the service control manager would
# try to run (the unquoted-service-path weakness). A quoted string must be followed by
# whitespace or the end, so "C:\x.exe"mongod.exe is rejected as ambiguous.
# The separators are a space or a tab only, never .NET's \s: Windows command-line parsing splits
# on those two, while \s also matches Unicode spaces such as U+00A0, so "mongod.exe" followed by
# a no-break space would otherwise look like a complete token ending in mongod.exe.
$ImagePathPattern = '^[ \t]*(?:"(?<path>[^"]+)"|(?<path>[^"\s]+))(?=[ \t]|\z)'

# Only a name that passed the pattern above may reach the registry or the service lookup; the
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

# Returns the file name of the executable named by an ImagePath string, or $null.
function Get-ImagePathExecutableName
{
    param
    (
        [AllowEmptyString()]
        [string]$ImagePath
    )

    $res = $null
    $match = [regex]::Match($ImagePath, $ImagePathPattern)
    if ($match.Success)
    {
        try
        {
            $res = [System.IO.Path]::GetFileName($match.Groups['path'].Value)
        }
        catch [System.ArgumentException]
        {
            # A path holding characters illegal in a path cannot name mongod.exe. Leaving the
            # result $null makes the service not allow-listed, which is the safe answer.
            $res = $null
        }
    }
    return $res
}

# Returns the file name of the executable a service runs, or $null when it cannot be read.
function Get-RegisteredExecutableName
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
            $res = Get-ImagePathExecutableName -ImagePath ([string]$key.GetValue('ImagePath', ''))
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
        $executable = Get-RegisteredExecutableName -Name $Name
        $res = ($null -ne $executable) -and $executable.Equals($MongoExecutableName, [StringComparison]::OrdinalIgnoreCase)
    }
    return $res
}

# The service with exactly this name, or $null. The list is enumerated and compared with an
# exact ordinal-ignore-case match, rather than asking a cmdlet to resolve a (wildcard-capable)
# name.
function Get-InstalledService
{
    param
    (
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $res = $null
    foreach ($candidate in [System.ServiceProcess.ServiceController]::GetServices())
    {
        if ($null -eq $res -and $candidate.ServiceName.Equals($Name, [StringComparison]::OrdinalIgnoreCase))
        {
            $res = $candidate
        }
        else
        {
            $candidate.Dispose()
        }
    }
    return $res
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
        $service = Get-InstalledService -Name $Name
        if ($null -ne $service)
        {
            $service.Dispose()
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
# before it is recorded; otherwise it could forge extra lines in the entry.
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
# so it cannot forge lines, and the length is bounded.
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

# Writes one Application event-log entry: Information for success, Warning for a refused,
# not-installed, timed-out or failed action.
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
        # The source is registered by the installer; it is deliberately not created here, so
        # an administrator process does not register event sources on a caller's behalf.
        if ([System.Diagnostics.EventLog]::SourceExists($EventSourceName))
        {
            $timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
            $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $message = "timestamp=$timestamp user=$user service=$Service action=$ActionName result=$Result"
            $entryType = [System.Diagnostics.EventLogEntryType]::Warning
            $eventId = $EventIdNotSucceeded
            if ($Result -eq 'Succeeded')
            {
                $entryType = [System.Diagnostics.EventLogEntryType]::Information
                $eventId = $EventIdSucceeded
            }
            [System.Diagnostics.EventLog]::WriteEntry($EventSourceName, $message, $entryType, $eventId)
        }
        else
        {
            [Console]::Error.WriteLine("Restart-SaddleRAGComponent: event source '$EventSourceName' is not registered; the action was not recorded.")
        }
    }
    catch
    {
        # The record is an audit aid, not part of the action: a missing source, a full log or
        # a permissions problem must never turn a service change that worked into a failure
        # (or hide one that did not), so the exit code is left to the action alone.
        [Console]::Error.WriteLine("Restart-SaddleRAGComponent: could not write the event-log entry: " + $_.Exception.Message)
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

    # Asking a service that is still starting to stop is refused or ignored by many services;
    # let it finish starting first.
    if ($Service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StartPending)
    {
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, $Timeout)
        $Service.Refresh()
    }
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

    # A service that is still stopping cannot be started; let it finish stopping first.
    if ($Service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StopPending)
    {
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $Timeout)
        $Service.Refresh()
    }
    if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running)
    {
        if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StartPending)
        {
            $Service.Start()
        }
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, $Timeout)
    }
}

# Performs the action on one validated service and returns its exit code. The result is recorded.
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
    $recordedName = Get-LoggableName -Name $Name
    try
    {
        $recordedName = $service.ServiceName
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
    Write-ActionLog -Service $recordedName -ActionName $ActionName -Result $result

    # Console, not the output stream: anything written to the output stream would become part
    # of this function's return value and corrupt the exit code.
    [Console]::Out.WriteLine("Restart-SaddleRAGComponent: $recordedName $ActionName $result")
    return $code
}

$exitCode = $ExitSucceeded
try
{
    if ($PSCmdlet.ParameterSetName -eq 'ImagePathProbe')
    {
        $probed = Get-ImagePathExecutableName -ImagePath $TestImagePath
        if ($null -eq $probed)
        {
            $probed = ''
        }
        [Console]::Out.WriteLine($probed)
    }
    else
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
                    $reason = 'NotAllowListed'
                    if ($check -eq $ExitNotInstalled)
                    {
                        $reason = 'NotInstalled'
                    }
                    [Console]::Out.WriteLine("Restart-SaddleRAGComponent: $(Get-LoggableName -Name $name) $reason")
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
}
catch
{
    # Anything unexpected (for example the SCM refusing a query) is reported as a failed
    # control call; stderr carries the reason, as in the other installer scripts.
    [Console]::Error.WriteLine("Restart-SaddleRAGComponent: FAILURE: " + $_.Exception.Message)
    $exitCode = $ExitControlFailed
}

exit $exitCode
