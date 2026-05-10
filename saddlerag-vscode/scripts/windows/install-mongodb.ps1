# install-mongodb.ps1 — installs MongoDB Community Edition on Windows via winget
$ErrorActionPreference = "Stop"

Write-Host "Installing MongoDB Community Edition..."
winget install --id MongoDB.Server --accept-package-agreements --accept-source-agreements

Write-Host "Starting MongoDB service..."
Start-Service -Name "MongoDB"

Write-Host "MongoDB installed and started."
