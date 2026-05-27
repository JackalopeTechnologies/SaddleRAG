// IngestionPersistenceMode.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Ingestion;

/// <summary>
///     Controls whether the streaming ingestion stages write to MongoDB.
///     <see cref="Full" /> is the real-scrape behavior: PageRepository,
///     ChunkRepository, vector index, BM25 index, library metadata — all
///     written. <see cref="DryRun" /> exercises crawl + classify + chunk +
///     embed but skips every Upsert into the page / chunk / library
///     repositories. The audit log is still written so the dry-run path
///     produces the same audit timeline as a real scrape; the MCP layer
///     pre-deletes prior audit rows for the (library, version) before
///     each dry-run job. The orchestrator additionally omits IndexStage
///     and the IngestionFinalizer in DryRun mode.
/// </summary>
public enum IngestionPersistenceMode
{
    Full,
    DryRun
}
