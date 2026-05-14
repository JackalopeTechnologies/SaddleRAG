# Linux / Docker Support Design

**Date:** 2026-05-14
**Status:** Approved

## Goal

Make SaddleRAG build and run on Linux (x86-64, CPU-only) and ship a turnkey Docker
image as part of every tagged release. The Windows MSI release process is unchanged.

## Licensing note

Windows container images carry Microsoft Windows Server licensing costs even for
base images. Linux container images (Debian-based `aspnet:10.0`) are free. All
third-party models (ONNX from HuggingFace, Ollama models from the Ollama registry)
are downloaded explicitly by the user via a prewarm step — never bundled into the
image or auto-pulled at container start.

---

## Deliverables

| # | Deliverable | What it is |
|---|-------------|------------|
| 1 | Linux portability fixes | Minimal C# / config changes to make `SaddleRAG.Mcp` compile and run on Linux |
| 2 | `Dockerfile` | Multi-stage image: SDK build → slim runtime, Chromium baked in |
| 3 | `docker-compose.yml` | SaddleRAG + MongoDB + Ollama, all env-var overridable |
| 4 | `warmup.sh` | One-liner wrapper for the explicit prewarm step |
| 5 | `install.sh` | Bare-metal turnkey script for Ubuntu/Debian and Rocky/RHEL |
| 6 | `uninstall.sh` | Reversal script for bare-metal installs |
| 7 | CI additions | Linux build job + Docker build/push job in `build.yml` |

---

## Section 1 — Linux Portability Fixes

### Code changes

**`SaddleRAG.Mcp.csproj`**

Make two Windows-only packages conditional on the `win-x64` RID; add cross-platform
logging sinks unconditionally:

```xml
<!-- Windows-only — keep off Linux build -->
<PackageReference Include="Serilog.Sinks.EventLog"
                  Version="4.0.0"
                  Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices"
                  Version="10.0.8"
                  Condition="'$(RuntimeIdentifier)' == 'win-x64'" />

<!-- Serilog.Sinks.Console is already a transitive dep of Serilog.AspNetCore — no explicit ref needed -->
<!-- Add Serilog.Sinks.File explicitly; match version to the Serilog ecosystem version in use -->
<PackageReference Include="Serilog.Sinks.File" Version="..." />
```

**`Program.cs`**

Wrap the two Windows-specific hosting calls with OS guards:

```csharp
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
    // Windows Event Log sink setup
}

if (OperatingSystem.IsWindows())
{
    host.UseWindowsService();
}
```

**`appsettings.json`**

Add Console and File entries to the Serilog `WriteTo` array alongside the existing
EventLog entry. The EventLog sink reference is already guarded by its package
condition; on Linux the runtime simply won't load it.

### No other project changes needed

`SaddleRAG.Database`, `SaddleRAG.Monitor`, `SaddleRAG.Core`, `SaddleRAG.Ingestion`,
and `SaddleRAG.Cli` are already cross-platform. `SaddleRAG.Installer` and
`SaddleRAG.Installer.Logic` remain Windows-only and are excluded from Linux builds.

### ONNX model path

`OnnxSettings.DefaultModelsDir` resolves to a `CommonApplicationData` path that maps
poorly inside Docker. Override via environment variable:

```
SADDLERAG__Onnx__ModelsDir=/data/models
```

In Docker this path is a named volume. In bare-metal installs it is
`/var/lib/saddlerag/models/`. No code change required — standard ASP.NET Core
config binding handles the override.

### Build flag

CPU-only builds use:

```
dotnet publish ... -p:UseGpu=false
```

This swaps `Microsoft.ML.OnnxRuntime.DirectML` for the cross-platform CPU package.
No runtime code changes required.

---

## Section 2 — Docker Image and Compose

### Warmup philosophy

Models are not bundled in the image and are not auto-pulled at container start.
The user explicitly runs a prewarm step — identical in principle to the MSI custom
action that calls `SaddleRAG.Mcp.exe --prewarm` before the Windows service starts.

### `--prewarm` flag (existing, cross-platform)

`Program.cs` already implements `--prewarm`: starts the full ASP.NET Core host,
runs `McpWarmupService` to completion (downloading ONNX models from HuggingFace
and bootstrapping Ollama models), then stops. The host uses a 660-second timeout
when ONNX is enabled to accommodate first-install downloads.

**Gap to close during implementation:** `OllamaBootstrapper.BootstrapAsync` currently
only pulls models referenced by existing libraries. On a fresh install with no
libraries it pulls nothing. The bootstrapper must also ensure the configured default
classification model (`Ollama.ClassificationModels[0]`, currently `phi4-mini:3.8b`)
is present regardless of library state.

### Warmup sequence (same on Windows and Linux)

```
[Warmup] 1. MongoDB profiles discovered
[Warmup] 2. Ollama bootstrap      — pulls required Ollama models if absent
[Warmup] 3. ONNX models ready     — downloads from HuggingFace if absent
                                     ← first-run pause point (200–500 MB)
[Warmup] 4. Vector indices loaded
[Warmup] 5. Embedding provider warm
[Warmup] 6. Ollama generate models warm
[Warmup] 7. Full pipeline warm (embed + vector search)
[Warmup] 8. ReRanker session warm
```

