# PatchAppSettings.ps1
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.

[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$AppSettingsPath,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$ConnectionString,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$DatabaseName,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$OllamaEndpoint,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$ExecutionProvider,

    # Set by the immediate EscapeAppSettingsProperties CA when one of the
    # _ESCAPED property writes fails. The patch script aborts in that case
    # rather than silently writing an empty connection string / endpoint.
    # Empty string means "no escape failure recorded".
    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$EscapeFailed
)

# Rewrites the installed appsettings.json with the user's MongoDB / Ollama /
# ONNX-execution-provider choices captured by the installer UI (or the
# command line on a silent install). Mirrors the inline logic previously
# embedded in Package.wxs SetProperty; extracted here so it is testable in
# isolation and so future installer-driven config edits land in one place.
#
# ExecutionProvider falls back to 'Cpu' when empty so the written value is
# always a valid OnnxExecutionProvider enum literal. The CA wrapper in
# Package.wxs is Return="ignore", so this script's non-zero exit codes
# surface in the MSI log but do not abort the install — the runtime EP
# fallback catches DirectML load failures and degrades to CPU with a
# recorded warning. Hard-stopping a partial appsettings write would
# strand the user with no service and no diagnostic; soft-stopping
# leaves Mongo/Ollama placeholders that startup config-binding will
# complain about visibly.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Built-in defaults for the local profile. A blank installer input (an empty MSI
# property, or an escape CA that produced an empty *_ESCAPED value) must never
# overwrite a good value with "" -- that strands the service with no database and
# no Ollama endpoint. These mirror the ExecutionProvider -> Cpu fallback applied
# below, and match the shipped SaddleRAG.Mcp/appsettings.json template.
$DefaultConnectionString = 'mongodb://localhost:27017'
$DefaultDatabaseName     = 'SaddleRAG'
$DefaultOllamaEndpoint   = 'http://localhost:11434'

# Resolve a config value with a three-tier fallback: the provided installer value
# wins when non-blank; otherwise keep whatever the shipped template already holds;
# otherwise fall back to the built-in default. Never returns an empty string.
function Resolve-ConfigValue
{
    param
    (
        [AllowEmptyString()]
        [string]$Provided,

        [AllowEmptyString()]
        [string]$Existing,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Default
    )

    $res = $Default
    if (-not [string]::IsNullOrWhiteSpace($Provided))
    {
        $res = $Provided
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Existing))
    {
        $res = $Existing
    }
    return $res
}

$TempPath = $AppSettingsPath + '.tmp'

# Win32 MoveFileEx is the canonical atomic file-replace API on Windows.
# PowerShell 5.1 (which hosts the installer's deferred CA) runs on the
# .NET Framework, where [System.IO.File]::Move only has the 2-arg
# overload -- no built-in overwrite. P/Invoke MoveFileEx with
# MOVEFILE_REPLACE_EXISTING (0x1) to get a single atomic metadata op
# on NTFS without depending on a .NET version that PS 5.1 doesn't ship.
if (-not ('SaddleRAG.NativeApi.FileUtils' -as [type]))
{
    Add-Type -Namespace SaddleRAG.NativeApi -Name FileUtils -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);
'@
}

try
{
    # If the upstream JScript CA failed to escape one or more property
    # values, [X_ESCAPED] expanded to empty here; writing empty strings to
    # appsettings.json would let the install "succeed" with broken config.
    # Abort with the surfaced failure instead so the operator sees it in
    # the MSI log (the FAILURE: sentinel below is grep-friendly).
    if (-not [string]::IsNullOrEmpty($EscapeFailed))
    {
        throw "Upstream argv-escape failed for one or more MSI properties: $EscapeFailed"
    }

    $json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json

    $json.MongoDB.Profiles.local.ConnectionString = Resolve-ConfigValue -Provided $ConnectionString -Existing ([string]$json.MongoDB.Profiles.local.ConnectionString) -Default $DefaultConnectionString
    $json.MongoDB.Profiles.local.DatabaseName     = Resolve-ConfigValue -Provided $DatabaseName     -Existing ([string]$json.MongoDB.Profiles.local.DatabaseName)     -Default $DefaultDatabaseName
    $json.Ollama.Endpoint                         = Resolve-ConfigValue -Provided $OllamaEndpoint    -Existing ([string]$json.Ollama.Endpoint)                         -Default $DefaultOllamaEndpoint

    $effectiveProvider = if ([string]::IsNullOrWhiteSpace($ExecutionProvider))
                         {
                             'Cpu'
                         }
                         else
                         {
                             $ExecutionProvider
                         }
    $json.Onnx.ExecutionProvider = $effectiveProvider

    # Write to a sibling .tmp file, then atomically replace the target via
    # MoveFileEx(..., MOVEFILE_REPLACE_EXISTING). On NTFS this is a single
    # atomic metadata operation, so a crash mid-write (disk-full, antivirus
    # interrupt, process kill) leaves the original appsettings.json intact
    # rather than truncated or missing. Move-Item -Force is *not* atomic in
    # PowerShell's provider -- it deletes the destination first, then
    # renames -- so a crash in between leaves no appsettings.json at all,
    # worse than the truncation mode this guard exists to prevent. The
    # runtime EP fallback can absorb a missing Onnx.ExecutionProvider, but
    # a half-written or missing MongoDB or Ollama section would fail
    # startup config-binding with no diagnostic link back to the installer.
    $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $TempPath -Encoding UTF8
    $MoveFileReplaceExisting = 0x1
    $moved = [SaddleRAG.NativeApi.FileUtils]::MoveFileEx($TempPath, $AppSettingsPath, $MoveFileReplaceExisting)
    if (-not $moved)
    {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw [System.IO.IOException]::new("MoveFileEx failed with Win32 error $err while replacing '$AppSettingsPath'.")
    }

    # Stable success-sentinel token for MSI-log scrapers that want to
    # distinguish ran-and-worked from ran-and-silently-failed (the CA wrapper
    # is Return="ignore", so a non-zero exit shows in the log but doesn't
    # abort the install).
    Write-Output "PatchAppSettings: SUCCESS (ExecutionProvider=$effectiveProvider) to $AppSettingsPath"
}
catch
{
    # Best-effort cleanup of the temp file; failure here is fine, the catch
    # has already captured the original problem.
    if (Test-Path -LiteralPath $TempPath)
    {
        Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
    }
    # Write-Error is intentionally NOT used here: with $ErrorActionPreference='Stop'
    # it word-wraps long messages in the redirected-pipe case (no terminal attached),
    # inserting CRLF mid-string and breaking substring searches in MSI-log scrapers
    # and tests. [Console]::Error.WriteLine writes the message as a single raw line.
    [Console]::Error.WriteLine("PatchAppSettings: FAILURE: " + $_.Exception.Message)
    exit 1
}
