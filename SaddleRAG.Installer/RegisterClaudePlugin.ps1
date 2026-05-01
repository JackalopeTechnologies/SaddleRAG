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
