# Linux / Docker Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make SaddleRAG build and run on Linux (x86-64, CPU-only), ship a turnkey Docker image via CI on every tagged release, and provide a bare-metal install script for Ubuntu/Debian and Rocky/RHEL.

**Architecture:** Minimal code changes to `SaddleRAG.Mcp` enable cross-platform compilation; a multi-stage Dockerfile produces a self-contained linux-x64 image with Chromium baked in; a `docker-compose.yml` wires SaddleRAG + MongoDB + Ollama with all settings env-var injectable; a `install.sh` script mirrors the MSI workflow for bare-metal hosts. Models are never bundled — an explicit `--prewarm` step downloads them, identical to what the MSI custom action does on Windows.

**Tech Stack:** .NET 10 (ASP.NET Core), Docker / Docker Compose, GitHub Actions, MongoDB 8, Ollama, Microsoft.Playwright (Chromium), Serilog, bash.

**Spec:** `docs/superpowers/specs/2026-05-14-linux-docker-design.md`

---

## Pre-reading (do before any task)

Read these files once at session start — tasks reference them without repeating the read:

- `SaddleRAG.Mcp/Program.cs` lines 80–130 (Serilog setup + UseWindowsService call)
- `SaddleRAG.Mcp/SaddleRAG.Mcp.csproj` (current package references)
- `SaddleRAG.Mcp/appsettings.json` (Kestrel port, Ollama/Onnx/MongoDB config keys)
- `.github/workflows/build.yml` (existing CI structure)

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Modify | `SaddleRAG.Mcp/SaddleRAG.Mcp.csproj` | Conditional Windows-only packages |
| Modify | `SaddleRAG.Mcp/Program.cs` | `UseWindowsService` OS guard |
| Create | `Dockerfile` | Multi-stage linux-x64 CPU image |
| Create | `.dockerignore` | Keep build context lean |
| Create | `docker-compose.yml` | Three-service stack |
| Create | `warmup.sh` | Explicit model-download trigger |
| Create | `install.sh` | Bare-metal turnkey install |
| Create | `uninstall.sh` | Bare-metal teardown |
| Modify | `.github/workflows/build.yml` | `build-linux` + `docker` jobs |
| Modify | `README.md` | Linux / Docker section |

---

## Task 1: Verify Linux build baseline and fix compilation blockers

**Files:**
- Modify: `SaddleRAG.Mcp/SaddleRAG.Mcp.csproj`
- Modify: `SaddleRAG.Mcp/Program.cs` (line 124)

- [ ] **Step 1: Attempt the Linux build**

```bash
dotnet build SaddleRAG.Mcp/SaddleRAG.Mcp.csproj -c Release -r linux-x64 --self-contained true -p:UseGpu=false -p:TreatWarningsAsErrors=true
```

Expected: build succeeds or fails with specific errors. Record every error message.

- [ ] **Step 2: Guard `UseWindowsService` (known required change)**

`Program.cs` line 124 calls `builder.Host.UseWindowsService(...)` unconditionally.
`UseWindowsService` is a no-op on Linux at runtime but the call site may cause
linker warnings under `TreatWarningsAsErrors=true`. Wrap it:

```csharp
// Before (line 124):
builder.Host.UseWindowsService(options => { options.ServiceName = ServiceName; });

// After:
if (OperatingSystem.IsWindows())
    builder.Host.UseWindowsService(options => { options.ServiceName = ServiceName; });
```

- [ ] **Step 3: Fix any additional compilation errors from Step 1**

If `Serilog.Sinks.EventLog` or `Microsoft.Extensions.Hosting.WindowsServices`
caused errors, make them conditional in `SaddleRAG.Mcp.csproj`:

```xml
<PackageReference Include="Serilog.Sinks.EventLog"
                  Version="4.0.0"
                  Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices"
                  Version="10.0.8"
                  Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
```

