// Not executed at runtime. The MCP server is launched via the manifest's
// server.mcp_config.command ("npx -y mcp-remote@latest http://localhost:6100/mcp
// --allow-http"), which bridges Claude Desktop's stdio transport to the local
// SaddleRAG HTTP server. This file exists only to satisfy the node entry_point
// field in manifest.json.
