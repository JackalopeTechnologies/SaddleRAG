# install-ollama.ps1 — installs Ollama on Windows
$ErrorActionPreference = "Stop"

$installer = "$env:TEMP\OllamaSetup.exe"
Write-Host "Downloading Ollama installer..."
Invoke-WebRequest "https://ollama.com/download/OllamaSetup.exe" -OutFile $installer

Write-Host "Running installer..."
Start-Process -FilePath $installer -ArgumentList "/S" -Wait

Write-Host "Ollama installed."
