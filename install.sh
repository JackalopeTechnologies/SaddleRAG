#!/usr/bin/env bash
# install.sh
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.
# install.sh — turnkey bare-metal install for Ubuntu/Debian and Rocky/RHEL.
# Must run as root (sudo install.sh).
# Installs: .NET ASP.NET Core Runtime 10, MongoDB 8, Ollama, SaddleRAG.
# Registers SaddleRAG as a systemd service and runs --prewarm.

set -euo pipefail

# ── Colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()    { echo -e "${GREEN}[INFO]${NC} $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC} $*"; }
die()     { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

# ── Phase 1: Pre-flight ───────────────────────────────────────────────────────
[[ $EUID -ne 0 ]] && die "Run with sudo: sudo $0"

ARCH=$(uname -m)
[[ "$ARCH" != "x86_64" ]] && die "Only x86-64 is supported (detected: $ARCH)"

if command -v apt-get &>/dev/null; then
    PKG=apt
elif command -v dnf &>/dev/null; then
    PKG=dnf
else
    die "Unsupported distribution. Supported: Ubuntu/Debian (apt) and Rocky/RHEL (dnf)."
fi

info "Detected package manager: $PKG"

INSTALL_DIR=/opt/saddlerag
CONFIG_DIR=/etc/saddlerag
DATA_DIR=/var/lib/saddlerag
LOG_DIR=/var/log/saddlerag
SERVICE_USER=saddlerag
SYSTEMD_UNIT=/etc/systemd/system/saddlerag.service

if [[ -d "$INSTALL_DIR" ]]; then
    warn "Existing installation detected at $INSTALL_DIR."
    read -rp "Upgrade? [y/N] " answer
    [[ "${answer,,}" != "y" ]] && die "Aborted."
    systemctl stop saddlerag 2>/dev/null || true
fi

# ── Phase 2: System dependencies ─────────────────────────────────────────────
info "Installing .NET ASP.NET Core Runtime 10..."
if [[ "$PKG" == "apt" ]]; then
    wget -q https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release && echo "$VERSION_ID")/packages-microsoft-prod.deb \
        -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    rm /tmp/packages-microsoft-prod.deb
    apt-get update -q
    apt-get install -y aspnetcore-runtime-10.0
else
    dnf install -y dotnet-aspnet-10.0
fi

info "Installing MongoDB 8..."
if [[ "$PKG" == "apt" ]]; then
    curl -fsSL https://www.mongodb.org/static/pgp/server-8.0.asc \
        | gpg -o /usr/share/keyrings/mongodb-server-8.0.gpg --dearmor
    echo "deb [ arch=amd64,arm64 signed-by=/usr/share/keyrings/mongodb-server-8.0.gpg ] \
https://repo.mongodb.org/apt/ubuntu $(. /etc/os-release && echo "$UBUNTU_CODENAME")/mongodb-org/8.0 multiverse" \
        | tee /etc/apt/sources.list.d/mongodb-org-8.0.list
    apt-get update -q
    apt-get install -y mongodb-org
else
    cat > /etc/yum.repos.d/mongodb-org-8.0.repo <<'REPO'
[mongodb-org-8.0]
name=MongoDB Repository
baseurl=https://repo.mongodb.org/yum/redhat/9/mongodb-org/8.0/x86_64/
gpgcheck=1
enabled=1
gpgkey=https://www.mongodb.org/static/pgp/server-8.0.asc
REPO
    dnf install -y mongodb-org
fi
systemctl enable --now mongod

info "Installing Ollama..."
curl -fsSL https://ollama.com/install.sh | sh
systemctl enable --now ollama

# ── Phase 3: Install SaddleRAG ────────────────────────────────────────────────
info "Fetching latest SaddleRAG release..."
LATEST_TAG=$(curl -fsSL https://api.github.com/repos/JackalopeTechnologies/SaddleRAG/releases/latest \
    | grep '"tag_name"' | cut -d'"' -f4)
VERSION="${LATEST_TAG#v}"
TARBALL="SaddleRAG.Mcp-${VERSION}-linux-x64.tar.gz"
CHECKSUM="SaddleRAG.Mcp-${VERSION}-linux-x64.tar.gz.sha256"
BASE_URL="https://github.com/JackalopeTechnologies/SaddleRAG/releases/download/${LATEST_TAG}"

