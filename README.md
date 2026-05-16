# SaddleRAG

**Documentation Retrieval-Augmented Generation for AI coding assistants.**

SaddleRAG scrapes documentation websites, classifies and chunks the content with a local LLM, generates vector embeddings, and stores everything in MongoDB. It exposes the indexed documentation through MCP (Model Context Protocol) tools so that AI assistants like Claude Code, GitHub Copilot, and others can search your documentation library in real time.

## Why SaddleRAG?

AI coding assistants are limited by their training cutoff and context window. When you're working with a niche library, a new release, or internal documentation, the assistant doesn't know about it. SaddleRAG bridges that gap:

- **Scrape any documentation site** into a searchable vector database
- **Auto-index project dependencies** from NuGet, npm, and pip
- **Serve documentation to your AI assistant** via MCP tools during coding sessions
- **Track multiple versions** of the same library and diff changes between them
- **Share a company-wide documentation database** across your team

## Architecture

```
Documentation Sites          SaddleRAG Pipeline                    AI Assistants

docs.example.com  --+
                    |      +-------------+
github.com/repo   --+-->   |  Playwright  |  (headless browser)
                    |      |   Crawler    |
learn.microsoft   --+      +------+------+
                                  |
                           +------v------+
                           |   Ollama    |  (local LLM)
                           |  Classifier |  phi4-mini:3.8b
                           +------+------+
                                  |
                           +------v------+
                           |  Category-  |
                           |   Aware     |
                           |  Chunker    |
                           +------+------+
                                  |
                           +------v------+
                           |   Ollama    |  nomic-embed-text
                           |  Embedder   |  (768 dimensions)
                           +------+------+
                                  |
                           +------v------+     +--------------+
                           |   MongoDB   |<--->|  MCP Server  |--> Claude Code
                           |  (storage)  |     |   (HTTP)     |--> Copilot
                           +-------------+     +--------------+--> Any MCP client
```

## Quick Start (Windows Installer)

