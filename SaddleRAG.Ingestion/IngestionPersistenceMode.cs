// IngestionPersistenceMode.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion;

/// <summary>
///     Controls whether the streaming ingestion stages write to MongoDB.
///     <see cref="Full" /> is the real-scrape behavior: PageRepository,
///     ChunkRepository, vector index, BM25 index, library metadata — all
///     written. <see cref="DryRun" /> exercises crawl + classify + chunk +
///     embed but skips every Upsert call so the run produces no persisted
///     state. The orchestrator additionally omits IndexStage and the
///     IngestionFinalizer in DryRun mode.
/// </summary>
internal enum IngestionPersistenceMode
{
    Full,
    DryRun
}
