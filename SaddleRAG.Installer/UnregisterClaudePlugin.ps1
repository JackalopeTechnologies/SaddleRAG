# UnregisterClaudePlugin.ps1
# Removes plugin files from ~/.claude/plugins/saddlerag and removes the MCP
# server entry from ~/.claude/settings.json (Claude Code CLI) and from
# %APPDATA%\Claude\claude_desktop_config.json (Claude Desktop).
# Runs as the installing user.

$dst = Join-Path $env:USERPROFILE '.claude\plugins\saddlerag'
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }

# UTF-8 WITHOUT BOM. PowerShell 5.1 (powershell.exe) writes a BOM when using
# `Set-Content -Encoding UTF8`, which Claude Desktop's JSON parser rejects.
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# --- Helper: remove mcpServers.saddlerag from a JSON settings file ---
function Remove-SaddleRagEntry
{
    param([string]$FilePath, $Encoding)

    if (-not (Test-Path $FilePath)) { return }

    $s = Get-Content $FilePath -Raw | ConvertFrom-Json

    $hasMcp = $s.PSObject.Properties | Where-Object { $_.Name -eq 'mcpServers' }
    if (-not $hasMcp) { return }

    $hasSaddle = $s.mcpServers.PSObject.Properties | Where-Object { $_.Name -eq 'saddlerag' }
    if (-not $hasSaddle) { return }

    $s.mcpServers.PSObject.Properties.Remove('saddlerag')
    [System.IO.File]::WriteAllText($FilePath, ($s | ConvertTo-Json -Depth 10), $Encoding)
}

# Claude Code CLI
Remove-SaddleRagEntry -FilePath (Join-Path $env:USERPROFILE '.claude\settings.json') -Encoding $Utf8NoBom

# Claude Desktop (%APPDATA% is not populated in impersonated deferred CAs; derive from USERPROFILE)
Remove-SaddleRagEntry -FilePath (Join-Path $env:USERPROFILE 'AppData\Roaming\Claude\claude_desktop_config.json') -Encoding $Utf8NoBom