The fastest way to get SaddleRAG running is the MSI installer from [GitHub Releases](https://github.com/JackalopeTechnologies/saddlerag/releases). It installs SaddleRAG as a Windows service, configures connections to MongoDB and Ollama, and starts automatically.

SaddleRAG requires two free, open-source tools as prerequisites. Both are available as community editions at no cost.

### Step 1: Install MongoDB Community Edition (free)

MongoDB stores all scraped documentation, chunks, and vector embeddings.

1. Download the **Community Edition** from [mongodb.com/try/download/community](https://www.mongodb.com/try/download/community)
2. Run the installer, choose **Complete** setup type
3. Keep the default settings: **port 27017**, **Run as a Service** checked
4. After install, verify it's running: open a terminal and run `mongosh` -- you should see a connection prompt

> **Using Docker or a remote server?** No problem. The SaddleRAG installer lets you enter any MongoDB connection string (e.g. `mongodb://your-server:27017`). You can also run MongoDB in Docker: `docker run -d -p 27017:27017 --name saddlerag-mongo mongo:latest`

### Step 2: Install Ollama (free)

Ollama runs AI models locally for document classification and embedding generation. No API keys or cloud accounts needed.

1. Download from [ollama.com](https://ollama.com)
2. Run the installer -- Ollama runs as a background service on **port 11434**
3. After install, verify it's running: open a terminal and run `ollama list`

SaddleRAG automatically pulls the required models on first use:
- `nomic-embed-text` -- generates vector embeddings (768 dimensions)
- `phi4-mini:3.8b` -- classifies documentation pages and optional re-ranking

> **Running Ollama elsewhere?** The SaddleRAG installer lets you point to any Ollama endpoint (e.g. `http://your-gpu-server:11434`).

### Step 3: Install SaddleRAG

1. Download `SaddleRAG.Mcp-*.msi` from the [latest release](https://github.com/JackalopeTechnologies/saddlerag/releases/latest)
2. Run the installer
3. **MongoDB Configuration** -- the installer defaults to `mongodb://localhost:27017` with database `SaddleRAG`. Use the **Test Connection** button to verify MongoDB is reachable. If your MongoDB is on a different host, enter the connection string. **Reset to Local Defaults** reverts to the standard local settings.
4. **Ollama Configuration** -- defaults to `http://localhost:11434`. Use **Test Connection** to verify. Change only if Ollama is running on another machine.
5. Click **Install** -- files are copied to `Program Files\SaddleRAG\SaddleRAG.Mcp`, your connection settings are written to `appsettings.json`, and the **SaddleRAGMcp** Windows service starts automatically.

> **Don't have the prerequisites yet?** The installer includes **Download** buttons on each configuration page that open your browser to the MongoDB and Ollama download pages. Install them, then click **Test Connection** to verify before proceeding.

### Step 4: Connect Your AI Assistant

The MSI installer wires SaddleRAG into all your installed AI tools automatically:

- **Claude Code** — adds a user-level MCP server entry
- **Claude Desktop** — adds a server entry to `claude_desktop_config.json`
- **VSCode (GitHub Copilot Chat MCP)** — adds a server entry to VS Code user settings
- **GitHub Copilot CLI** — adds a server entry to the Copilot CLI MCP config

No manual `.mcp.json` editing required. If a tool is not installed, its registration is skipped silently.

### Step 5: Verify

Open your AI assistant and ask it to list libraries:

> "Use the list_libraries tool to show what documentation is indexed."

If SaddleRAG is running, you'll get an empty list (nothing indexed yet). Then try:

> "Scrape the documentation at https://docs.example.com for me."

The assistant will use the `scrape_docs` tool to index the site.

### Verify the Service

- **Health check**: visit `http://localhost:6100/health` in a browser
- **Service status**: run `Get-Service SaddleRAGMcp` in PowerShell
- **Logs**: check `%ProgramData%\SaddleRAG\logs\` or use the `get_server_logs` MCP tool

## Quick Start (Developer / Build from Source)

If you want to build and run from source instead of the MSI:

### Prerequisites

| Dependency | Version | Purpose |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0+ | Build and run |
| [MongoDB](https://www.mongodb.com/try/download/community) | 6.0+ | Document storage (port 27017) |
| [Ollama](https://ollama.com) | Latest | Local LLM for embeddings (port 11434) |

### Build and Run

```bash
git clone https://github.com/JackalopeTechnologies/saddlerag.git
cd SaddleRAG
dotnet build SaddleRAG.slnx
dotnet run --project SaddleRAG.Mcp
```

The server starts on `http://localhost:6100` by default. Configuration is in `SaddleRAG.Mcp/appsettings.Development.json`.

### Running Tests with Coverage

The repo has a pinned `dotnet-reportgenerator-globaltool` in `.config/dotnet-tools.json` and two helper scripts that wrap `dotnet test --collect:"XPlat Code Coverage"` and produce an HTML report.

```powershell
# Windows
scripts/coverage.ps1
```

```bash
# Linux / macOS
scripts/coverage.sh
```

Both scripts:

1. Restore the local tool manifest (`reportgenerator`)
2. Run the test project with coverage collection into `./coverage-results`
3. Generate an HTML report in `./coverage-results/html/index.html`, print the text summary, and (by default) open it in your browser
4. Exit 0 on a successful test run — no coverage gate is enforced

Pass `--no-open` (bash) or `-NoOpen` (PowerShell) to skip the browser launch; pass `--filter <expr>` / `-Filter <expr>` to override the default `Category!=Integration` xUnit filter.

CI collects coverage from two jobs — `build-linux` (unit) and `integration-test-linux` (Mongo / Playwright / ONNX integration) — and merges them in a `coverage-report` job. The merged summary is rendered on the workflow run page (via `$GITHUB_STEP_SUMMARY`) and posted as a sticky comment on each PR; the full cobertura XML and HTML drill-down are uploaded as a workflow artifact for download.

### Connect Your AI Assistant

Add to `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 60
    }
  }
}
```

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

SaddleRAG downloads models on first run, not at image build time. This applies
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

## Managing AI Client Registrations

The `SaddleRAG.Cli` tool manages which AI tools SaddleRAG is wired into.

### Check registration status

```bash
SaddleRAG.Cli clients-status
```

Reports whether SaddleRAG is registered in each supported tool, with config file paths and any errors.

### Re-register after adding a new tool

```bash
SaddleRAG.Cli register-clients
```

Registers SaddleRAG in all installed AI tools. Safe to run multiple times — existing entries are updated in place.

### Disable SaddleRAG for specific tools

```bash
SaddleRAG.Cli unregister-clients --claude-desktop=true --claude-code=false
```

Each flag controls one tool. Omitted flags default to `true` (remove from that tool). The example above removes SaddleRAG from Claude Desktop but leaves Claude Code wired.

## MCP Tools Reference

SaddleRAG exposes 33 tools through the MCP protocol. Six load eagerly into every session; the rest are deferred and pulled in by ToolSearch when needed.

### Entry-point tools (eager — in every session)

| Tool | Description |
|---|---|
| `get_dashboard_index` | Start here in any fresh session. Returns a single-call status overview: library/version counts, recent scrape jobs, server health |
| `list_libraries` | List all indexed libraries with current version and all ingested versions |
| `search_docs` | Natural language search across all libraries or filtered by library, version, and category |
| `get_class_reference` | Look up API reference for a class or type by name — exact match, then fuzzy |
| `get_library_overview` | Get Overview-category chunks for a library: concepts, architecture, getting-started guides |
| `list_symbols` | List documented symbols for a library, optionally filtered by kind (class, enum, function, parameter) |

### Ingestion

| Tool | Description |
|---|---|
| `start_ingest` | Single ingestion entry point — inspects (library, version) state and returns the next recommended action |
| `scrape_docs` | Scrape a documentation URL with auto-derived crawl settings. Cache-aware: skips already-indexed libraries unless `force=true`. Use for first-time ingest or URL/pattern overrides |
| `rescrape_library` | Re-scrape an already-indexed library from its source. Takes library + version only — reuses the prior scrape's config and seeds the crawler from stored page URLs so dead/changed/new pages are all picked up |
| `dryrun_scrape` | Test a scrape configuration without writing to the database. Reports page counts, depth distribution, and GitHub repos that would be cloned |
| `index_project_dependencies` | Scan a project's NuGet/npm/pip dependencies and auto-index their documentation |

### Job management

| Tool | Description |
|---|---|
| `get_scrape_status` | Poll a scrape job's progress by job ID |
| `list_scrape_jobs` | List recent scrape jobs with status, most recent first |
| `cancel_scrape` | Cancel a running scrape job |

### Library & pages

| Tool | Description |
|---|---|
| `list_pages` | List the URLs of every page indexed for a (library, version) — useful for auditing scrape completeness |
| `add_page` | Fetch a single URL and add it to an existing (library, version) index without re-crawling |

### Version management

| Tool | Description |
|---|---|
| `get_version_changes` | Diff two versions of a library — added, removed, and changed pages with summaries |

### Health

| Tool | Description |
|---|---|
| `get_library_health` | Per-version diagnostic snapshot: chunk count, hostname distribution, language mix, boundary-issue rate, suspect markers |

### Library administration

| Tool | Description |
|---|---|
| `rename_library` | Rename a library across every collection. Defaults to `dryRun=true` — preview before committing |
| `delete_version` | Hard-delete one (library, version) with all its chunks, pages, indexes, and profile. Defaults to `dryRun=true` |
| `delete_library` | Hard-delete an entire library across every collection. Defaults to `dryRun=true` |

### Index maintenance

| Tool | Description |
|---|---|
| `rechunk_library` | Re-run the chunker over stored pages, replace all chunks, and re-embed. Requires `reextract_library` as a follow-up |
| `reembed_library` | Re-embed every stored chunk via the current embedding provider; updates the version's provider/model/dimensions. Use after swapping embedding provider or model |
| `reextract_library` | Re-run the symbol extractor and classifier over existing chunks without re-crawling or re-embedding |
| `recon_library` | Get the instructions and JSON schema needed to characterize a docs site before scraping (LLM-assisted reconnaissance) |
| `submit_library_profile` | Submit the reconnaissance JSON produced by `recon_library` to persist it as the LibraryProfile |

### Symbol management

| Tool | Description |
|---|---|
| `list_excluded_symbols` | List symbols on the extraction stoplist for a library |
| `add_to_likely_symbols` | Add a symbol to the high-confidence list (overrides heuristic rejection) |
| `add_to_stoplist` | Add a symbol to the stoplist so it is excluded from future extraction passes |

### URL correction

| Tool | Description |
|---|---|
| `submit_url_correction` | Submit a corrected canonical URL for a page that was indexed under a redirect or wrong URL |

### Configuration

| Tool | Description |
|---|---|
| `list_profiles` | List all configured MongoDB database profiles |
| `reload_profile` | Reload the in-memory vector index from MongoDB (useful after manual data changes) |

### Settings

| Tool | Description |
|---|---|
| `set_rerank_strategy` | Set the reranker strategy at runtime: Off, Llm, or CrossEncoder |
| `toggle_logging` | Toggle verbose request logging without restarting the server |

### Diagnostics

| Tool | Description |
|---|---|
| `get_server_logs` | Retrieve recent server log lines, with optional text filter |

## CLI Tool

The CLI provides direct access to ingestion and management without the MCP server.

```bash
dotnet build SaddleRAG.Cli/SaddleRAG.Cli.csproj
```

### Commands

**Ingest a documentation library:**
```bash
saddlerag ingest \
  --root-url https://docs.example.com/ \
  --library-id example-lib \
  --version 2.0 \
  --hint "Example library for building widgets" \
  --allowed "docs.example.com" \
  --max-pages 500 \
  --delay 1000
```

**Dry-run a scrape (no database writes):**
```bash
saddlerag dryrun \
  --root-url https://docs.example.com/ \
  --allowed "docs.example.com" \
  --max-pages 200
```

**Inspect a page's link/sidebar structure (useful for tuning URL patterns):**
```bash
saddlerag inspect --url https://docs.example.com/getting-started
```

**List indexed libraries:**
```bash
saddlerag list
```

**Show ingestion status:**
```bash
saddlerag status --library-id example-lib
```

**Re-classify pages with the LLM (fix unclassified pages):**
```bash
saddlerag reclassify --library-id example-lib
saddlerag reclassify --all  # Reclassify everything, even already-classified pages
```

**Scan project dependencies and auto-index:**
```bash
saddlerag scan --path ./MyProject.sln
saddlerag scan --path ./package.json --profile company
```

**Manage database profiles:**
```bash
saddlerag profile list
```

## Configuration

### MongoDB Profiles

SaddleRAG supports multiple MongoDB databases via named profiles. Configure them in `appsettings.json`:

```json
{
  "MongoDB": {
    "ActiveProfile": "local",
    "Profiles": {
      "local": {
        "ConnectionString": "mongodb://localhost:27017",
        "DatabaseName": "SaddleRAG",
        "Description": "Local development database"
      },
      "company": {
        "ConnectionString": "mongodb://saddlerag.internal.company.com:27017",
        "DatabaseName": "SaddleRAG",
        "Description": "Shared company documentation database"
      }
    }
  }
}
```

Every MCP tool accepts an optional `profile` parameter to target a specific database. This enables scenarios like:
- Personal local index for experiments
- Shared team database with pre-indexed company libraries
- CI/CD pipeline that indexes docs on release

### Ollama Settings

```json
{
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "EmbeddingDimensions": 768,
    "ClassificationModel": "phi4-mini:3.8b",
    "ReRankingModel": "phi4-mini:3.8b",
    "ModelPullTimeoutSeconds": 600
  }
}
```

### Environment Variables

All settings can be overridden via environment variables prefixed with `SADDLERAG_`:

```bash
SADDLERAG_MONGODB_PROFILE=company          # Override active profile
ASPNETCORE_ENVIRONMENT=Development      # Enable dev settings (disables re-ranking)
```

## Troubleshooting

### SaddleRAG isn't visible in Claude Code / Claude Desktop / VSCode / Copilot

Run the diagnostics command:

```bash
SaddleRAG.Cli clients-status
```

Then re-register:

```bash
SaddleRAG.Cli register-clients
```

If registration succeeds but the tool still doesn't appear, restart the AI tool so it picks up the new config.

### I want to disable SaddleRAG for one specific tool

Use `unregister-clients` with explicit flags. Each flag controls one tool; unspecified tools are also unregistered by default, so be explicit:

```bash
# Remove from Claude Desktop only, leave everything else registered
SaddleRAG.Cli unregister-clients --claude-desktop=true --claude-code=false --vscode=false --copilot-cli=false
```

To re-enable a tool later, run `register-clients` (it re-wires all installed tools).

### The MCP server health check fails

Visit `http://localhost:6100/health`. If it returns an error, check the service:

```powershell
Get-Service SaddleRAGMcp
Start-Service SaddleRAGMcp
```

Logs are in `%ProgramData%\SaddleRAG\logs\`.

## Releasing

To create a new release with an MSI installer:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The CI pipeline builds the solution, runs tests, packages the MSI, and attaches it to a GitHub Release automatically.

## Project Structure

```
SaddleRAG.slnx                    # Solution file
SaddleRAG.Core/                   # Domain models, interfaces, enums
SaddleRAG.Database/               # MongoDB repositories and context factory
SaddleRAG.Ingestion/              # Scraping, classification, chunking, embedding pipeline
  Crawling/                    #   Playwright web crawler + GitHub repo scraper
  Classification/              #   Ollama LLM page classifier
  Chunking/                    #   Category-aware semantic chunker
  Embedding/                   #   Ollama embedding provider
  Symbols/                     #   Symbol extraction and stoplist management
  Recon/                       #   LLM-assisted library profiling (recon/reextract)
  Scanning/                    #   Project dependency scanner
  Ecosystems/                  #   NuGet, npm, pip registry clients
SaddleRAG.Mcp/                    # ASP.NET Core MCP server (HTTP transport)
  Tools/                       #   33 MCP tool definitions across 18 files
SaddleRAG.Cli/                    # Command-line interface (ingest, status, register-clients, ...)
SaddleRAG.Installer/              # WiX MSI installer definition
SaddleRAG.Tests/                  # Integration and unit tests
```

## License

SaddleRAG is dual-licensed:

- **Free for individual use** by a single natural person where the SaddleRAG instance serves only that person, under the [GNU Affero General Public License version 3 or later](./LICENSE).
- **Commercial license required** for multi-user deployments at $100 per Authorized User per year. See [COMMERCIAL-LICENSE.md](./COMMERCIAL-LICENSE.md) for full terms.

For commercial licensing inquiries: **[douglas@jackalopetechnologies.com](mailto:douglas@jackalopetechnologies.com)**

SaddleRAG was previously distributed under the MIT License under the name DocRAG. The project was renamed and relicensed in 2026; commits prior to the relicense remain available under MIT terms.

Contributions are welcome under the [Contributor License Agreement](./CLA.md). See [CONTRIBUTING.md](./CONTRIBUTING.md).
