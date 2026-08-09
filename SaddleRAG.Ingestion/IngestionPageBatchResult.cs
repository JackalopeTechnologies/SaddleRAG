// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion;

internal sealed record IngestionPageBatchResult(IReadOnlyList<PageRecord> Pages,
                                                IReadOnlyList<DocChunk> Chunks);
