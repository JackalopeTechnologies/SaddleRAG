#!/usr/bin/env bash
# install-mongodb.sh — installs MongoDB Community Edition on macOS or Linux
set -euo pipefail

if [[ "$(uname -s)" == "Darwin" ]]; then
    echo "Installing MongoDB via Homebrew..."
    if ! command -v brew >/dev/null 2>&1; then
        echo "ERROR: Homebrew is required on macOS. Install from https://brew.sh" >&2
        exit 1
    fi
    brew tap mongodb/brew
    brew install mongodb-community
    brew services start mongodb-community
    echo "MongoDB installed and started via Homebrew."
else
    echo "Installing MongoDB on Linux..."
    curl -fsSL https://www.mongodb.org/static/pgp/server-8.0.asc | sudo gpg -o /usr/share/keyrings/mongodb-server-8.0.gpg --dearmor
    echo "deb [ signed-by=/usr/share/keyrings/mongodb-server-8.0.gpg ] https://repo.mongodb.org/apt/ubuntu noble/mongodb-org/8.0 multiverse" | sudo tee /etc/apt/sources.list.d/mongodb-org-8.0.list
    sudo apt-get update -y
    sudo apt-get install -y mongodb-org
    sudo systemctl start mongod
    sudo systemctl enable mongod
    echo "MongoDB installed and started via systemd."
fi