If these packages are made conditional, also wrap their usages in Program.cs with
`#if WINDOWS` or move them inside `if (OperatingSystem.IsWindows())` blocks.
The `using Microsoft.Extensions.Hosting.WindowsServices;` at line 17 must be
removed or wrapped with `#if` if the package is conditional.

If `Serilog.Sinks.EventLog` had no compile errors (it was already runtime-guarded),
skip this step.

- [ ] **Step 4: Re-run build until clean**

```bash
dotnet build SaddleRAG.Mcp/SaddleRAG.Mcp.csproj -c Release -r linux-x64 --self-contained true -p:UseGpu=false -p:TreatWarningsAsErrors=true
```

Expected: exit code 0, zero warnings, zero errors.

- [ ] **Step 5: Run tests**

```bash
dotnet test SaddleRAG.slnx -c Release --filter "Category!=Integration"
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add SaddleRAG.Mcp/SaddleRAG.Mcp.csproj SaddleRAG.Mcp/Program.cs
git commit -F - <<'EOF'
feat: guard Windows-only hosting calls for Linux build

UseWindowsService() wrapped in OperatingSystem.IsWindows(). EventLog and
WindowsServices packages made conditional on win-x64 RID if the baseline
Linux build required it.
EOF
```

---

## Task 2: Full linux-x64 publish verification

**Files:**
- No file changes — verification only

- [ ] **Step 1: Publish linux-x64 self-contained**

```bash
dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:UseGpu=false \
  -p:TreatWarningsAsErrors=true \
  -o ./publish-linux-verify
```

Expected: exit code 0, output directory contains `SaddleRAG.Mcp` (no extension) executable.

- [ ] **Step 2: Confirm Playwright script is present**

```bash
ls ./publish-linux-verify/playwright*
```

Expected: at least one file — either `playwright` (shell script, no extension) or
`playwright.ps1`. Note the exact filename — you will use it in Task 4 (Dockerfile).

- [ ] **Step 3: Confirm key config files are present**

```bash
ls ./publish-linux-verify/appsettings.json
```

Expected: file exists.

- [ ] **Step 4: Clean up**

```bash
rm -rf ./publish-linux-verify
```

No commit needed — verification only.

---

## Task 3: `.dockerignore`

**Files:**
- Create: `.dockerignore`

- [ ] **Step 1: Create `.dockerignore`**

```
.git
.github
.claude
*.md
docs/
SaddleRAG.Tests/
SaddleRAG.Installer/
SaddleRAG.Installer.Logic/
**/.vs/
**/bin/
**/obj/
publish-linux-verify/
artifacts/
*.msi
*.user
*.suo
```

- [ ] **Step 2: Commit**

```bash
git add .dockerignore
git commit -F - <<'EOF'
chore: add .dockerignore for lean Docker build context
EOF
```

---

## Task 4: Dockerfile

**Files:**
- Create: `Dockerfile`

> **Before writing:** confirm the Playwright script name from Task 2 Step 2.
> If the file is `playwright` (no extension), use `./playwright install chromium --with-deps`.
> If it is `playwright.ps1`, install `powershell` in the runtime stage and use
> `pwsh playwright.ps1 install chromium --with-deps`. The steps below assume
> `playwright` (shell script, the common case for self-contained linux-x64 publishes).

- [ ] **Step 1: Create `Dockerfile`**

```dockerfile
# Dockerfile

# ── Stage 1: build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj \
        -c Release \
        -r linux-x64 \
        --self-contained true \
        -p:UseGpu=false \
        -p:TreatWarningsAsErrors=true \
        -o /app/publish

# ── Stage 2: runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Playwright / Chromium system dependencies (bookworm-slim base)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        curl \
        libatk1.0-0 \
        libatk-bridge2.0-0 \
        libcups2 \
        libdrm2 \
        libgbm1 \
        libgtk-3-0 \
        libnspr4 \
        libnss3 \
        libxcomposite1 \
        libxdamage1 \
        libxfixes3 \
        libxkbcommon0 \
        libxrandr2 \
        libasound2 \
        xvfb \
        fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Install Playwright browsers using the script shipped with the publish output
RUN chmod +x playwright \
    && ./playwright install chromium \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 6100

HEALTHCHECK --interval=30s --timeout=10s --start-period=300s --retries=5 \
    CMD curl -sf http://localhost:6100/health || exit 1

ENTRYPOINT ["./SaddleRAG.Mcp"]
```

