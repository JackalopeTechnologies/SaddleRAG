# smoke-test.ps1 — validates a published SaddleRAG.Mcp binary on Windows
# Usage: .\scripts\smoke-test.ps1 [-Binary path] [-Port port]
param(
    [string]$Binary = ".\SaddleRAG.Mcp.exe",
    [int]$Port = 6100,
    [int]$TimeoutSecs = 60
)

$ErrorActionPreference = "Stop"

Write-Host "Starting $Binary on port $Port..."
$proc = Start-Process -FilePath $Binary -PassThru -WindowStyle Hidden

try
{
    Write-Host "Waiting for /health (timeout ${TimeoutSecs}s)..."
    $healthy = $false
    for ($i = 1; $i -le $TimeoutSecs; $i++)
    {
        try
        {
            $null = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -ErrorAction Stop
            Write-Host "  /health OK (after ${i}s)"
            $healthy = $true
            break
        }
        catch { Start-Sleep 1 }
    }

    if (-not $healthy)
    {
        Write-Error "FAIL: /health timed out after ${TimeoutSecs}s"
        exit 1
    }

    Write-Host "Checking /mcp..."
    $null = Invoke-WebRequest "http://localhost:$Port/mcp" -UseBasicParsing
    Write-Host "  /mcp OK"

    Write-Host "Checking /api/status..."
    $status = Invoke-WebRequest "http://localhost:$Port/api/status" -UseBasicParsing
    if ($status.Content -notmatch '"libraries"')
    {
        Write-Error "FAIL: /api/status missing 'libraries' key"
        exit 1
    }
    Write-Host "  /api/status OK"

    Write-Host "PASS: smoke test completed"
}
finally
{
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
