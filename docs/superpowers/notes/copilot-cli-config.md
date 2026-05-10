# GitHub Copilot CLI — config layout (Windows, as of 2026-05-10)

## CLI inspected on this machine

- `copilot --version`: not installed (not found on PATH)
- `gh --version`: gh version 2.89.0 (2026-03-26)
- `gh extension list`: (empty — no extensions installed)
- Per-user dirs probed:
  - `%LOCALAPPDATA%\github-copilot` — **found** (IDE extension cache only: SQLite DBs for symbol lookup, `versions.json`; no CLI config)
  - `%APPDATA%\github-copilot` — missing
  - `%USERPROFILE%\.copilot` — **found** (IDE extension only: `ide/` with `.lock` files; no `mcp-config.json` or `skills/`)
  - `%USERPROFILE%\.config\github-copilot` — missing

**Conclusion:** The GitHub Copilot **IDE extension** (Visual Studio / VS Code) has written some local cache files, but neither the standalone `copilot` CLI binary nor the `gh-copilot` extension is installed. None of the CLI-specific config files (`mcp-config.json`, `settings.json`, `skills/`, `agents/`) are present.

---

## Copilot CLI MCP support — web research findings

The standalone `copilot` CLI (agentic flavor, shipped in late 2025, GA install via `winget install GitHub.Copilot`) has **documented, first-class MCP support**. This is not an experimental flag — it is presented as a standard capability in the official GitHub Docs as of early 2026. Key evidence:

- Official GitHub Docs page "Adding MCP servers for GitHub Copilot CLI" exists and documents the feature without any "preview" disclaimer.
- The `copilot-cli-for-beginners` repository (official GitHub org) has a dedicated chapter (06) on MCP servers with working JSON examples.
- The DeepWiki analysis of the `github/copilot-cli` repo confirms Windows-specific paths and schema details.
- A January 2026 GitHub Changelog entry ("Enhanced agents, context management, and new ways to install") describes the feature as shipping with context management, custom agents, and MCP — all in the same release.

Agent skills (a parallel feature distinct from MCP) are also documented and appear stable, without a preview disclaimer.

Custom agents (`.agent.md` files) carry a "public preview" label for some IDE targets (JetBrains, Eclipse, Xcode) but the CLI configuration surface is presented as available.

---

## Config file path (if MCP supported)

**User-level (per-machine, cross-project):**

```
%USERPROFILE%\.copilot\mcp-config.json
```

On Windows, `~` resolves to `%USERPROFILE%` (e.g., `C:\Users\DougGerard\.copilot\mcp-config.json`). The docs consistently use `~/.copilot/mcp-config.json` notation; the DeepWiki source explicitly calls out `%USERPROFILE%\.copilot\mcp-config.json` for Windows.

**Workspace-level (per-repo, checked in or local):**

The CLI discovers MCP servers from these locations, searched from CWD up to the git root:

```
.mcp.json                  (project root)
.vscode/mcp.json           (VS Code compat)
devcontainer.json          (devcontainer compat)
```

**COPILOT_HOME override:**

Setting `COPILOT_HOME` in the environment redirects the default config directory away from `%USERPROFILE%\.copilot`. All CLI config files (`mcp-config.json`, `settings.json`, `agents/`, `skills/`) move under the new root.

---

## Schema (if MCP supported)

The top-level key is `mcpServers`. Each entry is keyed by a logical server name.

```json
{
  "mcpServers": {
    "filesystem": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "."],
      "tools": ["*"]
    },
    "my-remote-server": {
      "type": "sse",
      "url": "https://example.com/mcp",
      "headers": {
        "Authorization": "Bearer ${MY_TOKEN}"
      },
      "tools": ["tool-a", "tool-b"]
    }
  }
}
```

**Field reference:**

| Field | Type | Required | Notes |
|---|---|---|---|
| `type` | string | yes | `"stdio"` (local child process), `"sse"` (HTTP SSE), `"remote"` (OAuth remote) |
| `command` | string | for stdio | Path to executable; supports `~` and `%ENV_VAR%` expansion |
| `args` | string[] | for stdio | Arguments to the executable |
| `url` | string | for sse/remote | HTTP(S) endpoint |
| `headers` | object | optional | HTTP headers; values support `${VAR}` env substitution |
| `env` | object | optional | Extra environment variables injected into the child process |
| `cwd` | string | optional | Working directory for stdio servers; supports tilde expansion |
| `instructions` | string | optional | System instructions prepended for this server's tools |
| `tools` | string[] | optional | Whitelist of tool names; `["*"]` enables all |

The format is documented but **not versioned with a `$schema` field**. It shares structural similarity with the VS Code `mcp.json` format but is not identical (VS Code uses `"inputs"` for secrets; Copilot CLI uses `env` and `${VAR}` in `headers`).

---

## Skills / custom commands

**Skills** are Markdown files with YAML frontmatter that the Copilot CLI loads as behavioral instructions when relevant.

**User-level skills (cross-project):**

```
%USERPROFILE%\.copilot\skills\<skill-name>\SKILL.md
```

**Project-level skills (per-repo):**