- [ ] **Step 2: Build and verify the image**

```bash
docker build -t saddlerag:local-test .
```

Expected: build completes, no errors. Note the final image size in the output.

- [ ] **Step 3: Smoke-test the image starts (no services)**

```bash
docker run --rm saddlerag:local-test --help 2>&1 | head -20
```

Expected: the process starts and exits (it will error on missing MongoDB/Ollama —
that is fine for this step, we just want to confirm the binary executes).

- [ ] **Step 4: Clean up test image**

```bash
docker rmi saddlerag:local-test
```

- [ ] **Step 5: Commit**

```bash
git add Dockerfile
git commit -F - <<'EOF'
feat: add multi-stage Dockerfile for linux-x64 CPU-only image

SDK stage publishes with UseGpu=false. Runtime stage (aspnet:10.0) installs
Chromium via Playwright for JS-heavy scraping. Models are not bundled;
warmup.sh triggers explicit first-run downloads.
EOF
```

---

## Task 5: `docker-compose.yml` and `warmup.sh`

**Files:**
- Create: `docker-compose.yml`
- Create: `warmup.sh`

> **Before writing:** read `SaddleRAG.Mcp/appsettings.json` to confirm the exact
> config key paths for MongoDB and Ollama endpoint. The env var convention is
> `SADDLERAG__Section__Key` using double-underscore. Check:
> - MongoDB connection string key (look for `MongoDB.Profiles.local.ConnectionString` or similar)
> - Ollama endpoint key (look for `Ollama.Endpoint`)
> - Onnx enabled keys (`Onnx.Enabled`, `Onnx.EmbeddingEnabled`)

- [ ] **Step 1: Create `docker-compose.yml`**

```yaml
# docker-compose.yml
# One-command SaddleRAG stack: server + MongoDB + Ollama.
# Models are NOT downloaded automatically. Run ./warmup.sh once after first start.

services:
  saddlerag:
    image: ghcr.io/jackalopetechnologies/saddlerag:latest
    ports:
      - "6100:6100"
    environment:
      SADDLERAG__Onnx__Enabled: "true"
      SADDLERAG__Onnx__EmbeddingEnabled: "true"
      SADDLERAG__Onnx__ModelsDir: /data/models
      SADDLERAG__Ollama__Endpoint: http://ollama:11434
      SADDLERAG__MongoDB__Profiles__local__ConnectionString: mongodb://mongo:27017
    volumes:
      - onnx-models:/data/models
    depends_on:
      - mongo
      - ollama
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-sf", "http://localhost:6100/health"]
      interval: 30s
      timeout: 10s
      retries: 5
      start_period: 5m

  mongo:
    image: mongo:8
    volumes:
      - mongo-data:/data/db
    restart: unless-stopped

  ollama:
    image: ollama/ollama:latest
    volumes:
      - ollama-data:/root/.ollama
    restart: unless-stopped

volumes:
  mongo-data:
  onnx-models:
  ollama-data:
```

> **Note:** If the config key paths differ from the above, adjust the env var names
> to match. The double-underscore maps to nested JSON: `Ollama__Endpoint` →
> `appsettings.json["Ollama"]["Endpoint"]`.

- [ ] **Step 2: Verify compose syntax**

```bash
docker compose config
```

Expected: prints the resolved config with no errors.

- [ ] **Step 3: Create `warmup.sh`**

