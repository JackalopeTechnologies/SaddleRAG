[CmdletBinding()]
param()

# Set OLLAMA_KEEP_ALIVE=-1 system-wide if (and only if) it isn't already set.
# Records a registry marker so the matching uninstall script knows we set it
# and can clean up cleanly. If the user already has a value, this script is a
# no-op and the user's preference is left untouched.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$EnvVarName   = 'OLLAMA_KEEP_ALIVE'
$OurValue     = '-1'
$RegistryPath = 'HKLM:\Software\Jackalope Technologies\SaddleRAG'
$MarkerName   = 'OllamaKeepAliveSetByUs'

$existing = [Environment]::GetEnvironmentVariable($EnvVarName, 'Machine')

if ([string]::IsNullOrEmpty($existing))
{
    [Environment]::SetEnvironmentVariable($EnvVarName, $OurValue, 'Machine')

    if (-not (Test-Path -LiteralPath $RegistryPath))
    {
        New-Item -Path $RegistryPath -Force | Out-Null
    }

    Set-ItemProperty -LiteralPath $RegistryPath -Name $MarkerName -Value 1 -Type DWord
    Write-Output "OllamaKeepAlive: set system-wide to '$OurValue' (marker recorded)"
}
else
{
    Write-Output "OllamaKeepAlive: already set to '$existing'; left unchanged"
}
