# SaddleRAG Desktop Extension (.mcpb)

A [Claude Desktop Extension](https://github.com/anthropics/dxt) that registers the local
SaddleRAG MCP server with Claude Desktop.

## Why this exists

`SaddleRAG.ClientIntegration`'s `ClaudeDesktopWriter` registers SaddleRAG by writing an
`mcpServers` entry into `claude_desktop_config.json` (via the `mcp-remote` stdio bridge).
That works on standard Claude Desktop, but the newer (Cowork/Connectors-era) build **owns
that file and rewrites it, dropping externally-added `mcpServers` entries** — so the
registration does not persist. Its "Add custom connector" UI is no alternative for a local
server: it requires an **HTTPS** URL, and SaddleRAG serves plain HTTP on `localhost:6100`.

A Desktop Extension is the durable path: Claude Desktop installs and manages it in its own
extension store, so it survives the app's config rewrites. There is no HTTP/SSE transport in
the manifest schema, so the extension wraps the same local `mcp-remote` stdio bridge.

## Contents

- `manifest.json` — MCPB manifest (`manifest_version` 0.3). `version` is a placeholder that
  the pack script overwrites with the build version.
- `server/index.js` — unused stub; present only to satisfy the `entry_point` field. The
  server is launched via `mcp_config.command` (`npx mcp-remote …`).

## Build

```pwsh
pwsh scripts/pack-desktop-extension.ps1 -Version 1.3.4 -OutputDir ./artifacts/1.3.4
```

Produces `saddlerag-<version>.mcpb`. CI builds and uploads it alongside the MSI.

## Install (end user)

Double-click the `.mcpb`, or in Claude Desktop: **Settings → Extensions → Install from
file**. Requires Node.js (for `npx mcp-remote`) and a running SaddleRAG service on
`http://localhost:6100`.
