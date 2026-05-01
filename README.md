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
                           |  Classifier |  qwen3:1.7b
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
- `qwen3:1.7b` -- classifies documentation pages and optional re-ranking

> **Running Ollama elsewhere?** The SaddleRAG installer lets you point to any Ollama endpoint (e.g. `http://your-gpu-server:11434`).

### Step 3: Install SaddleRAG

1. Download `SaddleRAG.Mcp.msi` from the [latest release](https://github.com/JackalopeTechnologies/saddlerag/releases/latest)
2. Run the installer
3. **MongoDB Configuration** -- the installer defaults to `mongodb://localhost:27017` with database `SaddleRAG`. Use the **Test Connection** button to verify MongoDB is reachable. If your MongoDB is on a different host, enter the connection string. **Reset to Local Defaults** reverts to the standard local settings.
4. **Ollama Configuration** -- defaults to `http://localhost:11434`. Use **Test Connection** to verify. Change only if Ollama is running on another machine.
5. Click **Install** -- files are copied to `Program Files\SaddleRAG\SaddleRAG.Mcp`, your connection settings are written to `appsettings.json`, and the **SaddleRAGMcp** Windows service starts automatically.

> **Don't have the prerequisites yet?** The installer includes **Download** buttons on each configuration page that open your browser to the MongoDB and Ollama download pages. Install them, then click **Test Connection** to verify before proceeding.

### Step 4: Connect Your AI Assistant

Add this to your MCP client configuration. For **Claude Code**, create a `.mcp.json` file in your project root or home directory:

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

Or install the [Claude Code plugin](#claude-code-plugin) for automatic wiring and the `saddlerag-first` skill.

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

## Claude Code Plugin

The `plugin/` directory is a Claude Code plugin that wires SaddleRAG into every session automatically — no manual `.mcp.json` needed. It:

- Registers the SaddleRAG MCP server
- Bundles a `saddlerag-first` skill that tells Claude to query SaddleRAG before answering from training data on any coding question

### Install (local development)

```bash
claude --plugin-dir E:/GitHub/SaddleRAG/plugin
```

### Install (from git, once published)

```bash
claude plugin install https://github.com/JackalopeTechnologies/saddlerag --plugin-dir plugin
```

### Context efficiency

The plugin uses per-tool `[McpMeta("anthropic/alwaysLoad", true)]` flags so only the 6 entry-point tools occupy session-start context (~1–2k tokens). The other 27 admin/maintenance tools stay deferred behind ToolSearch and are loaded on demand.

Full plugin documentation: [plugin/README.md](plugin/README.md)

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
| `scrape_docs` | Scrape a documentation URL with auto-derived crawl settings. Cache-aware: skips already-indexed libraries unless `force=true` |
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
| `rechunk_library` | Re-run the chunker over stored pages, replace all chunks, and re-embed. Requires `rescrub_library` as a follow-up |
| `rescrub_library` | Re-run the symbol extractor and classifier over existing chunks without re-crawling or re-embedding |
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
| `toggle_reranking` | Enable or disable LLM re-ranking of search results at runtime |
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
    "ClassificationModel": "qwen3:1.7b",
    "ReRankingModel": "qwen3:1.7b",
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
  Recon/                       #   LLM-assisted library profiling (recon/rescrub)
  Scanning/                    #   Project dependency scanner
  Ecosystems/                  #   NuGet, npm, pip registry clients
SaddleRAG.Mcp/                    # ASP.NET Core MCP server (HTTP transport)
  Tools/                       #   33 MCP tool definitions across 18 files
SaddleRAG.Cli/                    # Command-line interface (8 subcommands)
SaddleRAG.Installer/              # WiX MSI installer definition
SaddleRAG.Tests/                  # Integration and unit tests
plugin/                           # Claude Code plugin
  .mcp.json                    #   MCP server registration
  skills/saddlerag-first/      #   Skill: query SaddleRAG before answering from training data
```

## License

SaddleRAG is dual-licensed:

- **Free for individual use** by a single natural person where the SaddleRAG instance serves only that person, under the [GNU Affero General Public License version 3 or later](./LICENSE).
- **Commercial license required** for multi-user deployments at $100 per Authorized User per year. See [COMMERCIAL-LICENSE.md](./COMMERCIAL-LICENSE.md) for full terms.

For commercial licensing inquiries: **[douglas@jackalopetechnologies.com](mailto:douglas@jackalopetechnologies.com)**

SaddleRAG was previously distributed under the MIT License under the name DocRAG. The project was renamed and relicensed in 2026; commits prior to the relicense remain available under MIT terms.

Contributions are welcome under the [Contributor License Agreement](./CLA.md). See [CONTRIBUTING.md](./CONTRIBUTING.md).
