// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Candidate document retained until the full pipeline succeeds.</summary>
internal sealed record PendingDirectoryDocument(SourceDocumentRecord Source,
                                                DocumentRevisionRecord Revision,
                                                DocumentIntakeResult Intake,
                                                IReadOnlyList<DocChunk> PriorChunks,
                                                bool ReusedExtraction);