On **Windows**: MSI runs prewarm before the service starts — models are on disk
before first service boot.

On **Linux bare-metal**: `install.sh` runs prewarm explicitly before registering
and starting the systemd unit.

On **Docker**: user runs `warmup.sh` after `docker compose up -d`. Container starts
in seconds; prewarm downloads happen when the user chooses to trigger them.

### Dockerfile (multi-stage)

**Stage 1 — build** (`mcr.microsoft.com/dotnet/sdk:10.0`):
- `dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj -c Release -r linux-x64 --self-contained true -p:UseGpu=false`
- Output to `/app/publish`

**Stage 2 — runtime** (`mcr.microsoft.com/dotnet/aspnet:10.0`, Debian bookworm-slim):
- Install Playwright system dependencies and Chromium via `playwright install chromium --with-deps`
- Chromium is baked in — the scraper works immediately with no post-start browser download
- Image size estimate: ~700–900 MB (acceptable for a self-hosted server)
- Copy publish output from Stage 1
- `EXPOSE 6100`
- `HEALTHCHECK --interval=30s --timeout=10s --start-period=5m` against `/health`
- `ENTRYPOINT ["./SaddleRAG.Mcp"]`

### `docker-compose.yml`

Three services:

| Service | Image | Volumes | Notes |
|---------|-------|---------|-------|
| `saddlerag` | `ghcr.io/jackalopetechnologies/saddlerag:latest` | `onnx-models:/data/models` | All config via env vars |
| `mongo` | `mongo:8` | `mongo-data:/data/db` | No auth by default; production users should add auth |
| `ollama` | `ollama/ollama:latest` | `ollama-data:/root/.ollama` | Plain `ollama serve`; models pulled by prewarm |

Environment variables baked into compose (no user configuration required):

```yaml
SADDLERAG__Onnx__Enabled: "true"
SADDLERAG__Onnx__EmbeddingEnabled: "true"
SADDLERAG__Onnx__ModelsDir: /data/models
SADDLERAG__Ollama__Endpoint: http://ollama:11434
SADDLERAG__MongoDB__Profiles__local__ConnectionString: mongodb://mongo:27017
```

All three endpoints remain overridable — power users can remove any service from
the compose file and point at their own instance via these variables.

Three named volumes: `mongo-data`, `onnx-models`, `ollama-data`. Data survives
`docker compose down`. `docker compose down -v` permanently deletes all data and
models — documented with an explicit warning.

### `warmup.sh`

Shipped alongside `docker-compose.yml`. Single explicit step the user runs once:

```bash
#!/usr/bin/env bash
echo "Downloading ONNX models and Ollama models (first run only)."
echo "This may take several minutes depending on your connection."
echo "Models are stored in named volumes and will not be re-downloaded on restart."
docker compose exec saddlerag ./SaddleRAG.Mcp --prewarm
```

### Ollama models

| Model | Size | Purpose | When pulled |
|-------|------|---------|------------|
| `phi4-mini:3.8b` | ~2.5 GB | Page classification (scraper quality) | During prewarm |
| `phi4:14b` | ~8 GB | CLI recon | User opt-in: `ollama pull phi4:14b` |

The 14B recon model is documented as optional. It is not pulled during prewarm.

---

## Section 3 — Bare-Metal Install Script (`install.sh`)

Target distributions: Ubuntu 22.04/24.04 (apt) and Rocky Linux 9 / RHEL 9 (dnf).
Detection: `command -v apt-get` → apt path; `command -v dnf` → dnf path; else exit.

### Phase 1 — Pre-flight

- Verify running as root/sudo
- Detect and record distro family (apt vs dnf)
- Confirm x86-64 architecture
- Detect existing installation; offer upgrade vs fresh install

### Phase 2 — System dependencies

- **.NET ASP.NET Core Runtime 10.0** — runtime-only (no SDK); via Microsoft's
  official package repos (distro-specific setup script)
- **MongoDB 8.0** — via official MongoDB repos; package name `mongodb-org` on both
  distro families
- **Ollama** — via Ollama's official `curl | sh` install script (already
  distro-agnostic); registered as a systemd service by the Ollama installer

### Phase 3 — Install SaddleRAG

- Create system user `saddlerag` (no login shell, no home directory)
- Create directories:
  - `/opt/saddlerag/` — binaries
  - `/etc/saddlerag/` — configuration
  - `/var/lib/saddlerag/models/` — ONNX model cache (owned by `saddlerag`)
  - `/var/log/saddlerag/` — logs
- Fetch latest release tarball from GitHub API, verify SHA256 checksum, extract to
  `/opt/saddlerag/`
- Write `/etc/saddlerag/appsettings.json` with:
  - MongoDB connection string pointing at localhost
  - `Onnx.ModelsDir` pointing at `/var/lib/saddlerag/models/`
  - `Onnx.Enabled` and `Onnx.EmbeddingEnabled` set to `true`

