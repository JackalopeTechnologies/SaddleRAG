# detect-ollama.ps1 — exit 0=running, 1=installed-not-running, 2=not-found
$ErrorActionPreference = "SilentlyContinue"

try {
    $response = Invoke-WebRequest "http://localhost:11434" -UseBasicParsing -TimeoutSec 2
    if ($response.StatusCode -eq 200) {
        Write-Output '{"status":"running","port":11434}'
        exit 0
    }
} catch {}

$ollama = Get-Command ollama -ErrorAction SilentlyContinue
if ($ollama) {
    Write-Output "{`"status`":`"stopped`",`"path`":`"$($ollama.Source)`"}"
    exit 1
}

Write-Output '{"status":"not-found"}'
exit 2
