[CmdletBinding()]
param()

# Conditionally remove OLLAMA_KEEP_ALIVE on uninstall.
# Only removes the env var if BOTH:
#   1. the install-time marker is present (we set it during install), AND
#   2. the current value still matches what we set ('-1') — if the user
#      changed it after install, we leave it alone.
# The marker registry value is removed in either case so we don't leave
# breadcrumbs behind. Cleans up an empty registry key on the way out.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$EnvVarName   = 'OLLAMA_KEEP_ALIVE'
$OurValue     = '-1'
$RegistryPath = 'HKLM:\Software\Jackalope Technologies\SaddleRAG'
$ParentRegistryPath = 'HKLM:\Software\Jackalope Technologies'
$MarkerName   = 'OllamaKeepAliveSetByUs'

$marker = $null

if (Test-Path -LiteralPath $RegistryPath)
{
    $prop = Get-ItemProperty -LiteralPath $RegistryPath -Name $MarkerName -ErrorAction SilentlyContinue

    if ($null -ne $prop)
    {
        $marker = $prop.$MarkerName
    }
}

if ($marker -ne 1)
{
    Write-Output 'OllamaKeepAlive: marker not present; leaving env var alone'
}
else
{
    $current = [Environment]::GetEnvironmentVariable($EnvVarName, 'Machine')

    if ($current -eq $OurValue)
    {
        [Environment]::SetEnvironmentVariable($EnvVarName, $null, 'Machine')
        Write-Output "OllamaKeepAlive: removed (was '$OurValue')"
    }
    else
    {
        Write-Output "OllamaKeepAlive: current value is '$current', not the value we set; leaving alone"
    }

    Remove-ItemProperty -LiteralPath $RegistryPath -Name $MarkerName -ErrorAction SilentlyContinue
}

# Clean up empty registry keys we may have created at install time
if (Test-Path -LiteralPath $RegistryPath)
{
    $remainingValues = (Get-Item -LiteralPath $RegistryPath).Property

    if ($null -eq $remainingValues -or $remainingValues.Count -eq 0)
    {
        $remainingSubkeys = Get-ChildItem -LiteralPath $RegistryPath -ErrorAction SilentlyContinue

        if ($null -eq $remainingSubkeys -or $remainingSubkeys.Count -eq 0)
        {
            Remove-Item -LiteralPath $RegistryPath -ErrorAction SilentlyContinue
        }
    }
}

if (Test-Path -LiteralPath $ParentRegistryPath)
{
    $parentRemainingValues  = (Get-Item -LiteralPath $ParentRegistryPath).Property
    $parentRemainingSubkeys = Get-ChildItem -LiteralPath $ParentRegistryPath -ErrorAction SilentlyContinue

    if (($null -eq $parentRemainingValues -or $parentRemainingValues.Count -eq 0) -and
        ($null -eq $parentRemainingSubkeys -or $parentRemainingSubkeys.Count -eq 0))
    {
        Remove-Item -LiteralPath $ParentRegistryPath -ErrorAction SilentlyContinue
    }
}