curl -fsSL "${BASE_URL}/${TARBALL}"  -o "/tmp/${TARBALL}"
curl -fsSL "${BASE_URL}/${CHECKSUM}" -o "/tmp/${CHECKSUM}"

info "Verifying checksum..."
pushd /tmp >/dev/null
sha256sum -c "${CHECKSUM}"
popd >/dev/null

info "Installing SaddleRAG ${VERSION}..."
id -u $SERVICE_USER &>/dev/null || useradd --system --no-create-home --shell /sbin/nologin $SERVICE_USER
install -d -o $SERVICE_USER -g $SERVICE_USER -m 755 "$INSTALL_DIR"
install -d -o $SERVICE_USER -g $SERVICE_USER -m 755 "$CONFIG_DIR"
install -d -o $SERVICE_USER -g $SERVICE_USER -m 750 "$DATA_DIR"
install -d -o $SERVICE_USER -g $SERVICE_USER -m 750 "$DATA_DIR/models"
install -d -o $SERVICE_USER -g $SERVICE_USER -m 750 "$LOG_DIR"

tar -xzf "/tmp/${TARBALL}" -C "$INSTALL_DIR" --strip-components=0
chown -R $SERVICE_USER:$SERVICE_USER "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/SaddleRAG.Mcp"

rm -f "/tmp/${TARBALL}" "/tmp/${CHECKSUM}"

info "Writing configuration..."
cat > "$INSTALL_DIR/appsettings.Production.json" <<APPSETTINGS
{
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://localhost:6100" } } },
  "MongoDB": {
    "ActiveProfile": "local",
    "Profiles": {
      "local": {
        "ConnectionString": "mongodb://localhost:27017",
        "DatabaseName": "SaddleRAG",
        "Description": "Local database"
      }
    }
  },
  "Ollama": { "Endpoint": "http://localhost:11434" },
  "Onnx": {
    "Enabled": true,
    "EmbeddingEnabled": true,
    "ModelsDir": "${DATA_DIR}/models"
  }
}
APPSETTINGS
chown $SERVICE_USER:$SERVICE_USER "$INSTALL_DIR/appsettings.Production.json"
chmod 640 "$INSTALL_DIR/appsettings.Production.json"

# ── Phase 4: Systemd registration ────────────────────────────────────────────
info "Registering systemd service..."
cat > "$SYSTEMD_UNIT" <<UNIT
[Unit]
Description=SaddleRAG MCP Documentation Server
After=network.target mongod.service ollama.service
Requires=mongod.service

[Service]
Type=simple
User=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/SaddleRAG.Mcp
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=-$CONFIG_DIR/env

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable saddlerag

# ── Phase 5: Prewarm (explicit model download) ────────────────────────────────
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Downloading ONNX models from HuggingFace and Ollama classification"
echo "  model (phi4-mini:3.8b). This is a one-time step."
echo "  Expected download: ~3 GB. Duration depends on connection speed."
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
ASPNETCORE_ENVIRONMENT=Production sudo -u $SERVICE_USER "$INSTALL_DIR/SaddleRAG.Mcp" --prewarm

# ── Phase 6: Start and verify ─────────────────────────────────────────────────
info "Starting SaddleRAG service..."
systemctl start saddlerag

info "Waiting for health check (up to 60 seconds)..."
for i in $(seq 1 60); do
    if curl -sf http://localhost:6100/health &>/dev/null; then
        break
    fi
    sleep 1
done

curl -sf http://localhost:6100/health &>/dev/null || die "SaddleRAG did not become healthy within 60 seconds. Check: journalctl -u saddlerag -n 50"

# ── Phase 7: Summary ──────────────────────────────────────────────────────────
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "  ${GREEN}SaddleRAG ${VERSION} installed successfully.${NC}"
echo ""
echo "  Server:    http://localhost:6100"
echo "  Logs:      journalctl -u saddlerag -f"
echo "  Config:    $CONFIG_DIR/appsettings.json"
echo "  Models:    $DATA_DIR/models"
echo "  Uninstall: sudo $INSTALL_DIR/uninstall.sh"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
