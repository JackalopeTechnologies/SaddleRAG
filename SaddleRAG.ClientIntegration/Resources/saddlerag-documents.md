---
name: saddlerag-documents
description: Register and manually scan a local document directory into SaddleRAG, monitor document-ingestion jobs, recover from Docling capability errors, and search classified documents with source citations. Use when a user asks to index, classify, find, or cite local PDF, DOCX, Markdown, text, or HTML files, or when a registered document library needs another explicit scan.
---

# SaddleRAG local-document protocol

Treat local-directory ingestion as an explicit, manual workflow. Never invent a path, create a scan schedule, configure a filesystem watcher, or trigger a scan from service startup or folder changes.

## Supported files

Manual directory scans accept:

- PDF: `.pdf`
- Word Open XML: `.docx`
- Markdown: `.md`, `.markdown`
- Plain text: `.txt`, `.text`
- HTML: `.html`, `.htm`

PDF and DOCX extraction require the user-managed Docling service. Markdown, text, and HTML use SaddleRAG's local extractors and do not require Docling.

## Register, scan, and monitor

1. Obtain the absolute directory path explicitly from the user. Also choose a stable `library` identifier and confirm whether to include subdirectories. Do not guess or discover a broader path.
2. If the directory contains PDF or DOCX files, run a fresh bounded capability check:

   ```
   get_document_ingestion_status(refresh=true)
   ```

   Continue when the result reports that document ingestion is ready. Follow the recovery procedure below for any other state.
3. Register the user-selected root without scanning it:

   ```
   register_directory_library(library="<stable-id>", path="<absolute-user-path>", recursive=true)
   ```

   Registration validates and stores the binding. It does not queue work. Pass the same optional `profile` to every subsequent tool call when the user selects a non-default profile.
4. Queue exactly one manual scan of the registered root:

   ```
   scan_directory_library(library="<stable-id>")
   ```

   Do not pass a path to this tool; it scans only the previously registered binding. Capture its `Version` and `JobId`.
5. Poll the returned job at 10-30 second intervals:

   ```
   get_job_status(jobId="<job-id>")
   ```

   Report `DirectoryScanProgress`, including discovered files, supported documents, completed documents, and the current relative path. On `Failed`, report `ErrorMessage` and any sanitized per-file failures from the result. On `Completed`, report the published library and version.

Each queue request captures the machine's local calendar date as the version in `yyyy-MM-dd` form. A published scan for the same date returns `ALREADY_SCANNED_TODAY`; do not manufacture another version. A failed or cancelled same-date attempt may be retried explicitly.

## Recover PDF or DOCX capability

When preflight or a scan reports a Docling-related reason:

1. Refresh the observed state with `get_document_ingestion_status(refresh=true)`.
2. Preserve and report its exact `State`, `ReasonCode`, `Detail`, endpoint, and remediation. Do not replace them with a generic installation error.
3. Call `get_docling_install_instructions()` for the configured endpoint and official project/release links.
4. Explain that Docling is user-managed. SaddleRAG does not install, license, configure, start, stop, restart, or upgrade it.
5. If the user asks the LLM to help set up Docling, use `saddlerag:docling-setup`. After the user completes or authorizes those steps, verify again with `get_document_ingestion_status(refresh=true)` before retrying the manual scan.

Do not require Docling to scan a directory containing only Markdown, text, or HTML files.

## Stored documents and later source movement

Treat the published MongoDB/GridFS originals and extracted artifacts as the authoritative ingestion record. The BM25 and vector indexes are rebuildable projections of those stored records.

A published version remains searchable when a source file or the entire source directory later moves or disappears. Citations retain the document's normalized relative path and page or heading metadata without exposing the registered absolute root. A later manual rescan still needs a valid registered local root; if the root moved, obtain the new path explicitly and call `register_directory_library` again before scanning.

## Classification, search, and citations

The scan classifies documents by subject matter, chunks them, embeds them, and publishes both BM25 and vector indexes only after the candidate version is complete.

Search the published document library with:

```
search_docs(query="<document topic>", library="<stable-id>")
```

Use the optional `subject` parameter to filter by a subject id, label, or alias when the user asks for a specific subject. Otherwise allow SaddleRAG to infer a subject boost from the query. Drop the subject filter if the user wants cross-subject recall.

Base every answer on returned content and cite the returned `DocumentSource` metadata. Prefer `SourceUri` plus `RelativePath`; include `PageStart`-`PageEnd` or `Heading` when present. Never invent an absolute filesystem path, page number, subject, or quotation that is absent from the search result.

## Non-negotiable boundaries

- Require an explicit user-selected path for registration.
- Run scans only after an explicit `scan_directory_library` request.
- Keep registration separate from scanning.
- Do not create recurring scans, startup triggers, or filesystem watchers.
- Do not make SaddleRAG manage the Docling process or accept Docling licensing on the user's behalf.
- Do not expose the registered absolute root through job status, search results, packages, or citations.
