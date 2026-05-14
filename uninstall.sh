#!/usr/bin/env bash
# uninstall.sh — removes a bare-metal SaddleRAG installation.
# Does NOT remove MongoDB, Ollama, or .NET runtime (shared system deps).
# By default preserves downloaded models (/var/lib/saddlerag/models/).

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info() { echo -e "${GREEN}[INFO]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
die()  { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

[[ $EUID -ne 0 ]] && die "Run with sudo: sudo $0"

INSTALL_DIR=/opt/saddlerag
CONFIG_DIR=/etc/saddlerag
DATA_DIR=/var/lib/saddlerag
LOG_DIR=/var/log/saddlerag
SERVICE_USER=saddlerag

info "Stopping and disabling SaddleRAG service..."
systemctl stop saddlerag 2>/dev/null || true
systemctl disable saddlerag 2>/dev/null || true
rm -f /etc/systemd/system/saddlerag.service
systemctl daemon-reload

info "Removing binaries and configuration..."
rm -rf "$INSTALL_DIR"
rm -rf "$CONFIG_DIR"
rm -rf "$LOG_DIR"

echo ""
warn "Downloaded models are in $DATA_DIR/models (~3 GB)."
warn "Deleting them means re-downloading on next install."
read -rp "Delete models? [y/N] " answer
if [[ "${answer,,}" == "y" ]]; then
    rm -rf "$DATA_DIR"
    info "Models deleted."
else
    info "Models preserved at $DATA_DIR/models."
fi

info "Removing service user..."
id -u $SERVICE_USER &>/dev/null && userdel $SERVICE_USER || true

echo ""
echo -e "  ${GREEN}SaddleRAG uninstalled.${NC}"
echo "  MongoDB, Ollama, and .NET runtime were NOT removed."
echo ""
