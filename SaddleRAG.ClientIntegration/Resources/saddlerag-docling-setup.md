---
name: saddlerag-docling-setup
description: Guide a user through installing, configuring, validating, and recovering the optional user-managed Docling Serve document scanner used by SaddleRAG, and optionally connect the official Docling MCP server to the same endpoint. Use when PDF or DOCX ingestion reports a Docling error, when a user asks how to enable document scanning, or when a user explicitly asks to install or configure Docling Serve or Docling MCP.
---

# SaddleRAG Docling setup protocol

Treat Docling Serve and Docling MCP as user-owned optional software. SaddleRAG consumes a configured HTTP endpoint; it never installs, licenses, starts, stops, restarts, upgrades, or supervises either process.

## Preserve the ownership boundary

- Obtain explicit user authorization before installing packages, changing configuration, starting a process, creating startup persistence, or adding an MCP server to a client.
- State the proposed commands, target Python environment, endpoint, and persistence changes before execution.
- Do not interpret a request to scan documents, a failed scan, or an unavailable status as permission to install or control software.
- Keep Docling bound to localhost unless the user explicitly chooses remote access and understands the authentication, firewall, TLS, and document-data exposure implications.
- Never put credentials in the endpoint URL, logs, chat output, or committed settings. Configure an API key separately when the endpoint requires one.
- Let the user make the license decision. Docling Serve and Docling MCP code are MIT licensed, but models, model weights, OCR engines, and transitive packages can have their own licenses. Review the selected components rather than treating the code license as a blanket approval.

## Diagnose before changing anything

1. Call `get_document_ingestion_status(refresh=true)`.
2. Read `State`, `ReasonCode`, `Detail`, `Endpoint`, `LastCheckedAt`, and `Remediation` together. Do not replace the reported detail with a guess.
3. If the result is `DOCLING_READY`, do not reinstall or restart Docling. Continue with the requested manual document scan.
4. Call `get_docling_install_instructions` when setup or recovery documentation is needed. Use its configured endpoint and official links as the current SaddleRAG guidance.

The refreshed status is intentionally stronger than a port check. SaddleRAG performs bounded health, readiness/model, and known owned-document conversion checks. It does not submit the user's documents during this probe.

## Review official sources and licenses

Present these primary sources before an installation decision:

- Installation: https://github.com/docling-project/docling-serve#installation
- Releases: https://github.com/docling-project/docling-serve/releases
- API server: https://docling-project.github.io/docling/usage/api_server/
- Docling Serve license: https://github.com/docling-project/docling-serve/blob/main/LICENSE
- Model and OCR catalog: https://docling-project.github.io/docling/usage/model_catalog/

Explain that SaddleRAG is compatibility-tested with Docling Serve `1.29.0`; this is a compatibility statement, not a claim that it is the newest release or a license recommendation. If the user chooses a different release, verify it through SaddleRAG's conversion probe before scanning.

## Install Docling Serve only after authorization

Prefer a dedicated Python environment selected by the user. On Windows, SaddleRAG's compatibility-tested example uses Python 3.12 and Docling Serve 1.29.0:

```powershell
py -3.12 -m pip install "docling-serve[ui]==1.29.0"
$env:PYTHONUTF8 = "1"
$env:TORCH_COMPILE_DISABLE = "1"
docling-serve run
```

Set `PYTHONUTF8=1` and `TORCH_COMPILE_DISABLE=1` in the environment of the user-owned Docling process. Do not change machine-wide environment variables or create a startup task unless the user explicitly authorizes that separate change.

The default SaddleRAG endpoint is `http://localhost:5001`. Configure `DocumentIngestion:Docling:Endpoint` to the user-selected absolute HTTP or HTTPS root URL without embedded credentials, query, or fragment. Keep any API key in the separate Docling API-key setting.

Do not have SaddleRAG launch the command above. If the user authorizes agent assistance, run it directly in the user-selected environment, report the exact result, and leave process ownership with the user.

## Validate through SaddleRAG

1. Allow the user-managed process time to load its models.
2. Call `get_document_ingestion_status(refresh=true)` once.
3. Proceed only when it returns `State=Ready` and `ReasonCode=DOCLING_READY`.
4. On failure, follow the returned `Detail` and `Remediation`; inspect the user-managed Docling logs when requested.
5. Do not retry indefinitely, restart automatically, or mask a conversion failure with a simple `/health` success.

Use the stable reason code to keep recovery focused:

| Reason | Response |
|---|---|
| `DOCLING_NOT_CONFIGURED` or `DOCLING_INVALID_ENDPOINT` | Correct the SaddleRAG endpoint configuration, then refresh status. |
| `DOCLING_STARTING`, `DOCLING_MODELS_UNAVAILABLE`, or `DOCLING_ARTIFACTS_UNAVAILABLE` | Let the user-owned process finish loading or make its selected artifacts available, then refresh status. |
| `DOCLING_ENDPOINT_UNREACHABLE` or `DOCLING_HEALTH_TIMEOUT` | Check the process, address, firewall, and user-owned logs; do not start or restart it without authorization. |
| `DOCLING_UNAUTHORIZED` | Correct the separately configured API key without exposing it. |
| `DOCLING_API_INCOMPATIBLE` | Compare the installed release and `/docs` schema with SaddleRAG's tested v1 API contract. |
| Conversion, partial-output, or invalid-output codes | Use the reported conversion detail and Docling logs; a healthy port alone is not sufficient. |

## Optionally add the official Docling MCP server

Docling MCP is useful for direct agent document tools, but it is not required by SaddleRAG. SaddleRAG talks to Docling Serve through its REST API and does not call Docling MCP.

Only add Docling MCP when the user explicitly requests it. Before installing or editing an MCP client configuration, verify the current official instructions because package commands and client schemas can change:

- Official MCP guide: https://docling-project.github.io/docling/usage/mcp/
- Official repository and installation options: https://github.com/docling-project/docling-mcp
- Docling MCP license: https://github.com/docling-project/docling-mcp/blob/main/LICENSE

Prefer remote conversion mode pointed at the same user-managed Docling Serve endpoint. This avoids installing a second local conversion/model stack. Configure the MCP process environment using the current official client syntax:

```text
DOCLING_CONVERSION_MODE=remote
DOCLING_SERVICE_URL=http://localhost:5001
DOCLING_SERVICE_API_KEY=<only when the selected endpoint requires one>
```

Use the actual configured Serve URL instead of assuming localhost. Do not enable local fallback unless the user separately chooses the additional local packages, models, storage, and licenses. After configuration, verify the MCP server using the client's normal MCP discovery and one harmless owned test document; do not use private or production documents as probes.

Keep ownership separate even when both are configured: SaddleRAG reports and uses Docling Serve, the MCP client owns Docling MCP, and the user owns installation, licensing, process lifecycle, and startup behavior for both.
