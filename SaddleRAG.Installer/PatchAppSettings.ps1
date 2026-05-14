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
    [string]$ExecutionProvider
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

try
{
    $json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json

    $json.MongoDB.Profiles.local.ConnectionString = $ConnectionString
    $json.MongoDB.Profiles.local.DatabaseName     = $DatabaseName
    $json.Ollama.Endpoint                         = $OllamaEndpoint

    $effectiveProvider = if ([string]::IsNullOrWhiteSpace($ExecutionProvider))
                         {
                             'Cpu'
                         }
                         else
                         {
                             $ExecutionProvider
                         }
    $json.Onnx.ExecutionProvider = $effectiveProvider

    $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $AppSettingsPath -Encoding UTF8

    Write-Output "PatchAppSettings: wrote MongoDB/Ollama/Onnx settings (ExecutionProvider=$effectiveProvider) to $AppSettingsPath"
}
catch
{
    Write-Error ("PatchAppSettings failed: " + $_.Exception.Message)
    exit 1
}
