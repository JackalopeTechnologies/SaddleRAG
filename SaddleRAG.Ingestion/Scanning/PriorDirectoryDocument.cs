// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Scanning;

internal sealed record PriorDirectoryDocument(DocumentRevisionRecord Revision,
                                              IReadOnlyList<PageRecord> Pages,
                                              IReadOnlyList<DocChunk> Chunks);