```bash
#!/usr/bin/env bash
# warmup.sh — run once after first `docker compose up -d` to download models.
# ONNX models (~200-500 MB) come from HuggingFace.
# Ollama models (nomic-embed-text + phi4-mini:3.8b, ~3 GB total) come from Ollama registry.
# Models are stored in named volumes and will NOT be re-downloaded on restart.
# The recon model (phi4:14b, ~8 GB) is not downloaded here — opt-in only:
#   docker compose exec ollama ollama pull phi4:14b

set -euo pipefail

echo ""
echo "SaddleRAG model download"
echo "========================"
echo "Downloading ONNX models from HuggingFace and Ollama models from Ollama registry."
echo "This is a one-time step. Duration depends on your connection speed (typically 5-15 min)."
echo ""

docker compose exec saddlerag ./SaddleRAG.Mcp --prewarm

echo ""
echo "Warmup complete. SaddleRAG is ready."
echo "Access the admin UI at: http://localhost:6100"
echo ""
```

- [ ] **Step 4: Make warmup.sh executable**

```bash
chmod +x warmup.sh
git update-index --chmod=+x warmup.sh
```

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml warmup.sh
git commit -F - <<'EOF'
feat: add docker-compose.yml and warmup.sh

Three-service stack (SaddleRAG + MongoDB + Ollama) with named volumes for
data persistence. Models are not bundled; warmup.sh runs --prewarm for
explicit first-run download, matching the MSI installer behaviour.
EOF
```

---

## Task 6: `install.sh` (bare-metal)

**Files:**
- Create: `install.sh`

- [ ] **Step 1: Create `install.sh`**

```bash
#!/usr/bin/env bash
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
# ASP.NET Core self-contained deploys use the binary's directory as the content root.
# appsettings.production.json in the same directory overrides the shipped defaults.
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
```

- [ ] **Step 2: Make executable and verify shell syntax**

```bash
chmod +x install.sh
git update-index --chmod=+x install.sh
bash -n install.sh
```

Expected: `bash -n` exits 0 (syntax valid).

- [ ] **Step 3: Commit**

```bash
git add install.sh
git commit -F - <<'EOF'
feat: add bare-metal install.sh for Ubuntu/Debian and Rocky/RHEL

Installs .NET runtime, MongoDB 8, Ollama, and SaddleRAG from GitHub
release. Registers systemd service, runs --prewarm for explicit model
download, verifies health endpoint.
EOF
```

---

## Task 7: `uninstall.sh`

**Files:**
- Create: `uninstall.sh`

- [ ] **Step 1: Create `uninstall.sh`**

```bash
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
```

- [ ] **Step 2: Verify syntax and commit**

```bash
chmod +x uninstall.sh
git update-index --chmod=+x uninstall.sh
bash -n uninstall.sh
git add uninstall.sh
git commit -F - <<'EOF'
feat: add uninstall.sh for bare-metal teardown

Stops/disables systemd service, removes binaries and config.
Prompts before deleting downloaded models (re-download is expensive).
Does not remove MongoDB, Ollama, or .NET runtime.
EOF
```

---

## Task 8: CI — `build-linux` job

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Read current `build.yml`**

Read `.github/workflows/build.yml` in full. Note the exact structure of the
existing `build` job — you will rename it to `build-windows` and add
`build-linux` as a parallel job.

- [ ] **Step 2: Add `packages: write` permission and rename existing job**

In the `permissions` block, add `packages: write`.
Rename `jobs.build:` to `jobs.build-windows:`.

```yaml
permissions:
  contents: write
  packages: write

jobs:
  build-windows:
    runs-on: windows-latest
    # ... rest of existing job unchanged ...