### Phase 4 — Systemd registration

- Write `/etc/systemd/system/saddlerag.service`:
  - `User=saddlerag`
  - `Restart=on-failure`
  - `EnvironmentFile=/etc/saddlerag/env`
  - `ExecStart=/opt/saddlerag/SaddleRAG.Mcp`
- `systemctl daemon-reload && systemctl enable saddlerag`
- Ensure `mongod` and `ollama` systemd units are enabled and started

### Phase 5 — Explicit prewarm (mirrors MSI custom action)

Runs as the `saddlerag` user:

```bash
echo "Downloading ONNX models and Ollama models..."
echo "This is a one-time step. Models are stored in /var/lib/saddlerag/models/"
echo "This may take several minutes depending on your connection speed."
sudo -u saddlerag /opt/saddlerag/SaddleRAG.Mcp --prewarm
```

### Phase 6 — Start and verify

- `systemctl start saddlerag`
- Poll `http://localhost:6100/health` for up to 60 seconds
- Exit with error if health check never passes

### Phase 7 — Summary

```
SaddleRAG installed successfully.

  Server:    http://localhost:6100
  Logs:      journalctl -u saddlerag -f
  Config:    /etc/saddlerag/appsettings.json
  Models:    /var/lib/saddlerag/models/
  Uninstall: sudo /opt/saddlerag/uninstall.sh
```

### `uninstall.sh`

Reverses the install cleanly:
- `systemctl stop saddlerag && systemctl disable saddlerag`
- Remove systemd unit, reload daemon
- Remove `/opt/saddlerag/`, `/etc/saddlerag/`, `/var/log/saddlerag/`
- Prompt before removing `/var/lib/saddlerag/models/` (preserves downloaded models
  by default — re-download is expensive)
- Remove `saddlerag` system user
- Does not uninstall MongoDB, Ollama, or .NET runtime (user installed these
  independently and may use them for other purposes)

---

## Section 4 — CI Pipeline Additions

### Restructure

Rename existing `build` job to `build-windows` (no behavior change).

### New job: `build-linux`

Runs on `ubuntu-latest` in parallel with `build-windows`:

1. Setup .NET 10.x
2. `dotnet restore SaddleRAG.slnx`
3. `dotnet build SaddleRAG.slnx -c Release -p:UseGpu=false -p:TreatWarningsAsErrors=true -p:Version={version}`
4. `dotnet test SaddleRAG.slnx -c Release --no-build --filter "Category!=Integration"`
5. `dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj -c Release -r linux-x64 --self-contained true -p:UseGpu=false -p:Version={version} -o ./artifacts/{version}/linux`
6. Package: `SaddleRAG.Mcp-{version}-linux-x64.tar.gz` + `SaddleRAG.Mcp-{version}-linux-x64.tar.gz.sha256`
7. Upload tarball + checksum as workflow artifact
8. On tag: `gh release upload v{version} <tarball> <checksum>` — requires `needs: build-windows` so the release exists first

### New job: `docker`

Runs on `ubuntu-latest`, `needs: build-linux`:

1. Download the linux artifact from `build-linux`
2. `docker build` using the published output (no re-compile)
3. On PR / push to master: build only, do not push (validates Dockerfile)
4. On tag:
   - Log in to `ghcr.io` using `GITHUB_TOKEN`
   - Push `ghcr.io/jackalopetechnologies/saddlerag:{version}`
   - Push `ghcr.io/jackalopetechnologies/saddlerag:latest`

### Permissions addition

```yaml
permissions:
  contents: write    # existing
  packages: write    # new — required for ghcr.io push
```

### Job dependency graph

```
build-windows ──┬── (on tag) creates GitHub Release draft
                │
build-linux   ──┼── (on tag) uploads linux tarball + checksum to Release
                │
                └──▶ docker ── (on tag) pushes to ghcr.io
```

Wall-clock time on a tag push is similar to today — Linux build and Docker push
run while Windows is compiling and building the MSI.

---

## Files to create / modify

| File | Action |
|------|--------|
| `SaddleRAG.Mcp/SaddleRAG.Mcp.csproj` | Modify — conditional package refs |
| `SaddleRAG.Mcp/Program.cs` | Modify — OS guards (~20 lines) |
| `SaddleRAG.Mcp/appsettings.json` | Modify — add Console + File Serilog sinks |
| `SaddleRAG.Ingestion/Embedding/OllamaBootstrapper.cs` | Modify — ensure default classification model is pulled during prewarm even with no libraries |
| `Dockerfile` | Create |
| `docker-compose.yml` | Create |
| `warmup.sh` | Create |
| `install.sh` | Create |
| `uninstall.sh` | Create |
| `.dockerignore` | Create |
| `.github/workflows/build.yml` | Modify — rename job, add `build-linux` and `docker` jobs, add `packages: write` permission |
| `README.md` | Modify — add Linux / Docker install section with warmup documentation |
