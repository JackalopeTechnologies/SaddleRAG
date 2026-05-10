# RegisterClaudePlugin.ps1
# Copies plugin files to ~/.claude/plugins/saddlerag and adds the MCP server
# entry to ~/.claude/settings.json (Claude Code CLI - native HTTP) and to
# %APPDATA%\Claude\claude_desktop_config.json (Claude Desktop - stdio bridge
# via mcp-remote because Claude Desktop's config format only supports stdio).
# Runs as the installing user (impersonated).
# $PSScriptRoot is the MCPFOLDER where this script was installed.

$src = Join-Path $PSScriptRoot 'plugin'
$dst = Join-Path $env:USERPROFILE '.claude\plugins\saddlerag'

if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force

# UTF-8 WITHOUT BOM. PowerShell 5.1 (powershell.exe) writes a BOM when using
# `Set-Content -Encoding UTF8`, which Claude Desktop's JSON parser rejects.
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# --- Helper: ensure file exists with empty mcpServers, return parsed object ---
function Get-OrCreateConfig
{
    param([string]$FilePath)

    $dir = Split-Path $FilePath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    if (Test-Path $FilePath)
    {
        $s = Get-Content $FilePath -Raw | ConvertFrom-Json
    }
    else
    {
        $s = New-Object PSObject
    }

    $hasMcp = $s.PSObject.Properties | Where-Object { $_.Name -eq 'mcpServers' }
    if (-not $hasMcp)
    {
        Add-Member -InputObject $s -MemberType NoteProperty -Name 'mcpServers' -Value (New-Object PSObject)
    }

    return $s
}

# --- Claude Code CLI: native HTTP MCP entry ---
$cliPath = Join-Path $env:USERPROFILE '.claude\settings.json'
$cli = Get-OrCreateConfig -FilePath $cliPath

$cliEntry = New-Object PSObject
Add-Member -InputObject $cliEntry -MemberType NoteProperty -Name 'type'    -Value 'http'
Add-Member -InputObject $cliEntry -MemberType NoteProperty -Name 'url'     -Value 'http://localhost:6100/mcp'
Add-Member -InputObject $cliEntry -MemberType NoteProperty -Name 'timeout' -Value 300
Add-Member -InputObject $cli.mcpServers -MemberType NoteProperty -Name 'saddlerag' -Value $cliEntry -Force

# --- Pre-approve SaddleRAG read-only MCP tools so Claude Code does not
#     prompt the user per-call for safe lookups. Mutating tools
#     (scrape_docs, delete_library, cleanup_*, rescrub_library, etc.)
#     intentionally still prompt — those affect persistent state.
$saddleReadOnlyTools = @(
    'mcp__saddlerag__search_docs',
    'mcp__saddlerag__get_class_reference',
    'mcp__saddlerag__get_library_overview',
    'mcp__saddlerag__get_library_health',
    'mcp__saddlerag__get_dashboard_index',
    'mcp__saddlerag__get_server_logs',
    'mcp__saddlerag__get_version_changes',
    'mcp__saddlerag__get_job_status',
    'mcp__saddlerag__get_scrape_status',
    'mcp__saddlerag__get_rescrub_status',
    'mcp__saddlerag__list_libraries',
    'mcp__saddlerag__list_pages',
    'mcp__saddlerag__list_symbols',
    'mcp__saddlerag__list_excluded_symbols',
    'mcp__saddlerag__list_jobs',
    'mcp__saddlerag__list_scrape_jobs',
    'mcp__saddlerag__list_rescrub_jobs',
    'mcp__saddlerag__list_profiles',
    'mcp__saddlerag__inspect_scrape',
    'mcp__saddlerag__recon_library'
)

$hasPerms = $cli.PSObject.Properties | Where-Object { $_.Name -eq 'permissions' }
if (-not $hasPerms)
{
    Add-Member -InputObject $cli -MemberType NoteProperty -Name 'permissions' -Value (New-Object PSObject)
}

$hasAllow = $cli.permissions.PSObject.Properties | Where-Object { $_.Name -eq 'allow' }
if (-not $hasAllow)
{
    Add-Member -InputObject $cli.permissions -MemberType NoteProperty -Name 'allow' -Value @()
}

$existing = @($cli.permissions.allow)
$toAdd = $saddleReadOnlyTools | Where-Object { $existing -notcontains $_ }
if ($toAdd)
{
    $cli.permissions.allow = @($existing + $toAdd)
}

[System.IO.File]::WriteAllText($cliPath, ($cli | ConvertTo-Json -Depth 10), $Utf8NoBom)

# --- Claude Desktop: stdio bridge via mcp-remote (only stdio is supported) ---
# %APPDATA% is not populated in impersonated deferred CAs; derive from USERPROFILE.
$desktopPath = Join-Path $env:USERPROFILE 'AppData\Roaming\Claude\claude_desktop_config.json'
$desktop = Get-OrCreateConfig -FilePath $desktopPath

$desktopEntry = New-Object PSObject
Add-Member -InputObject $desktopEntry -MemberType NoteProperty -Name 'command' -Value 'npx'
Add-Member -InputObject $desktopEntry -MemberType NoteProperty -Name 'args'    -Value @('-y', 'mcp-remote@latest', 'http://localhost:6100/mcp', '--allow-http')
Add-Member -InputObject $desktop.mcpServers -MemberType NoteProperty -Name 'saddlerag' -Value $desktopEntry -Force

[System.IO.File]::WriteAllText($desktopPath, ($desktop | ConvertTo-Json -Depth 10), $Utf8NoBom)