```

- [ ] **Step 3: Add `build-linux` job**

Add the following job after `build-windows`. Insert the correct `version` step
output references to match the existing job's pattern (the existing job uses
`steps.version.outputs.PackageVersion` — replicate that exactly).

```yaml
  build-linux:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v5

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '10.x'

      - name: Determine version
        id: version
        run: |
          if [[ "${GITHUB_REF}" == refs/tags/v* ]]; then
            VERSION="${GITHUB_REF#refs/tags/v}"
          else
            VERSION="0.0.0"
          fi
          echo "PackageVersion=${VERSION}" >> "$GITHUB_OUTPUT"
          echo "Version: ${VERSION}"

      - name: Restore
        run: dotnet restore SaddleRAG.slnx

      - name: Build
        run: dotnet build SaddleRAG.slnx --configuration Release --no-restore -p:TreatWarningsAsErrors=true -p:UseGpu=false -p:Version=${{ steps.version.outputs.PackageVersion }}

      - name: Test
        run: dotnet test SaddleRAG.slnx --configuration Release --no-build --filter "Category!=Integration"

      - name: Publish
        run: dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj --configuration Release --runtime linux-x64 --self-contained true -p:UseGpu=false -p:Version=${{ steps.version.outputs.PackageVersion }} --output ./artifacts/linux/publish

      - name: Package tarball
        run: |
          VERSION="${{ steps.version.outputs.PackageVersion }}"
          TARBALL="SaddleRAG.Mcp-${VERSION}-linux-x64.tar.gz"
          tar -czf "./artifacts/${TARBALL}" -C ./artifacts/linux/publish .
          sha256sum "./artifacts/${TARBALL}" > "./artifacts/${TARBALL}.sha256"
          echo "TARBALL=${TARBALL}" >> "$GITHUB_ENV"

      - name: Upload linux artifact
        uses: actions/upload-artifact@v5
        with:
          name: SaddleRAG.Mcp-linux-${{ steps.version.outputs.PackageVersion }}
          path: |
            ./artifacts/SaddleRAG.Mcp-${{ steps.version.outputs.PackageVersion }}-linux-x64.tar.gz
            ./artifacts/SaddleRAG.Mcp-${{ steps.version.outputs.PackageVersion }}-linux-x64.tar.gz.sha256

      - name: Upload to GitHub Release
        if: startsWith(github.ref, 'refs/tags/v')
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          VERSION="${{ steps.version.outputs.PackageVersion }}"
          gh release upload "v${VERSION}" \
            "./artifacts/SaddleRAG.Mcp-${VERSION}-linux-x64.tar.gz" \
            "./artifacts/SaddleRAG.Mcp-${VERSION}-linux-x64.tar.gz.sha256"
```

> **Race condition note:** `gh release upload` requires the release to exist first.
> The release is created by `build-windows`. To avoid a race on tag pushes, add
> `needs: [build-windows]` at the `build-linux` **job level** (not inside a step —
> `needs` is a job-level key in GitHub Actions). This serialises the two jobs on
> tags but they still run in parallel on PRs if you use a conditional:
>
> ```yaml
>   build-linux:
>     runs-on: ubuntu-latest
>     needs: ${{ startsWith(github.ref, 'refs/tags/v') && fromJSON('["build-windows"]') || fromJSON('[]') }}
> ```
>
> Alternatively, split the upload into a third job `upload-linux` that
> `needs: [build-windows, build-linux]`. Either approach is acceptable.

- [ ] **Step 4: Validate YAML**

```bash
python3 -c "import yaml, sys; yaml.safe_load(open('.github/workflows/build.yml'))" && echo "YAML valid"
```

Expected: prints `YAML valid`.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/build.yml
git commit -F - <<'EOF'
ci: add build-linux job for linux-x64 CPU-only publish

Runs in parallel with build-windows. Produces a self-contained
linux-x64 tarball + SHA256 checksum. On tag, uploads both to the
GitHub Release alongside the MSI.
EOF
```

---

## Task 9: CI — `docker` job

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Add `docker` job after `build-linux`**