```
<repo-root>\.github\skills\<skill-name>\SKILL.md
<repo-root>\.agents\skills\<skill-name>\SKILL.md
<repo-root>\.claude\skills\<skill-name>\SKILL.md   (alternate discovery path)
```

**`SKILL.md` frontmatter schema:**

```yaml
---
name: my-skill          # required; lowercase, hyphens for spaces
description: |          # required; tells Copilot when to apply the skill
  Used when working with ...
license: MIT            # optional
allowed-tools:          # optional; pre-approve specific tools
  - shell
---
```

The Markdown body contains the skill's instructions, examples, and guidelines.

**Custom agents** (`.agent.md` files) live under:

```
%USERPROFILE%\.copilot\agents\<agent-name>.agent.md   (user-level)
<repo-root>\.copilot\agents\<agent-name>.agent.md     (project-level)
```

Custom agents are "public preview" as of early 2026, primarily for IDE surfaces; the CLI surface appears further along.

**Note:** The Copilot CLI explicitly does NOT auto-load Claude Code skills from `~/.claude/` to prevent configuration leakage between tools.

---

## Recommendation for Task 9

**Variant A — real writer.**

MCP support in the GitHub Copilot CLI is documented, first-class, and stable enough to write against:

- The config path (`%USERPROFILE%\.copilot\mcp-config.json`) is well-defined and consistent across docs sources.
- The `mcpServers` JSON schema is stable and matches the broader MCP ecosystem pattern.
- The `COPILOT_HOME` env var provides a clean override mechanism if the user has a non-standard install.
- The feature is not marked experimental or preview in the CLI docs.

**Recommended writer implementation for Task 9:**

1. Resolve the config file path: `%USERPROFILE%\.copilot\mcp-config.json` (or `$COPILOT_HOME\mcp-config.json` if that env var is set).
2. Read/parse the existing file if present, or start with `{ "mcpServers": {} }`.
3. Merge the SaddleRAG MCP entry under `mcpServers` using the stdio schema.
4. Write the file back.
5. Status: "ok" with the resolved path.

The one edge case to handle: the `.copilot/` directory itself may not exist on a machine with only the IDE extension (as on this machine). The writer must `Directory.CreateDirectory` before writing.

---

## Risks / open questions

1. **Schema drift:** The `mcpServers` format has no `$schema` version field. If GitHub changes field names (e.g., renames `"stdio"` to `"local"` — the `copilot-cli-for-beginners` README uses `"local"` while the DeepWiki source uses `"stdio"`), existing configs may silently fail. The writer should document which type string it emits and why.

2. **`"local"` vs. `"stdio"` type string:** Two sources disagree. The beginners' tutorial uses `"type": "local"`; the DeepWiki schema analysis uses `"type": "stdio"`. The official GitHub Docs add-MCP page should be treated as authoritative but the fetched content was ambiguous. **Recommend using `"stdio"` as it matches the MCP specification vocabulary**, but verify against the live docs page before finalizing Task 9 code.

3. **Workspace vs. user level:** The CLI discovers `.mcp.json` at the project root too. If SaddleRAG already has a `.mcp.json`, the writer must decide whether to write there (project-scoped) or to the user config (cross-project). The plan should specify which is preferred.

4. **Windows path resolution:** The docs use `~/.copilot/mcp-config.json` uniformly. On Windows, `~` in .NET resolves via `Environment.GetFolderPath(SpecialFolder.UserProfile)`, not `%USERPROFILE%` substitution. The writer should use the .NET API, not string replacement.

5. **Custom agents preview status:** Custom agents are "public preview" — avoid writing agents config in Task 9; keep it to MCP only until agents GA.

---

## Sources consulted

- [Adding MCP servers for GitHub Copilot CLI — GitHub Docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)
- [Adding agent skills for GitHub Copilot CLI — GitHub Docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)
- [Custom agents configuration reference — GitHub Docs](https://docs.github.com/en/copilot/reference/custom-agents-configuration)
- [Using GitHub Copilot CLI — GitHub Docs](https://docs.github.com/copilot/how-tos/use-copilot-agents/use-copilot-cli)
- [github-mcp-server install guide for Copilot CLI](https://github.com/github/github-mcp-server/blob/main/docs/installation-guides/install-copilot-cli.md)
- [MCP Server Configuration — DeepWiki (github/copilot-cli)](https://deepwiki.com/github/copilot-cli/5.3-mcp-server-configuration)
- [copilot-cli-for-beginners / 06-mcp-servers — GitHub](https://github.com/github/copilot-cli-for-beginners/blob/main/06-mcp-servers/README.md)
- [GitHub Copilot CLI Enhanced agents changelog — GitHub Blog (2026-01-14)](https://github.blog/changelog/2026-01-14-github-copilot-cli-enhanced-agents-context-management-and-new-ways-to-install/)
- [GitHub Copilot CLI Custom Agents changelog — GitHub Blog (2025-10-28)](https://github.blog/changelog/2025-10-28-github-copilot-cli-use-custom-agents-and-delegate-to-copilot-coding-agent/)
