#!/usr/bin/env bash
# install-ollama.sh — installs Ollama on macOS or Linux
set -euo pipefail

echo "Installing Ollama..."
curl -fsSL https://ollama.com/install.sh | sh
echo "Ollama installed."