```yaml
  docker:
    runs-on: ubuntu-latest
    needs: [build-linux]

    steps:
      - uses: actions/checkout@v5

      - name: Determine version
        id: version
        run: |
          if [[ "${GITHUB_REF}" == refs/tags/v* ]]; then
            VERSION="${GITHUB_REF#refs/tags/v}"
          else
            VERSION="0.0.0"
          fi
          echo "PackageVersion=${VERSION}" >> "$GITHUB_OUTPUT"

      - name: Download linux artifact
        uses: actions/download-artifact@v5
        with:
          name: SaddleRAG.Mcp-linux-${{ steps.version.outputs.PackageVersion }}
          path: ./artifacts

      - name: Extract publish output for Docker
        run: |
          VERSION="${{ steps.version.outputs.PackageVersion }}"
          mkdir -p ./artifacts/linux/publish
          tar -xzf "./artifacts/SaddleRAG.Mcp-${VERSION}-linux-x64.tar.gz" \
              -C ./artifacts/linux/publish

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Log in to ghcr.io
        if: startsWith(github.ref, 'refs/tags/v')
        uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build Docker image (PR/push — no push)
        if: "!startsWith(github.ref, 'refs/tags/v')"
        uses: docker/build-push-action@v6
        with:
          context: .
          push: false
          tags: ghcr.io/jackalopetechnologies/saddlerag:dev

      - name: Build and push Docker image (tag)
        if: startsWith(github.ref, 'refs/tags/v')
        uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          tags: |
            ghcr.io/jackalopetechnologies/saddlerag:${{ steps.version.outputs.PackageVersion }}
            ghcr.io/jackalopetechnologies/saddlerag:latest
```

> **Note:** The Dockerfile as written does a full `dotnet publish` in the build
> stage. To reuse the artifact from `build-linux` instead of rebuilding, modify the
> Dockerfile to accept a `--build-arg PUBLISH_DIR` pointing at the pre-built output,
> or add a second Dockerfile stage that skips the SDK step. This is an optimization —
> for the first implementation, rebuilding in Docker is acceptable and simpler.

- [ ] **Step 2: Validate YAML**

```bash
python3 -c "import yaml, sys; yaml.safe_load(open('.github/workflows/build.yml'))" && echo "YAML valid"
```

Expected: `YAML valid`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/build.yml
git commit -F - <<'EOF'
ci: add docker build/push job

Builds image on every PR (validates Dockerfile). On tag, pushes
:version and :latest to ghcr.io/jackalopetechnologies/saddlerag.
EOF
```

---

## Task 10: README — Linux / Docker section

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Read current README**

Read `README.md` in full. Find the appropriate location to insert the Linux/Docker
section (likely after the Windows install section, before or after Contributing).

- [ ] **Step 2: Add Linux / Docker section**

Insert the following section at the chosen location. Adjust heading level to match
the existing document style:

```markdown
## Linux / Docker

### Docker (recommended)

Requires Docker with Compose. Tested on Ubuntu 22.04+.

**Start the stack:**
```bash
docker compose up -d
```

**Download models (one-time, ~3 GB):**
```bash
./warmup.sh
```

On first run, `warmup.sh` downloads ONNX embedding and reranker models from
HuggingFace and the Ollama classification model (`phi4-mini:3.8b`). Models are
stored in named Docker volumes and are not re-downloaded on restart.

The optional recon model (`phi4:14b`, ~8 GB) can be pulled separately:
```bash
docker compose exec ollama ollama pull phi4:14b
```

**Access:** `http://localhost:6100`

**Logs:** `docker compose logs -f saddlerag`

**Stop:** `docker compose down` (data preserved). `docker compose down -v` deletes
all volumes including downloaded models — use with caution.

### Bare-metal (Ubuntu/Debian or Rocky/RHEL)

```bash
curl -fsSL https://github.com/JackalopeTechnologies/SaddleRAG/releases/latest/download/install.sh | sudo bash
```

The script installs .NET ASP.NET Core Runtime 10, MongoDB 8, Ollama, and
SaddleRAG. It registers a systemd service and downloads models during install
(same prewarm step as the Windows MSI).

**Uninstall:** `sudo /opt/saddlerag/uninstall.sh`

### Model warmup behaviour

SaddleRAG downloads models on first start, not at image build time. This applies
to both Docker and bare-metal:

| Platform | When models download |
|----------|---------------------|
| Windows (MSI) | During MSI install (prewarm custom action) |
| Docker | When you run `./warmup.sh` after `docker compose up -d` |
| Bare-metal Linux | During `install.sh` (automatic) |

Warmup sequence (logged to stdout / `journalctl`):

```
[Warmup] MongoDB profiles discovered
[Warmup] Ollama bootstrap — pulls phi4-mini:3.8b if absent
[Warmup] ONNX models ready — downloads nomic-embed-text-v1.5 + mxbai-rerank-base-v1 from HuggingFace
[Warmup] Vector indices loaded
[Warmup] Full pipeline warm
```
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -F - <<'EOF'
docs: add Linux/Docker install section to README

Covers Docker Compose quick-start, bare-metal install.sh, and warmup
model-download behaviour across all platforms.
EOF
```

---

## Task 11: Open PR

- [ ] **Step 1: Push branch**

```bash
git push -u origin HEAD
```

- [ ] **Step 2: Create PR**

```bash
gh pr create \
  --base master \
  --title "feat: Linux/Docker support (CPU-only, turnkey install)" \
  --body "$(cat <<'EOF'
## Summary

- Minimal portability fixes to `SaddleRAG.Mcp` for Linux cross-compilation
- Multi-stage `Dockerfile` with Chromium baked in (CPU-only, no GPU dependencies)
- `docker-compose.yml` with SaddleRAG + MongoDB + Ollama; all endpoints env-var injectable
- `warmup.sh` for explicit model download matching the MSI prewarm behaviour
- `install.sh` / `uninstall.sh` for bare-metal Ubuntu/Debian and Rocky/RHEL
- CI: `build-linux` job (parallel with `build-windows`) produces linux-x64 tarball + SHA256
- CI: `docker` job builds image on every PR, pushes `:version` + `:latest` to `ghcr.io` on tag

## Test plan

- [ ] `dotnet build SaddleRAG.slnx -r linux-x64 -p:UseGpu=false -p:TreatWarningsAsErrors=true` passes
- [ ] `dotnet test SaddleRAG.slnx --filter "Category!=Integration"` passes
- [ ] `docker build -t saddlerag:test .` succeeds
- [ ] `docker compose config` validates cleanly
- [ ] `bash -n install.sh` and `bash -n uninstall.sh` pass syntax check
- [ ] CI `build-linux` job passes on the PR
- [ ] CI `docker` job builds (does not push) on the PR
EOF
)"
```

---

## Continuation prompt (for a fresh session)

Paste this into a new Claude Code session to resume implementation:

---

```
I have a completed design spec and implementation plan for adding Linux/Docker support to SaddleRAG. Please implement it.

Spec:    docs/superpowers/specs/2026-05-14-linux-docker-design.md
Plan:    docs/superpowers/plans/2026-05-14-linux-docker.md

Use the superpowers:subagent-driven-development skill to execute the plan task by task.

Key context:
- We are in the worktree at the current working directory on branch claude/fervent-lamport-716049
- The goal is Linux x86-64 CPU-only support + Docker image built as part of CI on every tagged release
- Models (ONNX from HuggingFace, Ollama models) are downloaded by an explicit --prewarm step, never bundled
- docker-compose.yml ships three services: SaddleRAG + MongoDB + Ollama; warmup.sh triggers the first-run download
- install.sh targets Ubuntu/Debian (apt) and Rocky/RHEL (dnf); it mirrors the MSI by running --prewarm before starting the systemd service
- The Windows MSI build and release process is unchanged; the Linux build runs as a parallel CI job
- Read Program.cs lines 80-130 and SaddleRAG.Mcp.csproj before starting Task 1 to understand the current Serilog and hosting setup

Do not commit directly to master. All work stays on the current branch.
```

---
